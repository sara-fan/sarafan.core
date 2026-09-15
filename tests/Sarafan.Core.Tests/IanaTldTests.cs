// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class IanaTldTests
{
    private static readonly DateTimeOffset SourceUpdatedAt = DateTimeOffset.Parse("2026-09-14T07:07:01Z");
    private static readonly DateTimeOffset RetrievedAt = DateTimeOffset.Parse("2026-09-15T01:00:00Z");

    [Test]
    public void ParserAcceptsCanonicalFeedAndExtractsMetadata()
    {
        var bytes = Feed("2026091400", Values(), "Mon Sep 14 07:07:01 2026 UTC", "\r\n");
        var result = IanaTldClient.Parse(bytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Version, Is.EqualTo("2026091400"));
            Assert.That(result.SourceUpdatedAt, Is.EqualTo(SourceUpdatedAt));
            Assert.That(result.TopLevelDomains, Is.Ordered.And.Unique);
            Assert.That(result.TopLevelDomains, Does.Contain("COM"));
            Assert.That(result.ContentSha256, Does.Match("^[0-9a-f]{64}$"));
        }
    }

    [TestCase("missing-newline")]
    [TestCase("bom")]
    [TestCase("header")]
    [TestCase("lowercase")]
    [TestCase("duplicate")]
    [TestCase("unordered")]
    [TestCase("truncated")]
    [TestCase("utf8")]
    [TestCase("oversized")]
    public void ParserRejectsUntrustedOrIncompleteContent(string kind)
    {
        var values = Values().ToList();
        byte[] data = kind switch
        {
            "missing-newline" => Feed("2026091400", values)[..^1],
            "bom" => Encoding.UTF8.GetPreamble().Concat(Feed("2026091400", values)).ToArray(),
            "header" => Encoding.UTF8.GetBytes("# current\n" + string.Join('\n', values) + "\n"),
            "lowercase" => Feed("2026091400", values.Select(value => value == "COM" ? "com" : value).Order()),
            "duplicate" => Feed("2026091400", values.Append("COM").Order()),
            "unordered" => Feed("2026091400", values.Reverse<string>()),
            "truncated" => Feed("2026091400", values.Take(10)),
            "utf8" => [0xff, 0xfe, 0x0a],
            "oversized" => new byte[IanaTldClient.MaximumResponseBytes + 1],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        Assert.Throws<InvalidDataException>(() => IanaTldClient.Parse(data));
    }

    [Test]
    public async Task HttpClientUsesOfficialEndpointAndPropagatesHttpFailureAndCancellation()
    {
        var requestUri = "";
        using var successClient = new HttpClient(new Handler((request, _) =>
        {
            requestUri = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Feed("2026091400", Values()))
            });
        }));
        var client = new IanaTldClient(successClient, NullLogger<IanaTldClient>.Instance);
        Assert.That((await client.GetAsync(default)).Version, Is.EqualTo("2026091400"));
        Assert.That(requestUri, Is.EqualTo(IanaTldClient.Endpoint));

        using var failureClient = new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))));
        Assert.ThrowsAsync<HttpRequestException>(() =>
            new IanaTldClient(failureClient, NullLogger<IanaTldClient>.Instance).GetAsync(default));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Assert.CatchAsync<OperationCanceledException>(() => client.GetAsync(cancellation.Token));
    }

    [Test]
    public async Task SynchronizationReplacesOnlyWithAValidNewerVersion()
    {
        await using var database = Database();
        await database.Database.EnsureCreatedAsync();
        var first = Download("2026091400", 'a');
        var same = Download("2026091400", 'a');
        var older = Download("2026091300", 'b');
        var newer = Download("2026091500", 'c');
        var client = new QueueClient(first, same, older, newer);
        var logger = new TestLogger<IanaTldCatalogService>();
        var service = new IanaTldCatalogService(database, client, new FixedTime(RetrievedAt), logger);

        Assert.That(await service.SynchronizeAsync(default), Is.EqualTo(IanaTldUpdateResult.Updated));
        Assert.That(await service.SynchronizeAsync(default), Is.EqualTo(IanaTldUpdateResult.Unchanged));
        Assert.That(await service.SynchronizeAsync(default), Is.EqualTo(IanaTldUpdateResult.OlderIgnored));
        Assert.That((await service.GetRequiredAsync(default)).Version, Is.EqualTo("2026091400"));
        Assert.That(await service.SynchronizeAsync(default), Is.EqualTo(IanaTldUpdateResult.Updated));

        var stored = await database.IanaTldCatalog.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Id, Is.EqualTo(IanaTldCatalog.SingletonId));
            Assert.That(stored.Version, Is.EqualTo("2026091500"));
            Assert.That(stored.Source, Is.EqualTo(IanaTldClient.Endpoint));
            Assert.That(stored.SourceUpdatedAt, Is.EqualTo(SourceUpdatedAt));
            Assert.That(stored.RetrievedAt, Is.EqualTo(RetrievedAt));
            Assert.That(stored.ContentSha256, Is.EqualTo(new string('c', 64)));
            Assert.That(stored.TopLevelDomains, Is.EqualTo(newer.TopLevelDomains));
            Assert.That(logger.Records.Count(record => record.Event.Id == 1900), Is.EqualTo(4));
            Assert.That(logger.Records.Count(record => record.Event.Id == 1901), Is.EqualTo(4));
        }
    }

    [Test]
    public async Task SameVersionWithDifferentContentAndClientFailurePreserveCurrent()
    {
        await using var database = Database();
        await database.Database.EnsureCreatedAsync();
        database.IanaTldCatalog.Add(IntegrationTestEnvironment.CreateIanaTldCatalog());
        await database.SaveChangesAsync();

        var changed = Download("2026091400", 'f');
        var conflict = new IanaTldCatalogService(database, new QueueClient(changed),
            new FixedTime(RetrievedAt), NullLogger<IanaTldCatalogService>.Instance);
        Assert.ThrowsAsync<InvalidDataException>(() => conflict.SynchronizeAsync(default));

        var failure = new HttpRequestException("private response");
        var failed = new IanaTldCatalogService(database, new FailedClient(failure),
            new FixedTime(RetrievedAt), NullLogger<IanaTldCatalogService>.Instance);
        Assert.That(Assert.ThrowsAsync<HttpRequestException>(() => failed.SynchronizeAsync(default)), Is.SameAs(failure));
        Assert.That((await database.IanaTldCatalog.AsNoTracking().SingleAsync()).ContentSha256,
            Is.EqualTo(new string('0', 64)));
    }

    [Test]
    public async Task MissingCatalogThrowsStableServiceRejection()
    {
        await using var database = Database();
        await database.Database.EnsureCreatedAsync();
        var service = new IanaTldCatalogService(database, new QueueClient(Download("2026091400", 'a')),
            new FixedTime(RetrievedAt), NullLogger<IanaTldCatalogService>.Instance);
        var exception = Assert.ThrowsAsync<ServiceException>(() => service.GetRequiredAsync(default));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.StatusCode, Is.EqualTo(503));
            Assert.That(exception.Code, Is.EqualTo("tld_catalog_unavailable"));
        }
    }

    [Test]
    public void ModelUsesOneConstrainedPostgreSqlArrayRow()
    {
        using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=metadata;Username=unused;Password=unused")
            .Options);
        var entity = database.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(IanaTldCatalog))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entity.GetTableName(), Is.EqualTo("iana_tld_catalog"));
            Assert.That(entity.FindProperty(nameof(IanaTldCatalog.TopLevelDomains))!.GetColumnType(), Is.EqualTo("text[]"));
            Assert.That(entity.GetCheckConstraints().Select(constraint => constraint.Name), Is.EquivalentTo(new[]
            {
                "ck_iana_tld_catalog_content_sha256",
                "ck_iana_tld_catalog_not_empty",
                "ck_iana_tld_catalog_singleton",
                "ck_iana_tld_catalog_version"
            }));
        }
    }

    [Test]
    public async Task TldDependentEndpointsReturnExact503WhileHealthRemainsAvailable()
    {
        await IntegrationTestEnvironment.ResetAsync();
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            database.IanaTldCatalog.RemoveRange(database.IanaTldCatalog);
            await database.SaveChangesAsync();
        }

        using var client = IntegrationTestEnvironment.Factory.CreateClient();
        foreach (var response in new[]
        {
            await client.GetAsync("/api/v1/orders/ops"),
            await client.PostAsJsonAsync("/api/v1/orders/preview", new ProductPreviewRequest { SourceUrl = "shop.example.com" })
        })
        {
            using (response)
            {
                var problem = await response.Content.ReadFromJsonAsync<SarafanProblemDetails>();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
                    Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(SarafanProblemDetailsFactory.MediaType));
                    Assert.That(problem?.Code, Is.EqualTo("tld_catalog_unavailable"));
                    Assert.That(problem?.Type, Is.EqualTo("https://sarafan.sw.consulting/problems/tld-catalog-unavailable"));
                    Assert.That(problem?.Title, Is.EqualTo("Каталог доменов временно недоступен"));
                    Assert.That(problem?.Detail, Is.EqualTo("Проверка ссылки временно недоступна. Повторите попытку позже."));
                }
            }
        }

        using var health = await client.GetAsync("/api/v1/status/status");
        Assert.That(health.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task JobReportsSafeFailureAndTldSummariesRemainBounded()
    {
        var logger = new TestLogger<IanaTldUpdateJob>();
        var job = new IanaTldUpdateJob(new FailedSynchronizer(), logger);
        await job.Execute(null!, default);
        var failed = logger.Records.Single(record => record.Event.Id == 1902);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed.Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(failed.Message, Does.Not.Contain("private"));
            Assert.That(failed.Exception, Is.Null);
            Assert.That(failed.Scope["error.type"], Is.EqualTo(typeof(InvalidDataException).FullName));
            Assert.That(LogValueSummary.Describe(Download("2026091400", 'a')),
                Is.EqualTo("IanaTldDownload(version=2026091400; count=1102; digest=[redacted])"));
            Assert.That(LogValueSummary.Describe(new IanaTldCatalogSnapshot("2026091400", ["COM"])),
                Is.EqualTo("IanaTldCatalogSnapshot(version=2026091400; count=1)"));
        }
    }

    [Test]
    public async Task JobHonorsRequestedCancellationWithoutFailureLog()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var logger = new TestLogger<IanaTldUpdateJob>();
        var job = new IanaTldUpdateJob(new CancelledSynchronizer(), logger);

        await job.Execute(null!, cancellation.Token);

        Assert.That(logger.Records, Is.Empty);
    }

    private static AppDbContext Database() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IanaTldDownload Download(string version, char digest)
        => new(version, SourceUpdatedAt, new string(digest, 64), Values());

    private static string[] Values() => Enumerable.Range(0, 1_100)
        .Select(index => $"T{index:D4}")
        .Append("COM")
        .Append("XN--P1AI")
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static byte[] Feed(
        string version,
        IEnumerable<string> values,
        string updated = "Mon Sep 14 07:07:01 2026 UTC",
        string newline = "\n")
        => Encoding.UTF8.GetBytes($"# Version {version}, Last Updated {updated}{newline}{string.Join(newline, values)}{newline}");

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class QueueClient(params IanaTldDownload[] downloads) : IIanaTldClient
    {
        private readonly Queue<IanaTldDownload> _downloads = new(downloads);
        public Task<IanaTldDownload> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult(_downloads.Dequeue());
    }

    private sealed class FailedClient(Exception exception) : IIanaTldClient
    {
        public Task<IanaTldDownload> GetAsync(CancellationToken cancellationToken)
            => Task.FromException<IanaTldDownload>(exception);
    }

    private sealed class FailedSynchronizer : IIanaTldSynchronizer
    {
        public Task<IanaTldUpdateResult> SynchronizeAsync(CancellationToken cancellationToken)
            => Task.FromException<IanaTldUpdateResult>(new InvalidDataException("private feed content"));
    }

    private sealed class CancelledSynchronizer : IIanaTldSynchronizer
    {
        public Task<IanaTldUpdateResult> SynchronizeAsync(CancellationToken cancellationToken)
            => Task.FromCanceled<IanaTldUpdateResult>(cancellationToken);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
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
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add(new Log(level, eventId, formatter(state, exception), exception, new(_scope)));
        private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }
}
