// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Data;
using Sarafan.Core.Models;
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
                var rates = await cbr.GetRatesAsync(date, cancellationToken);
                var retrievedAt = timeProvider.GetUtcNow();
                var inserted = false;
                foreach (var rate in rates)
                    inserted |= await AppDatabaseOperations.For(database)
                        .InsertExchangeRateAsync(database, rate, retrievedAt, cancellationToken);
                SarafanEvents.ExchangeRateUpdateCompleted(logger, inserted);
            }, cancellationToken);

    public Task<ExchangeRateDto?> GetLatestAsync(CancellationToken cancellationToken, Currency currency = Currency.Usd)
        => OperationLogging.RunAsync(logger, $"{typeof(ExchangeRateService).FullName}.{nameof(GetLatestAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)), () =>
            {
                var today = ExchangeRateSchedule.MoscowDate(timeProvider.GetUtcNow());
                return database.ExchangeRateHistory.AsNoTracking()
                    .Where(rate => rate.Provider == "CBR"
                        && rate.BaseCurrency == currency
                        && rate.QuoteCurrency == Currency.Rub
                        && rate.SourceEffectiveDate <= today)
                    .OrderByDescending(rate => rate.SourceEffectiveDate)
                    .Select(rate => new ExchangeRateDto(rate.Provider, rate.BaseCurrency, rate.QuoteCurrency,
                        rate.Nominal, rate.OfficialRate, rate.SourceEffectiveDate, rate.RetrievedAt))
                    .FirstOrDefaultAsync(cancellationToken);
            }, cancellationToken);
}
