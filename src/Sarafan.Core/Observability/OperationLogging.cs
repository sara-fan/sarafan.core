// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Runtime.CompilerServices;

using Sarafan.Core.Services;

namespace Sarafan.Core.Observability;

internal static class OperationLogging
{
    private static readonly ConditionalWeakTable<Exception, object> ReportedExceptions = new();

    internal static T Run<T>(ILogger logger, string operation, Func<string> inputs, Func<T> action)
    {
        Enter(logger, operation, inputs);
        try
        {
            var result = action();
            Exit(logger, operation, result);
            return result;
        }
        catch (Exception exception)
        {
            Failed(logger, operation, exception, CancellationToken.None);
            throw;
        }
    }

    internal static async Task<T> RunAsync<T>(
        ILogger logger, string operation, Func<string> inputs, Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        Enter(logger, operation, inputs);
        try
        {
            var result = await action();
            Exit(logger, operation, result);
            return result;
        }
        catch (Exception exception)
        {
            Failed(logger, operation, exception, cancellationToken);
            throw;
        }
    }

    internal static Task RunAsync(
        ILogger logger, string operation, Func<string> inputs, Func<Task> action,
        CancellationToken cancellationToken)
        => RunAsync(logger, operation, inputs, async () =>
        {
            await action();
            return new OperationCompleted();
        }, cancellationToken);

    internal static void Enter(
        ILogger logger,
        string operation,
        Func<string> inputs,
        LogLevel logLevel = LogLevel.Debug)
    {
        if (logger.IsEnabled(logLevel))
        {
            SarafanEvents.OperationEntered(logger, logLevel, operation, inputs());
        }
    }

    internal static void Exit(
        ILogger logger,
        string operation,
        object? output,
        LogLevel logLevel = LogLevel.Debug)
    {
        if (logger.IsEnabled(logLevel))
        {
            SarafanEvents.OperationExited(logger, logLevel, operation, LogValueSummary.Describe(output));
        }
    }

    internal static bool IsExpected(Exception exception, CancellationToken cancellationToken)
        => exception is ServiceException or BadHttpRequestException
            || exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

    internal static bool WasReported(Exception exception) => ReportedExceptions.TryGetValue(exception, out _);

    internal static void Failed(
        ILogger logger,
        string operation,
        Exception exception,
        CancellationToken cancellationToken,
        LogLevel logLevel = LogLevel.Debug)
    {
        if (!IsExpected(exception, cancellationToken) && logger.IsEnabled(LogLevel.Warning))
        {
            SarafanEvents.OperationFailed(logger, operation, exception);
            ReportedExceptions.GetValue(exception, _ => new object());
        }

        if (logger.IsEnabled(logLevel))
        {
            SarafanEvents.OperationExited(logger, logLevel, operation, exception switch
            {
                ServiceException service => $"no result; service rejection; status={service.StatusCode}",
                BadHttpRequestException request => $"no result; request rejection; status={request.StatusCode}",
                OperationCanceledException when cancellationToken.IsCancellationRequested => "no result; cancelled",
                _ => "no result; unexpected exception"
            });
        }
    }

    internal sealed record OperationCompleted;
}
