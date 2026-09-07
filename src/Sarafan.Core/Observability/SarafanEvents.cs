// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Observability;

public static partial class SarafanEvents
{
    public const string ExchangeRateUpdateStartedName = "sarafan.core.exchange_rate.update.started";
    public const string ExchangeRateUpdateCompletedName = "sarafan.core.exchange_rate.update.completed";
    public const string ExchangeRateUpdateFailedName = "sarafan.core.exchange_rate.update.failed";

    [LoggerMessage(EventId = 1700, EventName = ExchangeRateUpdateStartedName, Level = LogLevel.Information,
        Message = "CBR USD/RUB synchronization started.")]
    public static partial void ExchangeRateUpdateStarted(ILogger logger);

    [LoggerMessage(EventId = 1701, EventName = ExchangeRateUpdateCompletedName, Level = LogLevel.Information,
        Message = "CBR USD/RUB synchronization completed. New history entry: {Inserted}.")]
    public static partial void ExchangeRateUpdateCompleted(ILogger logger, bool inserted);

    public static void ExchangeRateUpdateFailed(ILogger logger, Exception exception)
    {
        using var scope = ErrorTypeScope(logger, exception);
        ExchangeRateUpdateFailedMessage(logger);
    }

    [LoggerMessage(EventId = 1702, EventName = ExchangeRateUpdateFailedName, Level = LogLevel.Warning,
        Message = "CBR USD/RUB synchronization failed; existing history was preserved. The next scheduled run will retry.")]
    private static partial void ExchangeRateUpdateFailedMessage(ILogger logger);

    public const string ApplicationStartedName = "sarafan.core.application.started";
    public const string ApplicationStoppedName = "sarafan.core.application.stopped";
    public const string MigrationStartedName = "sarafan.core.database.migration.started";
    public const string MigrationCompletedName = "sarafan.core.database.migration.completed";
    public const string MigrationFailedName = "sarafan.core.database.migration.failed";
    public const string RequestCompletedName = "sarafan.core.http.request.completed";
    public const string ProblemEmittedName = "sarafan.core.problem.emitted";
    public const string UnhandledExceptionName = "sarafan.core.exception.unhandled";
    public const string AuthenticationRejectedName = "sarafan.core.authentication.rejected";
    public const string OperationEnteredName = "sarafan.core.operation.entered";
    public const string OperationExitedName = "sarafan.core.operation.exited";
    public const string OperationFailedName = "sarafan.core.operation.failed";

    public static void OperationEntered(ILogger logger, string operation, string inputs)
        => OperationEntered(logger, LogLevel.Debug, operation, inputs);

