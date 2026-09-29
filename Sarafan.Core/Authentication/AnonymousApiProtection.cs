// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

using Sarafan.Core.Services;

namespace Sarafan.Core.Authentication;

public static class AnonymousApiPolicies
{
    public const string PublicRead = nameof(PublicRead);
    public const string PublicDownload = nameof(PublicDownload);
    public const string Authentication = nameof(Authentication);
    public const string StaffAuthentication = nameof(StaffAuthentication);
    public const string Session = nameof(Session);
    public const string StaffSession = nameof(StaffSession);
    public const string Logout = nameof(Logout);
    public const string StaffLogout = nameof(StaffLogout);
    public const string ProductPreview = nameof(ProductPreview);
    public const string Forecast = nameof(Forecast);
    public const string Health = nameof(Health);
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AnonymousApiPolicyAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public sealed class AnonymousApiPolicyOptions
{
    public int ClientPerMinute { get; set; }
    public int ClientBurst { get; set; }
    public int AggregatePerMinute { get; set; }
    public int AggregateBurst { get; set; }
    public int Concurrency { get; set; }
    public int BodyBytes { get; set; }
    public int DeadlineSeconds { get; set; }

    public void Validate(string name)
    {
        if (ClientPerMinute is < 1 or > 10000 || ClientBurst is < 1 or > 10000
            || AggregatePerMinute is < 1 or > 100000 || AggregateBurst is < 1 or > 100000
            || Concurrency is < 1 or > 1024 || BodyBytes is < 0 or > 1048576
            || DeadlineSeconds is < 1 or > 120)
            throw new InvalidOperationException($"AnonymousApiProtection:{name} contains an invalid limit.");
    }
}

public sealed class AnonymousApiProtectionOptions
{
    public const string SectionName = "AnonymousApiProtection";
    public Dictionary<string, AnonymousApiPolicyOptions> Policies { get; set; } = new(StringComparer.Ordinal);

    public void Validate()
    {
        string[] expected =
        [
            AnonymousApiPolicies.PublicRead, AnonymousApiPolicies.PublicDownload,
            AnonymousApiPolicies.Authentication, AnonymousApiPolicies.StaffAuthentication,
            AnonymousApiPolicies.Session, AnonymousApiPolicies.StaffSession,
            AnonymousApiPolicies.Logout, AnonymousApiPolicies.StaffLogout,
            AnonymousApiPolicies.ProductPreview, AnonymousApiPolicies.Forecast,
            AnonymousApiPolicies.Health
        ];
        if (Policies.Count != expected.Length || expected.Any(key => !Policies.ContainsKey(key)))
            throw new InvalidOperationException("AnonymousApiProtection must define every known policy exactly once.");
        foreach (var (name, policy) in Policies) policy.Validate(name);
    }
}

internal sealed class AnonymousApiProtectionRegistry
{
    private const int MaxClients = 4096;
    private readonly Dictionary<string, PolicyState> _policies;

    public AnonymousApiProtectionRegistry(IOptions<AnonymousApiProtectionOptions> options, TimeProvider timeProvider)
    {
        options.Value.Validate();
        _policies = options.Value.Policies.ToDictionary(item => item.Key,
            item => new PolicyState(item.Value, timeProvider), StringComparer.Ordinal);
    }

    public bool Contains(string name) => _policies.ContainsKey(name);

    public (RateLimitLease? Client, RateLimitLease? Aggregate, RateLimitLease? Concurrency, Action? Release) Acquire(
        string policy, string client)
    {
        var state = _policies[policy];
        var clientState = state.GetClient(client);
        if (clientState is null) return (null, null, null, null);
        clientState.Limiter.TryReplenish();
        state.Aggregate.TryReplenish();
        var clientLease = clientState.Limiter.AttemptAcquire();
        Action release = () => Interlocked.Decrement(ref clientState.Active);
        if (!clientLease.IsAcquired) return (clientLease, null, null, release);
        var aggregateLease = state.Aggregate.AttemptAcquire();
        if (!aggregateLease.IsAcquired) return (clientLease, aggregateLease, null, release);
        return (clientLease, aggregateLease, state.Concurrency.AttemptAcquire(), release);
    }

