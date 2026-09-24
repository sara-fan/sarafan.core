// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

// Implemented by a trusted integration, never by a staff request body. Missing imports remain unknown.
public interface IAutomaticPriceSource
{
    Task<decimal?> GetAmountAsync(Order order, ServiceKind service, Currency currency, CancellationToken token);
}

internal static class OrderPriceCalculator
{
    internal static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    internal static void ValidateInputs(OrderPricingInputs inputs, ServiceCatalogueEntry[] tariffs)
    {
        if (inputs.ManualAmounts is null || inputs.SelectedServices is null
            || inputs.SelectedServices.Distinct().Count() != inputs.SelectedServices.Length
            || inputs.SelectedServices.Any(service => service is not (ServiceKind.WarehousePhoto or ServiceKind.ProductInspection or ServiceKind.ShipmentInsurance)))
            throw Invalid("selectedServices", "Выберите дополнительные услуги из списка.");
        foreach (var (service, amount) in inputs.ManualAmounts)
        {
            if (service is ServiceKind.Product or ServiceKind.DomesticDelivery || !Enum.IsDefined(service)
                || !tariffs.Any(tariff => tariff.Service == service && tariff.PriceMethod == PriceMethod.Manual))
                throw Invalid("manualAmounts", "Ручная сумма допустима только для услуги с действующим ручным тарифом. Автоматические суммы менять нельзя.");
            if (!ValidAmount(amount)) throw Invalid("manualAmounts", "Укажите неотрицательные суммы до 99999999,99 с двумя дробными знаками.");
        }
        if (inputs.DomesticDeliveryRub is { } domestic && !ValidAmount(domestic)) throw Invalid("domesticDeliveryRub", "Укажите неотрицательную сумму с двумя дробными знаками.");
        if (inputs.CustomsRub is { } customs && !ValidAmount(customs)) throw Invalid("customsRub", "Укажите неотрицательную сумму с двумя дробными знаками.");
    }

    private static bool ValidAmount(decimal amount) => amount >= 0 && amount <= ServiceCatalogueRules.MaximumAmount && Round(amount) == amount;
    private static ServiceException Invalid(string field, string message) => new(400, "invalid_order_pricing")
    { Errors = new Dictionary<string, string[]> { [field] = [message] } };

    internal static Task<ServiceCatalogueEntry[]> TariffsAsync(AppDbContext database, DateTimeOffset now, CancellationToken token)
    {
        var today = ConsentCalendar.LocalDate(now);
        return database.ServiceCatalogueEntries.AsNoTracking()
            .Where(row => (row.AvailableFrom == null || row.AvailableFrom <= today) && (row.AvailableBy == null || row.AvailableBy >= today))
            .OrderBy(row => row.Service).ToArrayAsync(token);
    }

    internal static async Task<OrderPriceCalculationDto> CalculateAsync(AppDbContext database, Order order,
        DateTimeOffset now, OrderPricingInputs inputs, IAutomaticPriceSource? automatic, CancellationToken token)
    {
        var today = ConsentCalendar.LocalDate(now);
        var rate = await database.ExchangeRateHistory.AsNoTracking()
            .Where(row => row.Provider == "CBR" && row.BaseCurrency == Currency.Usd && row.QuoteCurrency == Currency.Rub
                && row.Nominal > 0 && row.OfficialRate > 0 && row.SourceEffectiveDate <= today)
            .OrderByDescending(row => row.SourceEffectiveDate).ThenByDescending(row => row.Id).FirstOrDefaultAsync(token);
        var tariffs = await TariffsAsync(database, now, token);
        var usdRub = rate is null ? (decimal?)null : rate.OfficialRate / rate.Nominal;
        var merchandise = order.SellerPriceCurrency == Currency.Usd && order.SellerPrice > 0
            ? order.SellerPrice * order.Quantity : null;
        var components = new List<PriceComponentDto>();
        foreach (var service in Enum.GetValues<ServiceKind>())
        {
            var tariff = tariffs.SingleOrDefault(row => row.Service == service);
            var currency = service == ServiceKind.Product ? Currency.Usd
                : service == ServiceKind.DomesticDelivery ? Currency.Rub
                : tariff?.Currency ?? Currency.Rub;
            // Product and domestic delivery have dedicated Demo inputs, not catalogue-derived amounts.
            var snapshot = tariff is null || service is ServiceKind.Product or ServiceKind.DomesticDelivery
                ? null : ServiceCatalogueService.ToDto(tariff);
            if (service is ServiceKind.WarehousePhoto or ServiceKind.ProductInspection or ServiceKind.ShipmentInsurance
                && !inputs.SelectedServices.Contains(service))
            {
                components.Add(new(service, PriceComponentState.NotApplicable, currency, null, null, snapshot));
                continue;
            }
            decimal? amount;
            if (service == ServiceKind.Product) amount = merchandise;
            else if (service == ServiceKind.DomesticDelivery) amount = inputs.DomesticDeliveryRub;
            else if (tariff is null) amount = null;
            else
            {
                var basis = currency == Currency.Usd ? merchandise : merchandise * usdRub;
                amount = tariff.PriceMethod switch
                {
                    PriceMethod.Fixed => tariff.Amount,
                    PriceMethod.Manual => inputs.ManualAmounts.TryGetValue(service, out var manual) ? manual : null,
                    PriceMethod.Auto => automatic is null ? null : await automatic.GetAmountAsync(order, service, currency, token),
                    PriceMethod.Percent => Percentage(basis, tariff),
                    PriceMethod.Stepped => Step(tariff.IntervalCurrency == Currency.Usd ? merchandise : merchandise * usdRub, tariff.Bands),
                    _ => null
                };
            }
            // Retain enough precision for conversion; round the original and converted components independently.
            var rub = currency == Currency.Rub ? amount : amount * usdRub;
            if (amount is < 0 || amount > ServiceCatalogueRules.MaximumAmount || rub > ServiceCatalogueRules.MaximumAmount)
                amount = rub = null;
            components.Add(new(service, amount is not null && rub is not null ? PriceComponentState.Calculated : PriceComponentState.NotCalculated,
                currency, amount is null ? null : Round(amount.Value), rub is null ? null : Round(rub.Value), snapshot));
        }
        var included = components.Where(item => item.Service != ServiceKind.DomesticDelivery && item.State != PriceComponentState.NotApplicable).ToArray();
        decimal? total = included.All(item => item.State == PriceComponentState.Calculated) ? included.Sum(item => item.AmountRub!.Value) : null;
        return new(now, rate is null ? null : new(rate.Id, rate.Provider, rate.BaseCurrency, rate.QuoteCurrency, rate.Nominal, rate.OfficialRate, rate.SourceEffectiveDate),
            components.ToArray(), total, inputs);
    }

    internal static decimal? Percentage(decimal? basis, ServiceCatalogueEntry tariff)
    {
        if (basis is null) return null;
        var value = basis.Value * tariff.Percentage!.Value / 100m;
        if (tariff.MinimumAmount is { } min) value = Math.Max(value, min);
        if (tariff.MaximumAmount is { } max) value = Math.Min(value, max);
        return value;
    }

    internal static decimal? Step(decimal? basis, PriceBand[] bands)
        => basis is null ? null : bands.SingleOrDefault(band => (band.From is null || basis > band.From) && (band.By is null || basis <= band.By))?.Amount;
}
