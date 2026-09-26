// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

internal static class ServiceCatalogueRules
{
    internal const decimal MaximumAmount = 99999999.99m;
    internal const decimal MaximumPercentage = 100m;
    internal const int AmountDecimalPlaces = 2;
    internal const int PercentageDecimalPlaces = 4;
    internal const int AuditSearchMaxLength = 200;
    internal const int AuditPageSizeMaximum = 100;
    internal const int MaximumBands = 100;

    private static readonly Currency[] PricingCurrencies = [Currency.Rub, Currency.Usd];

    internal static PreparedServiceCatalogueEntry Prepare(ServiceCatalogueWriteRequest request, bool update)
    {
        if (request.Service is not { } service || !Enum.IsDefined(service))
            throw Invalid("invalid_service_catalogue_service", "service", "Выберите услугу из списка.");
        if (request.PriceMethod is not { } method || !Enum.IsDefined(method))
            throw Invalid("invalid_service_catalogue_method", "priceMethod", "Выберите способ расчёта из списка.");
        if (request.AvailableFrom is { } availableFrom && request.AvailableBy is { } availableBy && availableBy < availableFrom)
            throw Invalid("invalid_service_catalogue_dates", "availableBy", "Дата окончания не может быть раньше даты начала.");

        if (update)
        {
            RequireVersion(request.Version);
        }
        else if (request.Version is not null)
        {
            throw Invalid("invalid_service_catalogue_version", "version", "Не передавайте версию при создании тарифа.");
        }

        ValidateCurrency(request.Currency);
        if (method != PriceMethod.Stepped && (request.IntervalCurrency is not null || request.Bands is { Length: > 0 }))
            throw Invalid("invalid_service_catalogue_bands", "bands", "Интервалы допустимы только для ступенчатого тарифа.");
        return method switch
        {
            PriceMethod.Percent => PreparePercent(request, service, request.AvailableFrom),
            PriceMethod.Fixed => PrepareFixed(request, service, request.AvailableFrom),
            PriceMethod.Manual => PrepareManual(request, service, request.AvailableFrom),
            PriceMethod.Auto => PrepareManual(request, service, request.AvailableFrom) with { PriceMethod = PriceMethod.Auto },
            PriceMethod.Stepped => PrepareStepped(request, service, request.AvailableFrom),
            _ => throw Invalid("invalid_service_catalogue_method", "priceMethod", "Выберите способ расчёта из списка.")
        };
    }

    internal static Guid RequireVersion(Guid? version)
    {
        if (version is null || version == Guid.Empty)
            throw Invalid("invalid_service_catalogue_version", "version", "Передайте текущую версию тарифа.");
        return version.Value;
    }

