// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService
{
    public Task<CustomerPricingDto> ForecastAsync(OrderForecastRequest request, CancellationToken token)
        => PricingRun(nameof(ForecastAsync),
            () => LogValueSummary.Inputs((nameof(request), request), (nameof(token), token)), async () =>
        {
            var quantity = request.Quantity.GetValueOrDefault();
            if (quantity is < 1 or > 4)
                throw ForecastInvalid("quantity", "Укажите количество от 1 до 4.");
            if (request.SellerPrice is { } price &&
                (price.Currency != Currency.Usd || price.Amount <= 0 || price.Amount > 99999999.99m
                    || decimal.Round(price.Amount, 2) != price.Amount))
                throw ForecastInvalid("sellerPrice", "Укажите цену в долларах США с двумя дробными знаками.");

            var now = timeProvider.GetUtcNow();
            if (request.SellerPrice is null)
                return new CustomerPricingDto(CustomerPricingState.Forecast, null, null, null, now, null, null);
            var order = new Order(0, 1, "https://sarafan.invalid/forecast", quantity,
                null, Guid.Empty, now);
            order.SetProduct(new OrderProductDto(null, request.SellerPrice, quantity, null, null, null));
            var calculation = await OrderPriceCalculator.CalculateAsync(database, order, now,
                OrderPricingInputs.Empty, null, token);
            return new CustomerPricingDto(CustomerPricingState.Forecast, calculation.TotalRub,
                calculation.CalculatedAt, null, now,
                null, null);
        }, token);

    private static ServiceException ForecastInvalid(string field, string detail)
        => new(400, field == "quantity" ? "invalid_order_quantity" : "invalid_order_seller_price")
        {
            Errors = new Dictionary<string, string[]> { [field] = [detail] }
        };

    private async Task<CustomerPricingDto> CustomerPricingAsync(long orderId, DateTimeOffset now,
        CancellationToken token)
        => CustomerPricing(await LatestPricingAsync(orderId, token), now);

    private static CustomerPricingDto CustomerPricing(OrderPricingSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null)
            return new(CustomerPricingState.Forecast, null, null, null, now, null, null);
        var calculation = ReadCalculation(snapshot);
        var state = snapshot.ValidUntil is null ? CustomerPricingState.Forecast
            : now >= snapshot.ValidUntil ? CustomerPricingState.Expired : CustomerPricingState.Confirmed;
        return new(state, calculation.TotalRub, calculation.CalculatedAt, snapshot.ValidUntil, now,
            null, snapshot.ValidUntil is null ? null : ExcludedRub(calculation, ServiceKind.CustomsPayments));
    }

    private static decimal? ExcludedRub(OrderPriceCalculationDto calculation, ServiceKind service)
        => calculation.Components.SingleOrDefault(component => component.Service == service)?.AmountRub;

    private async Task<Dictionary<long, OrderPricingSnapshot>> LatestCustomerPricingAsync(long[] orderIds,
        CancellationToken token)
    {
        if (orderIds.Length == 0) return [];
        var snapshots = await database.OrderPricingSnapshots.AsNoTracking()
            .Where(row => orderIds.Contains(row.OrderId)
                && row.Id == database.OrderPricingSnapshots.Where(other => other.OrderId == row.OrderId)
                    .Max(other => other.Id))
            .ToArrayAsync(token);
        return snapshots.ToDictionary(row => row.OrderId);
    }
}
