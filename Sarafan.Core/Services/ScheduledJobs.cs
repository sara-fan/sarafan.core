// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.Options;
using Quartz;

using Sarafan.Core.Observability;

namespace Sarafan.Core.Services;

internal static class ScheduledJobs
{
    internal static IServiceCollection AddSarafanScheduledJobs(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var schedules = configuration.GetSection(ScheduledJobsOptions.SectionName)
            .Get<ScheduledJobsOptions>() ?? new ScheduledJobsOptions();

        services.AddSingleton<IValidateOptions<ScheduledJobsOptions>, ScheduledJobsOptionsValidator>();
        services.AddOptions<ScheduledJobsOptions>()
            .Bind(configuration.GetSection(ScheduledJobsOptions.SectionName))
            .ValidateOnStart();

        services.AddQuartz(quartz =>
        {
            AddJob<ExchangeRateJob>(quartz, nameof(ScheduledJobsOptions.ExchangeRates), schedules.ExchangeRates);
            AddJob<ConsentRetentionJob>(quartz, nameof(ScheduledJobsOptions.ConsentRetention), schedules.ConsentRetention);
            AddJob<IanaTldUpdateJob>(quartz, nameof(ScheduledJobsOptions.IanaTldUpdate), schedules.IanaTldUpdate);
        });
        services.AddQuartzHostedService(options =>
        {
            options.AwaitApplicationStarted = true;
            options.WaitForJobsToComplete = true;
        });
        return services;
    }

    private static void AddJob<TJob>(
        IQuartzBuilder quartz,
        string name,
        ScheduledJobOptions schedule)
        where TJob : class, IJob
    {
        var jobKey = new JobKey(name);
        quartz.AddJob<TJob>(job => job.WithIdentity(jobKey).StoreDurably());

        if (!string.IsNullOrWhiteSpace(schedule.Cron))
        {
            quartz.AddTrigger(trigger => trigger
                .ForJob(jobKey)
                .WithIdentity($"{name}.cron")
                .WithCronSchedule(schedule.Cron, cron => cron
                    .InTimeZone(ScheduledJobTimeZones.Resolve(schedule.TimeZone))));
        }

        if (schedule.RunOnStartup)
        {
            quartz.AddTrigger(trigger => trigger
                .ForJob(jobKey)
                .WithIdentity($"{name}.startup")
                .StartNow());
        }
    }
}

[DisallowConcurrentExecution]
public sealed class ExchangeRateJob(
    IExchangeRateSynchronizer synchronizer,
    ILogger<ExchangeRateJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            await synchronizer.SynchronizeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SarafanEvents.ExchangeRateUpdateFailed(logger, exception);
        }
    }
}

[DisallowConcurrentExecution]
public sealed class ConsentRetentionJob(
    IConsentRetentionService retention,
    ILogger<ConsentRetentionJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            await retention.SweepAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SarafanEvents.ConsentRetentionFailed(logger, exception);
        }
    }
}

[DisallowConcurrentExecution]
public sealed class IanaTldUpdateJob(
    IIanaTldSynchronizer synchronizer,
    ILogger<IanaTldUpdateJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            await synchronizer.SynchronizeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SarafanEvents.IanaTldUpdateFailed(logger, exception);
        }
    }
}
