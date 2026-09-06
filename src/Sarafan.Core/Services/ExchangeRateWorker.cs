// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Observability;

namespace Sarafan.Core.Services;

internal static class ExchangeRateSchedule
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");

    internal static DateOnly MoscowDate(DateTimeOffset utcNow)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, Moscow).DateTime);

    internal static DateTimeOffset NextRun(DateTimeOffset utcNow)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, Moscow).DateTime;
        var next = local.Date.AddMinutes(10);
        if (next <= local) next = next.AddDays(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(next, Moscow));
    }
}

public sealed class ExchangeRateWorker(
    IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<ExchangeRateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // .NET 10 runs the entire background service asynchronously; provider failures cannot block startup.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IExchangeRateSynchronizer>().SynchronizeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                SarafanEvents.ExchangeRateUpdateFailed(logger, exception);
            }

            var now = timeProvider.GetUtcNow();
            await Task.Delay(ExchangeRateSchedule.NextRun(now) - now, timeProvider, stoppingToken);
        }
    }
}
