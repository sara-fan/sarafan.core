// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed class ConsentRetentionWorkerTests
{
    [Test]
    public async Task FailedSweep_ReportsSafeWarning_AndShutsDownWithoutWaitingADay()
    {
        var logger = new Collector();
        using var worker = new ConsentRetentionWorker(new FailedScopeFactory(), TimeProvider.System, logger);
        await worker.StartAsync(default);
        var message = await logger.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(message.Event.Id, Is.EqualTo(1800));
        Assert.That(message.Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(message.Message, Does.Contain("Consent retention processing failed").And.Not.Contain("private"));
        Assert.That(message.Exception, Is.Null);
    }

    private sealed class FailedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("private payload must never be logged");
    }
    private sealed class Collector : ILogger<ConsentRetentionWorker>
    {
        internal TaskCompletionSource<(EventId Event, LogLevel Level, string Message, Exception? Exception)> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Observed.TrySetResult((eventId, logLevel, formatter(state, exception), exception));
    }
}