    internal static ServiceCatalogueOpsDto Operations(string[] roles)
    {
        BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ViewServiceCatalogue);
        var manage = BackofficeAuthorization.IsAllowed(roles, BackofficeAction.ManageServiceCatalogue);
        return new ServiceCatalogueOpsDto(
            Enum.GetValues<ServiceKind>().OrderBy(value => (int)value)
                .Select(value => new ServiceCatalogueServiceOpsDto((int)value, value.GetDisplayName(), value.GetRouteAlias(), PricingCurrencies.ToArray(), Enum.GetValues<PriceMethod>(), value.IsIncludedInTotal())).ToArray(),
            Enum.GetValues<PriceMethod>().OrderBy(value => (int)value)
                .Select(value => new EnumOpsItemDto((int)value, value.GetDisplayName(), value.GetRouteAlias())).ToArray(),
            PricingCurrencies.Select(value => new ServiceCatalogueCurrencyOpsDto((int)value,
                value.GetDisplayName(), value.GetRouteAlias(), value switch
                {
                    Currency.Rub => "₽",
                    Currency.Usd => "$",
                    _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
                })).ToArray(),
            Enum.GetValues<ServiceCatalogueAuditAction>().OrderBy(value => (int)value)
                .Select(value => new EnumOpsItemDto((int)value, value.GetDisplayName(), value.GetRouteAlias())).ToArray(),
            new ServiceCatalogueLimitsDto(MaximumAmount, AmountDecimalPlaces, MaximumPercentage,
                PercentageDecimalPlaces, AuditSearchMaxLength, AuditPageSizeMaximum),
            new ServiceCatalogueActionsDto(true, manage, manage, manage, true),
            ConsentCalendar.TimeZoneId);
    }

    private static PreparedServiceCatalogueEntry PreparePercent(
        ServiceCatalogueWriteRequest request,
        ServiceKind service,
        DateOnly? availableFrom)
    {
        if (request.Currency != Currency.Usd)
            throw Invalid("invalid_service_catalogue_currency", "currency", "Процент от стоимости товара задаётся в долларах США.");
        if (request.Amount is not null)
            throw Invalid("invalid_service_catalogue_amount", "amount", "Для процентного тарифа фиксированная сумма не указывается.");
        if (!ValidPercentage(request.Percentage))
            throw Invalid("invalid_service_catalogue_percentage", "percentage",
                "Укажите процент больше нуля и не больше 100, максимум с четырьмя дробными знаками.");
        ValidateOptionalAmount(request.MinimumAmount, "minimumAmount");
        ValidateOptionalAmount(request.MaximumAmount, "maximumAmount");
        if (request.MinimumAmount is { } minimum && request.MaximumAmount is { } maximum && maximum < minimum)
            throw Invalid("invalid_service_catalogue_maximum_amount", "maximumAmount",
                "Максимальная сумма не может быть меньше минимальной.");
        return new PreparedServiceCatalogueEntry(service, PriceMethod.Percent, request.Percentage,
            request.MinimumAmount, request.MaximumAmount, null, request.Currency, availableFrom, request.AvailableBy);
    }

    private static PreparedServiceCatalogueEntry PrepareFixed(
        ServiceCatalogueWriteRequest request,
        ServiceKind service,
        DateOnly? availableFrom)
    {
        RejectPercentFields(request);
        ValidateCurrency(request.Currency);
        if (!ValidAmount(request.Amount))
            throw Invalid("invalid_service_catalogue_amount", "amount",
                "Укажите сумму от 0 до 99999999,99, максимум с двумя дробными знаками.");
        return new PreparedServiceCatalogueEntry(service, PriceMethod.Fixed, null, null, null,
            request.Amount, request.Currency, availableFrom, request.AvailableBy);
    }

    private static PreparedServiceCatalogueEntry PrepareManual(
        ServiceCatalogueWriteRequest request,
        ServiceKind service,
        DateOnly? availableFrom)
    {
        RejectPercentFields(request);
        ValidateCurrency(request.Currency);
        if (request.Amount is not null)
            throw Invalid("invalid_service_catalogue_amount", "amount", "Для ручного тарифа сумма каталога не указывается.");
        return new PreparedServiceCatalogueEntry(service, PriceMethod.Manual, null, null, null,
            null, request.Currency, availableFrom, request.AvailableBy);
    }

    private static void RejectPercentFields(ServiceCatalogueWriteRequest request)
    {
        if (request.Percentage is not null)
            throw Invalid("invalid_service_catalogue_percentage", "percentage", "Для этого способа расчёта процент не указывается.");
        if (request.MinimumAmount is not null)
            throw Invalid("invalid_service_catalogue_minimum_amount", "minimumAmount", "Для этого способа расчёта минимум не указывается.");
        if (request.MaximumAmount is not null)
            throw Invalid("invalid_service_catalogue_maximum_amount", "maximumAmount", "Для этого способа расчёта максимум не указывается.");
    }

    private static PreparedServiceCatalogueEntry PrepareStepped(ServiceCatalogueWriteRequest request, ServiceKind service, DateOnly? availableFrom)
    {
        var prepared = PrepareManual(request, service, availableFrom);
        if (request.IntervalCurrency != Currency.Usd)
            throw Invalid("invalid_service_catalogue_currency", "intervalCurrency", "Диапазоны задаются в долларах США, как стоимость товара.");
        if (request.Bands is not { Length: > 0 and <= MaximumBands } bands)
            throw Invalid("invalid_service_catalogue_bands", "bands", "Укажите от 1 до 100 интервалов.");
        decimal? expectedFrom = null;
        for (var index = 0; index < bands.Length; index++)
        {
            var band = bands[index];
            if (band is null || band.From != expectedFrom || index > 0 && !ValidAmount(band.From) || !ValidAmount(band.Amount)
                || (index == bands.Length - 1 ? band.By is not null : band.By is null || !ValidAmount(band.By) || (band.From is { } from && band.By <= from)))
                throw Invalid("invalid_service_catalogue_bands", "bands", "Первый интервал без нижней границы; далее граница «от» равна предыдущей границе «до» и не включается. Граница «до» включается; последний интервал без верхней границы. Суммы неотрицательны, до двух дробных знаков.");
            expectedFrom = band.By;
        }
        return prepared with { PriceMethod = PriceMethod.Stepped, IntervalCurrency = request.IntervalCurrency, Bands = bands.ToArray() };
    }

    private static void ValidateCurrency(Currency? currency)
    {
        if (currency is not (Currency.Rub or Currency.Usd))
            throw Invalid("invalid_service_catalogue_currency", "currency", "Выберите российский рубль или доллар США.");
    }

    private static void ValidateOptionalAmount(decimal? amount, string field)
    {
        if (amount is not null && !ValidAmount(amount))
        {
            var code = field == "minimumAmount"
                ? "invalid_service_catalogue_minimum_amount"
                : "invalid_service_catalogue_maximum_amount";
            throw Invalid(code, field, "Укажите неотрицательную сумму не больше 99999999,99, максимум с двумя дробными знаками.");
        }
    }

    private static bool ValidAmount(decimal? value)
        => value is >= 0 and <= MaximumAmount && decimal.Round(value.Value, AmountDecimalPlaces) == value;

    private static bool ValidPercentage(decimal? value)
        => value is > 0 and <= MaximumPercentage && decimal.Round(value.Value, PercentageDecimalPlaces) == value;

    private static ServiceException Invalid(string code, string field, string message)
        => new(400, code) { Errors = new Dictionary<string, string[]> { [field] = [message] } };
}

internal sealed record PreparedServiceCatalogueEntry(
    ServiceKind Service,
    PriceMethod PriceMethod,
    decimal? Percentage,
    decimal? MinimumAmount,
    decimal? MaximumAmount,
    decimal? Amount,
    Currency? Currency,
    DateOnly? AvailableFrom,
    DateOnly? AvailableBy,
    Currency? IntervalCurrency = null,
    PriceBand[]? Bands = null);
