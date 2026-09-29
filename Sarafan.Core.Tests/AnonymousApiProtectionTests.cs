// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Sarafan.Core.Authentication;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture, NonParallelizable]
public sealed class AnonymousApiProtectionTests
{
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        await IntegrationTestEnvironment.ResetAsync();
        _app = IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.PostConfigure<AnonymousApiProtectionOptions>(options =>
            {
                var forecast = options.Policies[AnonymousApiPolicies.Forecast];
                forecast.ClientPerMinute = 30;
                forecast.ClientBurst = 10;
                forecast.AggregatePerMinute = 120;
                forecast.AggregateBurst = 20;
                forecast.Concurrency = 8;
            });
        }));
        _client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Test]
    public async Task ForecastIsAnonymousAndMissingTariffsYieldUnknownWithoutCreatingAnOrder()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orders/preview/forecast",
            new { sellerPrice = new { amount = 12.50m, currency = 840 }, quantity = 2 });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("\"totalRub\":null"));
            Assert.That(body, Does.Not.Contain("components"));
        });
    }

    [Test]
    public async Task ForecastRejectsInvalidInputsBeforeCalculation()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orders/preview/forecast",
            new { sellerPrice = new { amount = -1m, currency = 840 }, quantity = 2 });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("invalid-order-seller-price"));
    }

    [TestCase("{\"sellerPrice\":{\"amount\":10,\"currency\":840,\"other\":1},\"quantity\":1}")]
    [TestCase("{\"sellerPrice\":{\"amount\":10,\"currency\":840},\"quantity\":1,\"other\":1}")]
    [TestCase("{\"sellerPrice\":{\"amount\":10,\"currency\":840},\"quantity\":1,\"a\":{\"b\":{\"c\":{\"d\":{\"e\":{\"f\":{\"g\":{\"h\":0}}}}}}}}")]
    public async Task ForecastRejectsUnknownOrDeepJson(string json)
    {
        using var response = await _client.PostAsync("/api/v1/orders/preview/forecast",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [TestCase("{")]
    [TestCase("not-json")]
    public async Task ForecastRejectsMalformedJsonBeforeModelBinding(string json)
    {
        using var response = await _client.PostAsync("/api/v1/orders/preview/forecast",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo(SarafanProblemDetailsFactory.MediaType));
    }

    [Test]
    public async Task ForecastBurstReturnsStructuredRateLimit()
    {
        HttpResponseMessage? last = null;
        for (var index = 0; index < 11; index++)
        {
            last?.Dispose();
            last = await _client.PostAsJsonAsync("/api/v1/orders/preview/forecast",
                new { sellerPrice = new { amount = 10m, currency = 840 }, quantity = 1 });
        }
        using (last)
        {
            Assert.That(last!.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(last.Headers.RetryAfter?.Delta, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(await last.Content.ReadAsStringAsync(), Does.Contain("rate-limited"));
        }
    }

    [Test]
    public async Task ChunkedOversizeForecastBodyIsRejected()
    {
        using var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"sellerPrice\":\"" + new string('x', 3000) + "\"}")));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders/preview/forecast")
        {
            Content = content
        };
        request.Headers.TransferEncodingChunked = true;
        using var response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public void ConfigurationAndClientKeysAreBoundedAndStable()
    {
        var options = new AnonymousApiProtectionOptions();
        Assert.That(options.Validate, Throws.InvalidOperationException);
        _app.Services.GetRequiredService<IConfiguration>()
            .GetSection(AnonymousApiProtectionOptions.SectionName).Bind(options);
        Assert.That(options.Validate, Throws.Nothing);
        Assert.Multiple(() =>
        {
            Assert.That(options.Policies[AnonymousApiPolicies.PublicRead].ClientBurst, Is.EqualTo(30));
            Assert.That(options.Policies[AnonymousApiPolicies.Forecast].BodyBytes, Is.EqualTo(2048));
            Assert.That(options.Policies[AnonymousApiPolicies.Forecast].DeadlineSeconds, Is.EqualTo(5));
        });
        options.Policies[AnonymousApiPolicies.Forecast].ClientBurst = 0;
        Assert.That(options.Validate, Throws.InvalidOperationException);
        options.Policies.Remove(AnonymousApiPolicies.Forecast);
        Assert.That(options.Validate, Throws.InvalidOperationException);
        Assert.That(AnonymousApiProtectionRegistry.ClientKey(IPAddress.Parse("::ffff:192.0.2.1")), Is.EqualTo("192.0.2.1"));
        Assert.That(AnonymousApiProtectionRegistry.ClientKey(IPAddress.Parse("2001:db8:1:2::1")),
            Is.EqualTo(AnonymousApiProtectionRegistry.ClientKey(IPAddress.Parse("2001:db8:1:2::2"))));
        Assert.That(AnonymousApiProtectionRegistry.ClientKey(null), Is.EqualTo("unknown"));
    }

    [Test]
    public void RegistryEnforcesClientAggregateAndConcurrencyWithoutQueuing()
    {
        var registry = Registry(options =>
        {
            options.ClientPerMinute = 1;
            options.ClientBurst = 1;
            options.AggregatePerMinute = 1;
            options.AggregateBurst = 4;
            options.Concurrency = 1;
        });
        var first = registry.Acquire(AnonymousApiPolicies.Forecast, "client-a");
        try
        {
            Assert.That(first.Client?.IsAcquired, Is.True);
            Assert.That(first.Aggregate?.IsAcquired, Is.True);
            Assert.That(first.Concurrency?.IsAcquired, Is.True);

            var clientLimited = registry.Acquire(AnonymousApiPolicies.Forecast, "client-a");
            Assert.That(clientLimited.Client?.IsAcquired, Is.False);
            Assert.That(clientLimited.Aggregate, Is.Null);
            Release(clientLimited);

            var concurrent = registry.Acquire(AnonymousApiPolicies.Forecast, "client-b");
            Assert.That(concurrent.Client?.IsAcquired, Is.True);
            Assert.That(concurrent.Aggregate?.IsAcquired, Is.True);
            Assert.That(concurrent.Concurrency?.IsAcquired, Is.False);
            Release(concurrent);
        }
        finally
        {
            Release(first);
        }

        var admitted = registry.Acquire(AnonymousApiPolicies.Forecast, "client-c");
        Assert.That(admitted.Concurrency?.IsAcquired, Is.True);
        Release(admitted);
        var fourth = registry.Acquire(AnonymousApiPolicies.Forecast, "client-d");
        Assert.That(fourth.Aggregate?.IsAcquired, Is.True);
        Release(fourth);
        var aggregateLimited = registry.Acquire(AnonymousApiPolicies.Forecast, "client-e");
        Assert.That(aggregateLimited.Client?.IsAcquired, Is.True);
        Assert.That(aggregateLimited.Aggregate?.IsAcquired, Is.False);
        Assert.That(aggregateLimited.Concurrency, Is.Null);
        Release(aggregateLimited);
    }

    [Test]
    public async Task RegistryRetainsActiveEntriesAndReclaimsOnlyIdleReplenishedEntries()
    {
        var clock = new ManualClock();
        var registry = Registry(options =>
        {
            options.ClientPerMinute = 10000;
            options.ClientBurst = 1;
            options.AggregatePerMinute = options.AggregateBurst = 100000;
            options.Concurrency = 1;
        }, clock);
        var releases = new List<Action>();
        try
        {
            for (var index = 0; index < 4096; index++)
            {
                var lease = registry.Acquire(AnonymousApiPolicies.Forecast, $"client-{index}");
                Assert.That(lease.Client?.IsAcquired, Is.True);
                lease.Client?.Dispose();
                lease.Aggregate?.Dispose();
                lease.Concurrency?.Dispose();
                releases.Add(lease.Release!);
            }

            var full = registry.Acquire(AnonymousApiPolicies.Forecast, "overflow");
            Assert.That(full.Client, Is.Null);
            clock.Advance(TimeSpan.FromMinutes(3));
            await Task.Delay(30);
            var stillFull = registry.Acquire(AnonymousApiPolicies.Forecast, "overflow");
            Assert.That(stillFull.Client, Is.Null, "Active client entries must not be evicted.");

            foreach (var release in releases) release();
            releases.Clear();
            var admitted = registry.Acquire(AnonymousApiPolicies.Forecast, "overflow");
            Assert.That(admitted.Client?.IsAcquired, Is.True);
            Release(admitted);
        }
        finally
        {
            foreach (var release in releases) release();
        }
    }

    [TestCase("array")]
    [TestCase("span")]
    [TestCase("array-async")]
    public async Task StreamedBodyLimitCatchesEveryReadShape(string readShape)
    {
        var registry = Registry(options => options.BodyBytes = 4,
            policy: AnonymousApiPolicies.Authentication);
        var context = Context(AnonymousApiPolicies.Authentication, "{\"a\":123456}");
        var originalBody = context.Request.Body;
        var entered = false;
        var middleware = new AnonymousApiProtectionMiddleware(async request =>
        {
            entered = true;
            var bytes = new byte[16];
            // Exercise each low-level read overload against an unknown-length streamed body.
#pragma warning disable CA2022
            switch (readShape)
            {
                case "array": request.Request.Body.Read(bytes, 0, bytes.Length); break;
                case "span": request.Request.Body.Read(bytes.AsSpan()); break;
                default: await request.Request.Body.ReadAsync(bytes, 0, bytes.Length, request.RequestAborted); break;
            }
#pragma warning restore CA2022
        }, registry);

        await middleware.InvokeAsync(context, new SarafanProblemDetailsFactory());
        Assert.That(entered, Is.True);
        Assert.That(context.Response.StatusCode, Is.EqualTo(413));
        Assert.That(context.Request.Body, Is.SameAs(originalBody));
    }

    [TestCase("array")]
    [TestCase("span")]
    [TestCase("array-async")]
    public async Task StreamedBodyAtLimitStillReadsNormally(string readShape)
    {
        var registry = Registry(options => options.BodyBytes = 4,
            policy: AnonymousApiPolicies.Authentication);
        var context = Context(AnonymousApiPolicies.Authentication, "abcd");
        var read = 0;
        var middleware = new AnonymousApiProtectionMiddleware(async request =>
        {
            var bytes = new byte[4];
#pragma warning disable CA2022
            read = readShape switch
            {
                "array" => request.Request.Body.Read(bytes, 0, bytes.Length),
                "span" => request.Request.Body.Read(bytes.AsSpan()),
                _ => await request.Request.Body.ReadAsync(bytes, 0, bytes.Length, request.RequestAborted)
            };
#pragma warning restore CA2022
            Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("abcd"));
        }, registry);

        await middleware.InvokeAsync(context, new SarafanProblemDetailsFactory());
        Assert.That(read, Is.EqualTo(4));
        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task CappedBodyCannotSeekOrWriteAroundItsLimit()
    {
        var registry = Registry(options => options.BodyBytes = 4,
            policy: AnonymousApiPolicies.Authentication);
        var context = Context(AnonymousApiPolicies.Authentication, "abcd");
        var middleware = new AnonymousApiProtectionMiddleware(request =>
        {
            var body = request.Request.Body;
            Assert.That(body.CanRead, Is.True);
            Assert.That(body.CanSeek, Is.False);
            Assert.That(body.CanWrite, Is.False);
            Assert.Throws<NotSupportedException>(() => _ = body.Length);
            Assert.Throws<NotSupportedException>(() => _ = body.Position);
            Assert.Throws<NotSupportedException>(() => body.Position = 0);
            Assert.Throws<NotSupportedException>(() => body.Seek(0, SeekOrigin.Begin));
            Assert.Throws<NotSupportedException>(() => body.SetLength(0));
            Assert.Throws<NotSupportedException>(() => body.Write(new byte[1], 0, 1));
            body.Flush();
            return Task.CompletedTask;
        }, registry);

        await middleware.InvokeAsync(context, new SarafanProblemDetailsFactory());
        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void StartupRejectsAnonymousEndpointsWithoutKnownPolicy(bool unknownPolicy)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Registry(_ => { }));
        using var application = builder.Build();
        var endpoint = application.MapGet("/unprotected", () => "value").AllowAnonymous();
        if (unknownPolicy) endpoint.WithMetadata(new AnonymousApiPolicyAttribute("Unknown"));

        Assert.That(application.ValidateAnonymousApiPolicies, Throws.InvalidOperationException);
    }

    [Test]
    public async Task ServerDeadlineWritesTimeoutAndReleasesConcurrency()
    {
        var registry = Registry(options =>
        {
            options.DeadlineSeconds = 1;
            options.Concurrency = 1;
        });
        var context = Context(AnonymousApiPolicies.Forecast, "{\"quantity\":1}");
        var originalCancellation = context.RequestAborted;
        var middleware = new AnonymousApiProtectionMiddleware(
            request => Task.Delay(Timeout.InfiniteTimeSpan, request.RequestAborted), registry);

        await middleware.InvokeAsync(context, new SarafanProblemDetailsFactory());

        Assert.That(context.Response.StatusCode, Is.EqualTo(503));
        Assert.That(context.RequestAborted, Is.EqualTo(originalCancellation));
        var next = registry.Acquire(AnonymousApiPolicies.Forecast, "unknown");
        Assert.That(next.Concurrency?.IsAcquired, Is.True);
        Release(next);
    }

    [Test]
    public async Task ClientDisconnectPropagatesCancellationAndReleasesConcurrency()
    {
        var registry = Registry(options => options.Concurrency = 1);
        using var disconnect = new CancellationTokenSource();
        var context = Context(AnonymousApiPolicies.Forecast, "{\"quantity\":1}");
        context.RequestAborted = disconnect.Token;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new AnonymousApiProtectionMiddleware(request =>
        {
            entered.SetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, request.RequestAborted);
        }, registry);

        var running = middleware.InvokeAsync(context, new SarafanProblemDetailsFactory());
        await entered.Task;
        disconnect.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await running);
        var next = registry.Acquire(AnonymousApiPolicies.Forecast, "unknown");
        Assert.That(next.Concurrency?.IsAcquired, Is.True);
        Release(next);
    }

    private AnonymousApiProtectionRegistry Registry(Action<AnonymousApiPolicyOptions> configure,
        TimeProvider? clock = null, string policy = AnonymousApiPolicies.Forecast)
    {
        var options = new AnonymousApiProtectionOptions();
        _app.Services.GetRequiredService<IConfiguration>()
            .GetSection(AnonymousApiProtectionOptions.SectionName).Bind(options);
        configure(options.Policies[policy]);
        return new AnonymousApiProtectionRegistry(Options.Create(options), clock ?? TimeProvider.System);
    }

    private static DefaultHttpContext Context(string policy, string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/anonymous-test";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AllowAnonymousAttribute(), new AnonymousApiPolicyAttribute(policy)),
            "anonymous test"));
        return context;
    }

    private static void Release((System.Threading.RateLimiting.RateLimitLease? Client,
        System.Threading.RateLimiting.RateLimitLease? Aggregate,
        System.Threading.RateLimiting.RateLimitLease? Concurrency, Action? Release) lease)
    {
        lease.Concurrency?.Dispose();
        lease.Aggregate?.Dispose();
        lease.Client?.Dispose();
        lease.Release?.Invoke();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
