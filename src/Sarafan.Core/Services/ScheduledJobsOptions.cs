// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.Options;
using Quartz;

namespace Sarafan.Core.Services;

public sealed class ScheduledJobsOptions
{
    public const string SectionName = "ScheduledJobs";

    public ScheduledJobOptions ExchangeRates { get; set; } = new()
    {
        Cron = "0 10 0 * * ?",
        TimeZone = "Europe/Moscow",
        RunOnStartup = true
    };

    public ScheduledJobOptions ConsentRetention { get; set; } = new()
    {
        Cron = "0 0 1 * * ?",
        TimeZone = "Europe/Moscow",
        RunOnStartup = true
    };

    public ScheduledJobOptions IanaTldUpdate { get; set; } = new()
    {
        Cron = "0 0 3 5 * ?",
        TimeZone = "Europe/Moscow",
        RunOnStartup = false
    };
}

public sealed class ScheduledJobOptions
{
    public string? Cron { get; set; }
    public string TimeZone { get; set; } = "Europe/Moscow";
    public bool RunOnStartup { get; set; }
}

internal sealed class ScheduledJobsOptionsValidator : IValidateOptions<ScheduledJobsOptions>
{
    public ValidateOptionsResult Validate(string? name, ScheduledJobsOptions options)
    {
        var failures = new List<string>();
        Validate(nameof(options.ExchangeRates), options.ExchangeRates, failures);
        Validate(nameof(options.ConsentRetention), options.ConsentRetention, failures);
        Validate(nameof(options.IanaTldUpdate), options.IanaTldUpdate, failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Validate(string name, ScheduledJobOptions schedule, ICollection<string> failures)
    {
        if (!string.IsNullOrWhiteSpace(schedule.Cron) && !CronExpression.TryParse(schedule.Cron, out _))
        {
            failures.Add($"ScheduledJobs:{name}:Cron is not a valid Quartz cron expression.");
        }

        if (string.IsNullOrWhiteSpace(schedule.TimeZone))
        {
            failures.Add($"ScheduledJobs:{name}:TimeZone is required.");
            return;
        }

        try
        {
            ScheduledJobTimeZones.Resolve(schedule.TimeZone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            failures.Add($"ScheduledJobs:{name}:TimeZone is invalid.");
        }
    }
}

internal static class ScheduledJobTimeZones
{
    internal static TimeZoneInfo Resolve(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException) when (id.Equals("Europe/Moscow", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
        }
        catch (TimeZoneNotFoundException) when (id.Equals("Russian Standard Time", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        }
    }
}
