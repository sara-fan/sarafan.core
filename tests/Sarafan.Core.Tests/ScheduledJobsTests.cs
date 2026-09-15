// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Quartz;

using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed class ScheduledJobsTests
{
    [Test]
    public void DefaultsPreserveExistingSchedulesAndMakeTldStartupOptional()
    {
        var options = new ScheduledJobsOptions();
        using (Assert.EnterMultipleScope())
        {
            AssertSchedule(options.ExchangeRates, "0 10 0 * * ?", true);
            AssertSchedule(options.ConsentRetention, "0 0 1 * * ?", true);
            AssertSchedule(options.IanaTldUpdate, "0 0 3 5 * ?", false);
        }
    }

    [Test]
    public void ValidatorRejectsInvalidCronAndTimezoneAndAcceptsBlankCron()
    {
        var validator = new ScheduledJobsOptionsValidator();
        var invalid = new ScheduledJobsOptions
        {
            ExchangeRates = new ScheduledJobOptions { Cron = "bad", TimeZone = "Europe/Moscow" },
            ConsentRetention = new ScheduledJobOptions { Cron = "", TimeZone = "missing/timezone" },
            IanaTldUpdate = new ScheduledJobOptions { Cron = null, TimeZone = "Europe/Moscow" }
        };
        var result = validator.Validate(null, invalid);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Failed, Is.True);
            Assert.That(result.Failures, Has.Count.EqualTo(2));
            Assert.That(validator.Validate(null, new ScheduledJobsOptions()).Succeeded, Is.True);
        }

        var missingTimezone = new ScheduledJobsOptions
        {
            IanaTldUpdate = new ScheduledJobOptions { Cron = "", TimeZone = "" }
        };
        var missingTimezoneResult = validator.Validate(null, missingTimezone);
        Assert.That(missingTimezoneResult.Failures, Has.One.Contains("TimeZone is required"));
    }

    [Test]
    public async Task RegistrationCreatesDurableJobsCronTriggersAndOptionalStartupTriggers()
    {
        await using var provider = Services(new Dictionary<string, string?>
        {
            ["ScheduledJobs:ConsentRetention:Cron"] = "",
            ["ScheduledJobs:ConsentRetention:RunOnStartup"] = "false",
            ["ScheduledJobs:IanaTldUpdate:RunOnStartup"] = "true"
        });
        var scheduler = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler();
        var hostedOptions = provider.GetRequiredService<IOptions<QuartzHostedServiceOptions>>().Value;

        var exchange = await scheduler.GetJobDetail(new JobKey(nameof(ScheduledJobsOptions.ExchangeRates)));
        var retention = await scheduler.GetJobDetail(new JobKey(nameof(ScheduledJobsOptions.ConsentRetention)));
        var tld = await scheduler.GetJobDetail(new JobKey(nameof(ScheduledJobsOptions.IanaTldUpdate)));
        var exchangeCron = (ICronTrigger?)await scheduler.GetTrigger(new TriggerKey("ExchangeRates.cron"));
        var tldCron = (ICronTrigger?)await scheduler.GetTrigger(new TriggerKey("IanaTldUpdate.cron"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exchange?.Durable, Is.True);
            Assert.That(retention?.Durable, Is.True);
            Assert.That(tld?.Durable, Is.True);
            Assert.That(exchange?.ConcurrentExecutionDisallowed, Is.True);
            Assert.That(retention?.ConcurrentExecutionDisallowed, Is.True);
            Assert.That(tld?.ConcurrentExecutionDisallowed, Is.True);
            Assert.That(hostedOptions.AwaitApplicationStarted, Is.True);
            Assert.That(hostedOptions.WaitForJobsToComplete, Is.True);
            Assert.That(exchangeCron?.CronExpressionString, Is.EqualTo("0 10 0 * * ?"));
            Assert.That(exchangeCron?.TimeZone.Id, Is.AnyOf("Europe/Moscow", "Russian Standard Time"));
            Assert.That(tldCron?.CronExpressionString, Is.EqualTo("0 0 3 5 * ?"));
            Assert.That(await scheduler.Exists(new TriggerKey("ExchangeRates.startup")), Is.True);
            Assert.That(await scheduler.Exists(new TriggerKey("ConsentRetention.cron")), Is.False);
            Assert.That(await scheduler.Exists(new TriggerKey("ConsentRetention.startup")), Is.False);
            Assert.That(await scheduler.Exists(new TriggerKey("IanaTldUpdate.startup")), Is.True);
        }
    }

    [Test]
    public void MoscowTimezoneAliasFallbackIsCrossPlatform()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ScheduledJobTimeZones.Resolve("Europe/Moscow").BaseUtcOffset, Is.EqualTo(TimeSpan.FromHours(3)));
            Assert.That(ScheduledJobTimeZones.Resolve("Russian Standard Time").BaseUtcOffset, Is.EqualTo(TimeSpan.FromHours(3)));
            Assert.Throws<TimeZoneNotFoundException>(() => ScheduledJobTimeZones.Resolve("missing/timezone"));
        }
    }

    private static ServiceProvider Services(IDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSarafanScheduledJobs(configuration);
        return services.BuildServiceProvider();
    }

    private static void AssertSchedule(ScheduledJobOptions schedule, string cron, bool startup)
    {
        Assert.That(schedule.Cron, Is.EqualTo(cron));
        Assert.That(schedule.TimeZone, Is.EqualTo("Europe/Moscow"));
        Assert.That(schedule.RunOnStartup, Is.EqualTo(startup));
    }
}
