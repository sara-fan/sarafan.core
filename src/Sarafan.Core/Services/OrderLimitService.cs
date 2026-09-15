// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed record OrderLimitRatePair(ExchangeRateHistory Usd, ExchangeRateHistory Eur)
{
    private BigInteger Numerator => new BigInteger(Eur.OfficialRate * 1000000m) * Usd.Nominal * new BigInteger(OrderLimitService.MaximumAmountEur * 100m);
    private BigInteger Denominator => new BigInteger(Usd.OfficialRate * 1000000m) * Eur.Nominal;
    // Bound UI metadata by the largest representable merchandise total as well as the EUR limit.
    public decimal MaximumTotalUsd => (decimal)BigInteger.Min(Numerator / Denominator, 39999999996L) / 100m;
    internal bool Allows(decimal unitPrice, int quantity)
        => new BigInteger(unitPrice * 100m) * quantity * Denominator <= Numerator;
    internal OrderLimitCheckDto ToDto() => new(OrderLimitService.MaximumAmountEur, Currency.Eur, true, Usd.SourceEffectiveDate, MaximumTotalUsd);
}

public sealed class OrderLimitService(AppDbContext database, TimeProvider timeProvider, ILogger<OrderLimitService> logger)
{
    public const decimal BaseMaximumAmountEur = 1000m;
    public const decimal ExchangeRateReserveCoefficient = 0.9m;
    public static readonly decimal MaximumAmountEur = BaseMaximumAmountEur * ExchangeRateReserveCoefficient;
    public static readonly decimal ExchangeRateReservePercent = (1m - ExchangeRateReserveCoefficient) * 100m;

    public Task<OrderLimitRatePair?> GetPairAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(OrderLimitService).FullName}.{nameof(GetPairAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)), async () =>
            {
                var today = ExchangeRateSchedule.MoscowDate(timeProvider.GetUtcNow());
                var rates = database.ExchangeRateHistory.AsNoTracking().Where(rate => rate.Provider == "CBR"
                    && rate.QuoteCurrency == Currency.Rub && rate.SourceEffectiveDate <= today);
                return await (from usd in rates
                              where usd.BaseCurrency == Currency.Usd
                              join eur in rates.Where(rate => rate.BaseCurrency == Currency.Eur)
                                  on usd.SourceEffectiveDate equals eur.SourceEffectiveDate
                              orderby usd.SourceEffectiveDate descending
                              select new OrderLimitRatePair(usd, eur)).FirstOrDefaultAsync(cancellationToken);
            }, cancellationToken);

    internal static OrderLimitCheckDto ToDto(OrderLimitRatePair? pair)
        => pair?.ToDto() ?? new(OrderLimitService.MaximumAmountEur, Currency.Eur, false, null, null);

    internal static OrderProductLimitsDto Limits(OrderLimitRatePair? pair)
        => new(1, 4, 1, 500, 200, 200, 2000, Currency.Usd, 99999999.99m, 2, ToDto(pair));

    internal static OrderLimitRatePair Validate(OrderProductDto product, OrderLimitRatePair? pair)
    {
        if (pair is null) throw new ServiceException(503, "order_limit_rates_unavailable");
        if (!pair.Allows(product.SellerPrice!.Amount, product.Quantity))
            throw new ServiceException(400, "order_value_limit_exceeded");
        return pair;
    }
}