    public static void OperationEntered(
        ILogger logger,
        LogLevel logLevel,
        string operation,
        string inputs)
    {
        using var scope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new("code.function.name", operation),
            new("sarafan.operation.inputs", inputs)
        });
        OperationEnteredMessage(logger, logLevel, operation, inputs);
    }

    public static void OperationExited(ILogger logger, string operation, string outputs)
        => OperationExited(logger, LogLevel.Debug, operation, outputs);

    public static void OperationExited(
        ILogger logger,
        LogLevel logLevel,
        string operation,
        string outputs)
    {
        using var scope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new("code.function.name", operation),
            new("sarafan.operation.outputs", outputs)
        });
        OperationExitedMessage(logger, logLevel, operation, outputs);
    }

    public static void OperationFailed(ILogger logger, string operation, Exception exception)
    {
        var errorType = exception.GetType().FullName ?? exception.GetType().Name;
        using var scope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new("code.function.name", operation),
            new("error.type", errorType)
        });
        OperationFailedMessage(logger, operation, errorType);
    }

    [LoggerMessage(EventId = 1600, EventName = OperationEnteredName, Message = "Entering {Operation}. Inputs: {Inputs}.")]
    private static partial void OperationEnteredMessage(
        ILogger logger,
        LogLevel logLevel,
        string operation,
        string inputs);

    [LoggerMessage(EventId = 1601, EventName = OperationExitedName, Message = "Exiting {Operation}. Outputs: {Outputs}.")]
    private static partial void OperationExitedMessage(
        ILogger logger,
        LogLevel logLevel,
        string operation,
        string outputs);

    [LoggerMessage(EventId = 1602, EventName = OperationFailedName, Level = LogLevel.Warning, Message = "Unexpected exception in {Operation}. Exception type: {ErrorType}.")]
    private static partial void OperationFailedMessage(ILogger logger, string operation, string errorType);

    [LoggerMessage(EventId = 1000, EventName = ApplicationStartedName, Level = LogLevel.Information, Message = "Sarafan Core {Version} started in {EnvironmentName}.")]
    public static partial void ApplicationStarted(
        ILogger logger,
        string version,
        string environmentName);

    [LoggerMessage(EventId = 1001, EventName = ApplicationStoppedName, Level = LogLevel.Information, Message = "Sarafan Core stopped.")]
    public static partial void ApplicationStopped(ILogger logger);

    [LoggerMessage(EventId = 1100, EventName = MigrationStartedName, Level = LogLevel.Information, Message = "Database migration started.")]
    public static partial void MigrationStarted(ILogger logger);

    [LoggerMessage(EventId = 1101, EventName = MigrationCompletedName, Level = LogLevel.Information, Message = "Database migration completed.")]
    public static partial void MigrationCompleted(ILogger logger);

    public static void MigrationFailed(ILogger logger, Exception exception)
    {
        if (!logger.IsEnabled(LogLevel.Error))
        {
            return;
        }

        using var scope = ErrorTypeScope(logger, exception);
        MigrationFailedMessage(logger);
    }

    public static void RequestCompleted(
        ILogger logger,
        string method,
        string route,
        int statusCode,
        double elapsedMilliseconds)
    {
        if (!logger.IsEnabled(statusCode >= 500 ? LogLevel.Error : LogLevel.Information))
        {
            return;
        }

        using var scope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new("http.request.method", method),
            new("http.route", route),
            new("http.response.status_code", statusCode),
            new("http.server.request.duration", elapsedMilliseconds / 1000d)
        });

        RequestCompletedMessage(
            logger,
            statusCode >= 500 ? LogLevel.Error : LogLevel.Information,
            method,
            route,
            statusCode,
            elapsedMilliseconds);
    }

    public static void ProblemEmitted(
        ILogger logger,
        int statusCode,
        string type,
        string code,
        string instance,
        string traceId)
    {
        if (!logger.IsEnabled(statusCode >= 500 ? LogLevel.Error : LogLevel.Information))
        {
            return;
        }

        using var scope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new("http.response.status_code", statusCode),
            new("sarafan.problem.type", type),
            new("sarafan.problem.code", code),
            new("sarafan.problem.instance", instance),
            new("trace_id", traceId)
        });

        ProblemEmittedMessage(
            logger,
            statusCode >= 500 ? LogLevel.Error : LogLevel.Information,
            code,
            statusCode);
    }

    [LoggerMessage(EventId = 1200, EventName = RequestCompletedName, Message = "HTTP {Method} {Route} completed with status {StatusCode} in {ElapsedMilliseconds:F1} ms.")]
    private static partial void RequestCompletedMessage(
        ILogger logger,
        LogLevel logLevel,
        string method,
        string route,
        int statusCode,
        double elapsedMilliseconds);

    [LoggerMessage(EventId = 1300, EventName = ProblemEmittedName, Message = "Returned RFC 9457 problem {ProblemCode} with status {StatusCode}.")]
    private static partial void ProblemEmittedMessage(
        ILogger logger,
        LogLevel logLevel,
        string problemCode,
        int statusCode);

    public static void UnhandledException(
        ILogger logger,
        Exception exception)
    {
        if (!logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        using var scope = ErrorTypeScope(logger, exception);
        UnhandledExceptionMessage(logger);
    }

    [LoggerMessage(EventId = 1500, EventName = AuthenticationRejectedName, Level = LogLevel.Warning, Message = "Authentication rejected an invalid bearer token.")]
    public static partial void AuthenticationRejected(ILogger logger);

    [LoggerMessage(EventId = 1102, EventName = MigrationFailedName, Level = LogLevel.Error, Message = "Database migration failed.")]
    private static partial void MigrationFailedMessage(ILogger logger);

    [LoggerMessage(EventId = 1400, EventName = UnhandledExceptionName, Level = LogLevel.Warning, Message = "An unhandled exception reached the centralized exception handler.")]
    private static partial void UnhandledExceptionMessage(ILogger logger);

    private static IDisposable? ErrorTypeScope(ILogger logger, Exception exception)
        => logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new("error.type", exception.GetType().FullName ?? exception.GetType().Name)
        });
}
