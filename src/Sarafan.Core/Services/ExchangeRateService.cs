// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Data;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public interface IExchangeRateSynchronizer
{
    Task SynchronizeAsync(CancellationToken cancellationToken);
}

public sealed class ExchangeRateService(
    AppDbContext database, ICbrRateClient cbr, TimeProvider timeProvider,
    ILogger<ExchangeRateService> logger) : IExchangeRateSynchronizer
{
    public Task SynchronizeAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(ExchangeRateService).FullName}.{nameof(SynchronizeAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)), async () =>
            {
                SarafanEvents.ExchangeRateUpdateStarted(logger);
                var date = ExchangeRateSchedule.MoscowDate(timeProvider.GetUtcNow());
                var rate = await cbr.GetAsync(date, cancellationToken);
                var retrievedAt = timeProvider.GetUtcNow();
                // The unique index arbitrates concurrent writers. Never alter the first observation.
                var inserted = await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO exchange_rate_history
                        (provider, source, base_currency, quote_currency, nominal, official_rate, source_effective_date, retrieved_at)
                    VALUES ({"CBR"}, {CbrRateClient.Endpoint}, {"USD"}, {"RUB"}, {rate.Nominal},
                        {rate.OfficialRate}, {rate.SourceEffectiveDate}, {retrievedAt})
                    ON CONFLICT (provider, base_currency, quote_currency, source_effective_date) DO NOTHING
                    """, cancellationToken);
                SarafanEvents.ExchangeRateUpdateCompleted(logger, inserted == 1);
            }, cancellationToken);

    public Task<ExchangeRateDto?> GetLatestAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(ExchangeRateService).FullName}.{nameof(GetLatestAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)), () =>
            {
                var today = ExchangeRateSchedule.MoscowDate(timeProvider.GetUtcNow());
                return database.ExchangeRateHistory.AsNoTracking()
                    .Where(rate => rate.Provider == "CBR" && rate.BaseCurrency == "USD" && rate.QuoteCurrency == "RUB"
                        && rate.SourceEffectiveDate <= today)
                    .OrderByDescending(rate => rate.SourceEffectiveDate)
                    .Select(rate => new ExchangeRateDto(rate.Provider, rate.BaseCurrency, rate.QuoteCurrency,
                        rate.Nominal, rate.OfficialRate, rate.SourceEffectiveDate, rate.RetrievedAt))
                    .FirstOrDefaultAsync(cancellationToken);
            }, cancellationToken);
}