    public AnonymousApiPolicyOptions Options(string name) => _policies[name].Options;

    internal static string ClientKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString();
    }

    private sealed class PolicyState
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ClientState> _clients = new(StringComparer.Ordinal);
        private readonly TimeProvider _timeProvider;
        private int _operations;
        public PolicyState(AnonymousApiPolicyOptions options, TimeProvider timeProvider)
        {
            Options = options;
            _timeProvider = timeProvider;
            Aggregate = Bucket(options.AggregateBurst, options.AggregatePerMinute);
            Concurrency = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = options.Concurrency,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
        }
        public AnonymousApiPolicyOptions Options { get; }
        public TokenBucketRateLimiter Aggregate { get; }
        public ConcurrencyLimiter Concurrency { get; }

        public ClientState? GetClient(string key)
        {
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                if (++_operations % 128 == 0 || _clients.Count == MaxClients)
                {
                    foreach (var (name, item) in _clients.ToArray())
                    {
                        item.Limiter.TryReplenish();
                        if (now - item.LastSeen >= TimeSpan.FromMinutes(2) && Volatile.Read(ref item.Active) == 0
                            && item.Limiter.GetStatistics()?.CurrentAvailablePermits == Options.ClientBurst)
                        {
                            _clients.Remove(name);
                            item.Limiter.Dispose();
                        }
                    }
                }
                if (!_clients.TryGetValue(key, out var state))
                {
                    if (_clients.Count >= MaxClients) return null;
                    state = new ClientState(Bucket(Options.ClientBurst, Options.ClientPerMinute));
                    _clients.Add(key, state);
                }
                state.LastSeen = now;
                Interlocked.Increment(ref state.Active);
                return state;
            }
        }
    }

    private sealed class ClientState(TokenBucketRateLimiter limiter)
    {
        public TokenBucketRateLimiter Limiter { get; } = limiter;
        public DateTimeOffset LastSeen { get; set; }
        public int Active;
    }

    private static TokenBucketRateLimiter Bucket(int burst, int perMinute) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = burst,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = TimeSpan.FromSeconds(60d / perMinute),
        AutoReplenishment = false,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
    });
}

