// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.Logging;

using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed class ConsentRetentionJobTests
{
    [Test]
    public async Task FailedSweep_ReportsSafeWarning()
    {
        var logger = new Collector();
        var job = new ConsentRetentionJob(new FailedRetention(), logger);
        await job.Execute(null!, default);
        var message = await logger.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(message.Event.Id, Is.EqualTo(1800));
        Assert.That(message.Event.Name, Is.EqualTo("sarafan.core.consent.retention.failed"));
        Assert.That(SarafanEvents.ConsentRetentionFailedName, Is.EqualTo(message.Event.Name));
        Assert.That(message.Attributes, Is.EquivalentTo(new[]
        {
            new KeyValuePair<string, object?>("error.type", typeof(InvalidOperationException).FullName)
        }));
        Assert.That(message.Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(message.Message, Is.EqualTo("Consent retention processing failed; the next scheduled run will retry."));
        Assert.That(message.Exception, Is.Null);
    }

    [Test]
    public async Task RequestedCancellationStopsWithoutFailureLog()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var logger = new Collector();
        var job = new ConsentRetentionJob(new CancelledRetention(), logger);

        await job.Execute(null!, cancellation.Token);

        Assert.That(logger.Observed.Task.IsCompleted, Is.False);
    }

    private sealed class FailedRetention : IConsentRetentionService
    {
        public Task<ConsentRetentionDto> SweepAsync(CancellationToken token)
            => Task.FromException<ConsentRetentionDto>(new InvalidOperationException("private payload must never be logged"));
    }

    private sealed class CancelledRetention : IConsentRetentionService
    {
        public Task<ConsentRetentionDto> SweepAsync(CancellationToken token)
            => Task.FromCanceled<ConsentRetentionDto>(token);
    }

    private sealed class Collector : ILogger<ConsentRetentionJob>
    {
        internal TaskCompletionSource<(EventId Event, LogLevel Level, string Message, Exception? Exception, KeyValuePair<string, object?>[] Attributes)> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private KeyValuePair<string, object?>[] _attributes = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            _attributes = ((IEnumerable<KeyValuePair<string, object?>>)state).ToArray();
            return null;
        }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Observed.TrySetResult((eventId, logLevel, formatter(state, exception), exception, _attributes));
    }
}
