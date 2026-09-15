// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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
        new(database, new StubClient(rate ?? Rate, failure), time ?? new FixedTime(Now), logger ?? NullLogger<ExchangeRateService>.Instance);

    [SetUp]
    public async Task ClearHistory()
    {
        await IntegrationTestEnvironment.ResetAsync();
    }

    [Test]
    public async Task StoresSourceMetadataAndPreservesEveryFirstObservationAcrossRetriesAndFailures()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = Database(scope);
        var logger = new TestLogger<ExchangeRateService>();
        await Service(database, logger: logger).SynchronizeAsync(default);
        await Service(database, Rate with { OfficialRate = 1 }, new FixedTime(Now.AddHours(1))).SynchronizeAsync(default);
        var history = await database.ExchangeRateHistory.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(history.Id, Is.Positive);
            Assert.That(history.Provider, Is.EqualTo("CBR"));
            Assert.That(history.Source, Is.EqualTo(CbrRateClient.Endpoint));
            Assert.That(history.BaseCurrency, Is.EqualTo(Currency.Usd));
            Assert.That(history.QuoteCurrency, Is.EqualTo(Currency.Rub));
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
    public void Model_PersistsNumericCurrenciesAndMakesHistoryValuesImmutable()
    {
        using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=metadata;Username=unused;Password=unused")
            .Options);
        var entity = database.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ExchangeRateHistory))!;
        var immutableProperties = new[]
        {
            nameof(ExchangeRateHistory.Provider),
            nameof(ExchangeRateHistory.Source),
            nameof(ExchangeRateHistory.BaseCurrency),
            nameof(ExchangeRateHistory.QuoteCurrency),
            nameof(ExchangeRateHistory.Nominal),
            nameof(ExchangeRateHistory.OfficialRate),
            nameof(ExchangeRateHistory.SourceEffectiveDate),
            nameof(ExchangeRateHistory.RetrievedAt)
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entity.FindProperty(nameof(ExchangeRateHistory.BaseCurrency))!.GetColumnType(), Is.EqualTo("integer"));
            Assert.That(entity.FindProperty(nameof(ExchangeRateHistory.QuoteCurrency))!.GetColumnType(), Is.EqualTo("integer"));
            Assert.That(
                immutableProperties.Select(property => entity.FindProperty(property)!.GetAfterSaveBehavior()),
                Has.All.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(entity.GetCheckConstraints().Select(constraint => constraint.Name), Does.Contain("CK_exchange_rate_base_currency"));
            Assert.That(entity.GetCheckConstraints().Select(constraint => constraint.Name), Does.Contain("CK_exchange_rate_quote_currency"));
        }
    }

    [Test]
    public async Task ReadIgnoresOtherPairsProvidersAndFutureDates()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = Database(scope);
        Assert.That(await Service(database).GetLatestAsync(default), Is.Null);
        await Service(database).SynchronizeAsync(default);
        foreach (var (provider, baseCurrency, quoteCurrency, date) in new[]
        {
            ("other", Currency.Usd, Currency.Rub, new DateOnly(2026, 9, 7)),
            ("CBR", (Currency)978, Currency.Rub, new DateOnly(2026, 9, 7)),
            ("CBR", Currency.Usd, (Currency)978, new DateOnly(2026, 9, 7)),
            ("CBR", Currency.Usd, Currency.Rub, new DateOnly(2026, 9, 8))
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
        Assert.That(status.ExchangeRates.Single(), Is.EqualTo(new ExchangeRateDto(
            "CBR", Currency.Usd, Currency.Rub, Rate.Nominal, Rate.OfficialRate, Rate.SourceEffectiveDate, Now)));
        Assert.That(status.Currencies, Is.EqualTo(new[]
        {
            new EnumOpsItemDto(643, "Российский рубль", "rub"),
            new EnumOpsItemDto(840, "Доллар США", "usd"), new EnumOpsItemDto(978, "Евро", "eur")
        }));
        using var health = await client.GetAsync("/api/v1/status/status");
        Assert.That(await health.Content.ReadAsStringAsync(), Does.Not.Contain("exchangeRates"));
    }

    [TestCase("2026-09-06T20:59:00Z", "2026-09-06")]
    [TestCase("2026-09-06T21:00:00Z", "2026-09-07")]
    [TestCase("2026-09-06T21:09:59Z", "2026-09-07")]
    [TestCase("2026-12-31T22:00:00Z", "2027-01-01")]
    public void ScheduleUsesMoscowCalendarIndependentOfServerTimezone(string now, string date)
    {
        Assert.That(ExchangeRateSchedule.MoscowDate(DateTimeOffset.Parse(now)), Is.EqualTo(DateOnly.Parse(date)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TimeZoneResolutionFallsBackToNativeWindowsIdentifier(bool invalidData)
    {
        var attempts = new List<string>();
        var expected = TimeZoneInfo.CreateCustomTimeZone("Moscow test", TimeSpan.FromHours(3), "Moscow test", "Moscow test");
        var actual = ExchangeRateSchedule.ResolveMoscow(id =>
        {
            attempts.Add(id);
            if (id == "Europe/Moscow")
            {
                if (invalidData) throw new InvalidTimeZoneException();
                throw new TimeZoneNotFoundException();
            }
            return expected;
        });
        Assert.That(actual, Is.SameAs(expected));
        Assert.That(attempts, Is.EqualTo(new[] { "Europe/Moscow", "Russian Standard Time" }));
        Assert.That(ExchangeRateSchedule.ResolveMoscow(_ => expected), Is.SameAs(expected));
        Assert.Throws<TimeZoneNotFoundException>(() => ExchangeRateSchedule.ResolveMoscow(_ => throw new TimeZoneNotFoundException()));
        Assert.Throws<InvalidOperationException>(() => ExchangeRateSchedule.ResolveMoscow(_ => throw new InvalidOperationException()));
    }

    [Test]
    public async Task JobReportsFailureWithoutLeakingProviderDetails()
    {
        var logger = new TestLogger<ExchangeRateJob>();
        var job = new ExchangeRateJob(
            new StubSynchronizer(_ => Task.FromException(new HttpRequestException("secret response"))),
            logger);
        await job.Execute(null!, default);
        var failed = logger.Records.Single(record => record.Event.Id == 1702);
        Assert.That(failed.Event.Name, Is.EqualTo(SarafanEvents.ExchangeRateUpdateFailedName));
        Assert.That(failed.Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(failed.Message, Does.Not.Contain("secret"));
        Assert.That(failed.Exception, Is.Null);
        Assert.That(failed.Scope["error.type"], Is.EqualTo(typeof(HttpRequestException).FullName));
    }

    [Test]
    public async Task JobTreatsRequestedCancellationAsExpected()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var logger = new TestLogger<ExchangeRateJob>();
        var job = new ExchangeRateJob(
            new StubSynchronizer(token => Task.FromCanceled(token)),
            logger);
        await job.Execute(null!, cancellation.Token);
        Assert.That(logger.Records, Is.Empty);
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
        Assert.That(LogValueSummary.Describe(new ExchangeRateDto(
            "secret", Currency.Usd, Currency.Rub, 1, 81, Rate.SourceEffectiveDate, Now)),
            Is.EqualTo("ExchangeRateDto(rate/metadata=[redacted])"));
        Assert.That(LogValueSummary.Describe(new BackofficeStatus("secret", "secret", "secret", [], [])),
            Is.EqualTo("BackofficeStatus(version/rates=[redacted])"));
    }

    [Test]
    public async Task SynchronizationPersistsBothCurrenciesAndIndependentSourceDates()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = Database(scope);
        var eur = Rate with { BaseCurrency = Currency.Eur, OfficialRate = 100m };
        var client = new PairClient([Rate, eur]);
        var service = new ExchangeRateService(db, client, new FixedTime(Now), NullLogger<ExchangeRateService>.Instance);
        await service.SynchronizeAsync(default);
        client.Rates = [Rate with { OfficialRate = 1 }, eur with { OfficialRate = 1 }];
        await service.SynchronizeAsync(default);
        Assert.That(await db.ExchangeRateHistory.CountAsync(), Is.EqualTo(2));
        Assert.That((await service.GetLatestAsync(default, Currency.Eur))!.OfficialRate, Is.EqualTo(100));
        client.Rates = [eur with { SourceEffectiveDate = new DateOnly(2026, 9, 6) }];
        await service.SynchronizeAsync(default);
        Assert.That((await service.GetLatestAsync(default))!.SourceEffectiveDate, Is.EqualTo(Rate.SourceEffectiveDate));
        Assert.That((await service.GetLatestAsync(default, Currency.Eur))!.SourceEffectiveDate, Is.EqualTo(new DateOnly(2026, 9, 6)));
    }

    private sealed class PairClient(IReadOnlyList<CbrRate> rates) : ICbrRateClient
    {
        internal IReadOnlyList<CbrRate> Rates { get; set; } = rates;
        public Task<CbrRate> GetAsync(DateOnly date, CancellationToken cancellationToken) => Task.FromResult(Rates[0]);
        public Task<IReadOnlyList<CbrRate>> GetRatesAsync(DateOnly date, CancellationToken cancellationToken) => Task.FromResult(Rates);
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

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