internal sealed class AnonymousApiProtectionMiddleware(
    RequestDelegate next, AnonymousApiProtectionRegistry registry)
{
    private static readonly Meter Meter = new("Sarafan.Core.AnonymousApiProtection");
    private static readonly Counter<long> Accepted = Meter.CreateCounter<long>("sarafan.anonymous.accepted");
    private static readonly Counter<long> Rejected = Meter.CreateCounter<long>("sarafan.anonymous.rejected");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("sarafan.anonymous.duration", "ms");
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("sarafan.anonymous.active");

    public async Task InvokeAsync(HttpContext context, SarafanProblemDetailsFactory problems)
    {
        var endpoint = context.GetEndpoint();
        var policy = endpoint?.Metadata.GetMetadata<AnonymousApiPolicyAttribute>();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is null || policy is null)
        {
            await next(context);
            return;
        }

        var limits = registry.Options(policy.Name);
        var leases = registry.Acquire(policy.Name, AnonymousApiProtectionRegistry.ClientKey(context.Connection.RemoteIpAddress));
        using var clientLease = leases.Client;
        using var aggregateLease = leases.Aggregate;
        using var concurrencyLease = leases.Concurrency;
        using var release = new ClientRelease(leases.Release);
        if (clientLease?.IsAcquired != true || aggregateLease?.IsAcquired != true
            || concurrencyLease?.IsAcquired != true)
        {
            var rejectedLease = clientLease?.IsAcquired != true ? clientLease
                : aggregateLease?.IsAcquired != true ? aggregateLease : concurrencyLease;
            var retrySeconds = rejectedLease is not null
                && rejectedLease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)
                ? Math.Clamp((int)Math.Ceiling(retry.TotalSeconds), 1, 3600) : 1;
            context.Response.Headers.RetryAfter = retrySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Rejected.Add(1, new KeyValuePair<string, object?>("policy", policy.Name),
                new KeyValuePair<string, object?>("reason", "admission"));
            await problems.WriteAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited", context.RequestAborted);
            return;
        }

        Accepted.Add(1, new KeyValuePair<string, object?>("policy", policy.Name));
        Active.Add(1, new KeyValuePair<string, object?>("policy", policy.Name));
        var started = Stopwatch.GetTimestamp();

        if (limits.BodyBytes == 0 && ((context.Request.ContentLength ?? 0) > 0
            || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            || limits.BodyBytes > 0 && context.Request.ContentLength > limits.BodyBytes)
        {
            try
            {
                Rejected.Add(1, new KeyValuePair<string, object?>("policy", policy.Name),
                    new KeyValuePair<string, object?>("reason", "body_size"));
                await problems.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", context.RequestAborted);
            }
            finally
            {
                Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    new KeyValuePair<string, object?>("policy", policy.Name));
                Active.Add(-1, new KeyValuePair<string, object?>("policy", policy.Name));
            }
            return;
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = limits.BodyBytes;
        var originalBody = context.Request.Body;
        context.Request.Body = new CappedBodyStream(originalBody, limits.BodyBytes);
        MemoryStream? bufferedForecast = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(limits.DeadlineSeconds));
        var original = context.RequestAborted;
        context.RequestAborted = deadline.Token;
        try
        {
            if (policy.Name == AnonymousApiPolicies.Forecast)
            {
                bufferedForecast = new MemoryStream();
                await context.Request.Body.CopyToAsync(bufferedForecast, deadline.Token);
                using (JsonDocument.Parse(bufferedForecast.ToArray(), new JsonDocumentOptions { MaxDepth = 8 })) { }
                bufferedForecast.Position = 0;
                context.Request.Body = bufferedForecast;
            }
            await next(context);
        }
        catch (JsonException) when (policy.Name == AnonymousApiPolicies.Forecast && !context.Response.HasStarted)
        {
            await problems.WriteAsync(context, StatusCodes.Status400BadRequest, "bad_request", original);
        }
        catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
        {
            await problems.WriteAsync(context, exception.StatusCode,
                SarafanProblemDetailsFactory.CodeForStatus(exception.StatusCode), original);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !original.IsCancellationRequested
            && !context.Response.HasStarted)
        {
            Rejected.Add(1, new KeyValuePair<string, object?>("policy", policy.Name),
                new KeyValuePair<string, object?>("reason", "deadline"));
            await problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "anonymous_api_timeout", original);
        }
        finally
        {
            context.RequestAborted = original;
            context.Request.Body = originalBody;
            bufferedForecast?.Dispose();
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("policy", policy.Name));
            Active.Add(-1, new KeyValuePair<string, object?>("policy", policy.Name));
        }
    }

    private sealed class ClientRelease(Action? release) : IDisposable
    {
        public void Dispose() => release?.Invoke();
    }

    private sealed class CappedBodyStream(Stream inner, long maximum) : Stream
    {
        private long _read;
        private int Remaining(int requested) => requested == 0 ? 0 : (int)Math.Min(requested, Math.Max(1, maximum - _read + 1));
        private void Count(int count)
        {
            _read += count;
            if (_read > maximum) throw new BadHttpRequestException("Request body exceeds the configured limit.", 413);
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, Remaining(count));
            Count(read);
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..Remaining(buffer.Length)]);
            Count(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..Remaining(buffer.Length)], cancellationToken);
            Count(read);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public static class AnonymousApiProtectionExtensions
{
    public static TBuilder WithAnonymousApiPolicy<TBuilder>(this TBuilder builder, string policy)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new AllowAnonymousAttribute(), new AnonymousApiPolicyAttribute(policy));
        return builder;
    }

    public static void ValidateAnonymousApiPolicies(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<AnonymousApiProtectionRegistry>();
        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints))
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null) continue;
            var policies = endpoint.Metadata.GetOrderedMetadata<AnonymousApiPolicyAttribute>();
            if (policies.Count != 1 || !registry.Contains(policies[0].Name))
                throw new InvalidOperationException($"Anonymous endpoint {endpoint.DisplayName} has no valid protection policy.");
        }
    }
}
