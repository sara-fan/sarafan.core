// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[NonParallelizable]
public sealed class ExchangeRateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-06T21:00:00Z");
    private static readonly CbrRate Rate = new(new DateOnly(2026, 9, 5), 100, 8112.3456m);
    private static AppDbContext Database(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();
    private static ExchangeRateService Service(AppDbContext database, CbrRate? rate = null, TimeProvider? time = null,
        ILogger<ExchangeRateService>? logger = null, Exception? failure = null) =>
        new(database, new StubClient(rate ?? Rate, failure), time ?? new ManualTime(Now), logger ?? NullLogger<ExchangeRateService>.Instance);

    [SetUp]
    public async Task ClearHistory()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        await Database(scope).ExchangeRateHistory.ExecuteDeleteAsync();
    }

    [Test]
    public async Task StoresSourceMetadataAndPreservesEveryFirstObservationAcrossRetriesConcurrencyAndFailures()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = Database(scope);
        var logger = new TestLogger<ExchangeRateService>();
        await Service(database, logger: logger).SynchronizeAsync(default);
        await Service(database, Rate with { OfficialRate = 1 }, new ManualTime(Now.AddHours(1))).SynchronizeAsync(default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var concurrent = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
            await Service(Database(concurrent)).SynchronizeAsync(default);
        }));
        var history = await database.ExchangeRateHistory.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Id, Is.Positive);
            Assert.That(history.Provider, Is.EqualTo("CBR"));
            Assert.That(history.Source, Is.EqualTo(CbrRateClient.Endpoint));
            Assert.That(history.BaseCurrency, Is.EqualTo("USD"));
            Assert.That(history.QuoteCurrency, Is.EqualTo("RUB"));
            Assert.That(history.Nominal, Is.EqualTo(Rate.Nominal));
            Assert.That(history.OfficialRate, Is.EqualTo(Rate.OfficialRate));
            Assert.That(history.SourceEffectiveDate, Is.EqualTo(Rate.SourceEffectiveDate));
            Assert.That(history.RetrievedAt, Is.EqualTo(Now));
        }
        await Service(database, Rate with { SourceEffectiveDate = new DateOnly(2026, 9, 6) }).SynchronizeAsync(default);
        await Service(database, Rate with { SourceEffectiveDate = new DateOnly(2026, 9, 4) }).SynchronizeAsync(default);
        var failure = new HttpRequestException("secret SOAP body");
        Assert.That(Assert.ThrowsAsync<HttpRequestException>(() => Service(database, failure: failure, logger: logger).SynchronizeAsync(default)), Is.SameAs(failure));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(() => Service(database).SynchronizeAsync(cancellation.Token));
        Assert.That(await database.ExchangeRateHistory.CountAsync(), Is.EqualTo(3));
        Assert.That((await Service(database).GetLatestAsync(default))!.SourceEffectiveDate, Is.EqualTo(new DateOnly(2026, 9, 6)));
        Assert.That(logger.Records.Any(record => record.Event.Id == 1600 && record.Level == LogLevel.Debug), Is.True);
        Assert.That(logger.Records.Any(record => record.Event.Id == 1601 && record.Level == LogLevel.Debug), Is.True);
        Assert.That(logger.Records.Any(record => record.Event.Id == 1602 && record.Level == LogLevel.Warning), Is.True);
        Assert.That(string.Join(" ", logger.Records.Select(record => record.Message)), Does.Not.Contain("secret"));
        Assert.That(logger.Records.All(record => record.Exception is null), Is.True);
    }

    [Test]
    public async Task UniqueConstraintRejectsDuplicateAndReadIgnoresOtherPairsProvidersAndFutureDates()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = Database(scope);
        Assert.That(await Service(database).GetLatestAsync(default), Is.Null);
        await Service(database).SynchronizeAsync(default);
        var original = await database.ExchangeRateHistory.AsNoTracking().SingleAsync();
        database.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Provider = original.Provider,
            Source = original.Source,
            BaseCurrency = original.BaseCurrency,
            QuoteCurrency = original.QuoteCurrency,
            Nominal = 1,
            OfficialRate = 99,
            SourceEffectiveDate = original.SourceEffectiveDate,
            RetrievedAt = Now
        });
        Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        database.ChangeTracker.Clear();
        foreach (var (provider, baseCurrency, quoteCurrency, date) in new[]
        {
            ("other", "USD", "RUB", new DateOnly(2026, 9, 7)), ("CBR", "EUR", "RUB", new DateOnly(2026, 9, 7)),
            ("CBR", "USD", "EUR", new DateOnly(2026, 9, 7)), ("CBR", "USD", "RUB", new DateOnly(2026, 9, 8))
        }) database.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Provider = provider,
            Source = "test",
            BaseCurrency = baseCurrency,
            QuoteCurrency = quoteCurrency,
            Nominal = 1,
            OfficialRate = 99,
            SourceEffectiveDate = date,
            RetrievedAt = Now
        });
        await database.SaveChangesAsync();
        Assert.That((await Service(database).GetLatestAsync(default))!.SourceEffectiveDate, Is.EqualTo(Rate.SourceEffectiveDate));
    }

    [Test]
    public async Task MigrationRoundTripLeavesExistingIdentityDataIntact()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = Database(scope);
        var identities = await database.BackofficeUsers.CountAsync();
        var migrations = database.Database.GetMigrations().ToArray();
        Assert.That(migrations[^1], Does.EndWith("_ExchangeRateHistory"));
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[^2]);
        await migrator.MigrateAsync();
        Assert.That(await database.BackofficeUsers.CountAsync(), Is.EqualTo(identities));
        Assert.That(await database.ExchangeRateHistory.CountAsync(), Is.Zero);
        await Service(database).SynchronizeAsync(default);
        Assert.That(await database.ExchangeRateHistory.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task StaffStatusIsAuthorizedNoStoreAndUsesPersistedRatesWithoutExposingFxOnPublicHealth()
    {
        using var client = IntegrationTestEnvironment.Factory.CreateClient();
        using var anonymous = await client.GetAsync("/api/v1/backoffice/status");
        Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(anonymous.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/problem+json"));
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = Database(scope);
        var customer = scope.ServiceProvider.GetRequiredService<JwtTokenService>().CreateAccessToken(new Customer { Id = 1, Phone = "+79990000000" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", customer.Token);
        using var wrongIdentity = await client.GetAsync("/api/v1/backoffice/status");
        Assert.That(wrongIdentity.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        var staff = await database.BackofficeUsers.AsNoTracking().Include(user => user.UserRoles).FirstAsync(user => user.IsActive);
        var token = scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(staff);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var empty = await client.GetAsync("/api/v1/backoffice/status");
        empty.EnsureSuccessStatusCode();
        Assert.That((await empty.Content.ReadFromJsonAsync<BackofficeStatus>())!.ExchangeRates, Is.Empty);
        await Service(database).SynchronizeAsync(default);
        using var populated = await client.GetAsync("/api/v1/backoffice/status");
        var status = await populated.Content.ReadFromJsonAsync<BackofficeStatus>();
        Assert.That(populated.Headers.CacheControl!.NoStore, Is.True);
        Assert.That(status!.Service, Is.EqualTo("Sarafan.Core"));
        Assert.That(status.Status, Is.EqualTo("ok"));
        Assert.That(status.AppVersion, Is.EqualTo(VersionInfo.AppVersion));
        Assert.That(status.ExchangeRates.Single(), Is.EqualTo(new ExchangeRateDto("CBR", "USD", "RUB", Rate.Nominal, Rate.OfficialRate, Rate.SourceEffectiveDate, Now)));
        using var health = await client.GetAsync("/api/v1/status/status");
        Assert.That(await health.Content.ReadAsStringAsync(), Does.Not.Contain("exchangeRates"));
    }

    [TestCase("2026-09-06T20:59:00Z", "2026-09-06T21:10:00Z", "2026-09-06")]
    [TestCase("2026-09-06T21:00:00Z", "2026-09-06T21:10:00Z", "2026-09-07")]
    [TestCase("2026-09-06T21:09:59Z", "2026-09-06T21:10:00Z", "2026-09-07")]
    [TestCase("2026-09-06T21:10:00Z", "2026-09-07T21:10:00Z", "2026-09-07")]
    [TestCase("2026-12-31T22:00:00Z", "2027-01-01T21:10:00Z", "2027-01-01")]
    public void ScheduleUsesMoscowCalendarIndependentOfServerTimezone(string now, string next, string date)
    {
        Assert.That(ExchangeRateSchedule.NextRun(DateTimeOffset.Parse(now)), Is.EqualTo(DateTimeOffset.Parse(next)));
        Assert.That(ExchangeRateSchedule.MoscowDate(DateTimeOffset.Parse(now)), Is.EqualTo(DateOnly.Parse(date)));
    }

    [Test]
    public async Task WorkerStartsImmediatelyRetriesNextMoscowMidnightAfterFailureAndCancelsWait()
    {
        var time = new ManualTime(Now);
        var calls = 0;
        var logger = new TestLogger<ExchangeRateWorker>();
        await using var provider = new ServiceCollection().AddScoped<IExchangeRateSynchronizer>(_ => new StubSynchronizer(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new HttpRequestException("secret response");
            return Task.CompletedTask;
        })).BuildServiceProvider();
        using var worker = new ExchangeRateWorker(provider.GetRequiredService<IServiceScopeFactory>(), time, logger);
        await worker.StartAsync(default);
        var first = await time.Scheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(first.Delay, Is.EqualTo(TimeSpan.FromMinutes(10)));
        time.Advance(first);
        var second = await time.Scheduled.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(calls, Is.EqualTo(2));
        Assert.That(second.Delay, Is.EqualTo(TimeSpan.FromDays(1)));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        var failed = logger.Records.Single(record => record.Event.Id == 1702);
        Assert.That(failed.Event.Name, Is.EqualTo(SarafanEvents.ExchangeRateUpdateFailedName));
        Assert.That(failed.Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(failed.Message, Does.Not.Contain("secret"));
        Assert.That(failed.Exception, Is.Null);
        Assert.That(failed.Scope["error.type"], Is.EqualTo(typeof(HttpRequestException).FullName));
    }

    [Test]
    public async Task WorkerDoesNotBlockStartupOrOverlapAndCancelsAnActiveSync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var time = new ManualTime(Now);
        await using var provider = new ServiceCollection().AddScoped<IExchangeRateSynchronizer>(_ => new StubSynchronizer(async cancellation =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        })).BuildServiceProvider();
        using var worker = new ExchangeRateWorker(provider.GetRequiredService<IServiceScopeFactory>(), time, NullLogger<ExchangeRateWorker>.Instance);
        await worker.StartAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(time.Scheduled.Reader.TryRead(out _), Is.False);
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(worker.ExecuteTask!.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public void CatalogueEventsAndNewSummariesPreservePrivacy()
    {
        var logger = new TestLogger<ExchangeRateService>();
        SarafanEvents.ExchangeRateUpdateStarted(logger);
        SarafanEvents.ExchangeRateUpdateCompleted(logger, true);
        SarafanEvents.ExchangeRateUpdateCompleted(logger, false);
        Assert.That(logger.Records.Select(record => (record.Event.Id, record.Event.Name, record.Level)), Is.EqualTo(new[]
        {
            (1700, SarafanEvents.ExchangeRateUpdateStartedName, LogLevel.Information),
            (1701, SarafanEvents.ExchangeRateUpdateCompletedName, LogLevel.Information),
            (1701, SarafanEvents.ExchangeRateUpdateCompletedName, LogLevel.Information)
        }));
        Assert.That(LogValueSummary.Describe(Rate), Is.EqualTo("CbrRate(rate/metadata=[redacted])"));
        Assert.That(LogValueSummary.Describe(new ExchangeRateDto("secret", "USD", "RUB", 1, 81, Rate.SourceEffectiveDate, Now)), Is.EqualTo("ExchangeRateDto(rate/metadata=[redacted])"));
        Assert.That(LogValueSummary.Describe(new BackofficeStatus("secret", "secret", "secret", [])), Is.EqualTo("BackofficeStatus(version/rates=[redacted])"));
    }

    private sealed class StubClient(CbrRate rate, Exception? failure) : ICbrRateClient
    {
        public Task<CbrRate> GetAsync(DateOnly date, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.That(date, Is.EqualTo(new DateOnly(2026, 9, 7)));
            return failure is null ? Task.FromResult(rate) : Task.FromException<CbrRate>(failure);
        }
    }

    private sealed class StubSynchronizer(Func<CancellationToken, Task> synchronize) : IExchangeRateSynchronizer
    {
        public Task SynchronizeAsync(CancellationToken cancellationToken) => synchronize(cancellationToken);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        internal Channel<ManualTimer> Scheduled { get; } = Channel.CreateUnbounded<ManualTimer>();
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            Scheduled.Writer.TryWrite(timer);
            return timer;
        }
        internal void Advance(ManualTimer timer) { _now += timer.Delay; timer.Fire(); }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        internal TimeSpan Delay { get; } = delay;
        internal void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Log(LogLevel Level, EventId Event, string Message, Exception? Exception, Dictionary<string, object?> Scope);
    private sealed class TestLogger<T> : ILogger<T>
    {
        private Dictionary<string, object?> _scope = [];
        internal List<Log> Records { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var previous = _scope;
            _scope = state is IEnumerable<KeyValuePair<string, object?>> fields ? fields.ToDictionary() : [];
            return new Scope(() => _scope = previous);
        }
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Records.Add(new Log(level, eventId, formatter(state, exception), exception, new(_scope)));
        private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }
}
