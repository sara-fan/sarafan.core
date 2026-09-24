// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Diagnostics;
using System.Globalization;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Sarafan.Core.Observability;

public sealed class SarafanConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "sarafan";

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        ArgumentNullException.ThrowIfNull(textWriter);

        var isApplicationEvent = SarafanLogPolicy.IsApplicationCategory(logEntry.Category);
        var message = isApplicationEvent
            ? logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception)
                ?? logEntry.State?.ToString()
            : SarafanLogPolicy.FrameworkMessage(logEntry.LogLevel, logEntry.Category);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var activity = Activity.Current;
        var trace = TraceFields.From(scopeProvider);
        if (activity is not null)
        {
            trace.TraceId = activity.TraceId.ToHexString();
            trace.SpanId = activity.SpanId.ToHexString();
        }

        var eventName = isApplicationEvent
            ? logEntry.EventId.Name ?? logEntry.Category
            : SarafanLogPolicy.FrameworkEventName;
        var oneLineMessage = message
            .Replace("\r\n", " ")
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        textWriter.Write(DateTimeOffset.UtcNow.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture));
        textWriter.Write(' ');
        textWriter.Write(Severity(logEntry.LogLevel));
        textWriter.Write(' ');
        textWriter.Write(eventName);
        if (!string.IsNullOrEmpty(trace.TraceId))
        {
            textWriter.Write(" trace_id=");
            textWriter.Write(trace.TraceId);
        }

        if (!string.IsNullOrEmpty(trace.SpanId))
        {
            textWriter.Write(" span_id=");
            textWriter.Write(trace.SpanId);
        }

        textWriter.Write(' ');
        textWriter.WriteLine(oneLineMessage);
    }

    private static string Severity(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "NONE"
    };

    private sealed class TraceFields
    {
        public string? TraceId { get; set; }

        public string? SpanId { get; set; }

        public static TraceFields From(IExternalScopeProvider? scopeProvider)
        {
            var result = new TraceFields();
            scopeProvider?.ForEachScope((scope, fields) =>
            {
                if (scope is not IEnumerable<KeyValuePair<string, object?>> values)
                {
                    return;
                }

                foreach (var (key, value) in values)
                {
                    if (key == "trace_id" && value is string traceId)
                    {
                        fields.TraceId = traceId;
                    }
                    else if (key == "span_id" && value is string spanId)
                    {
                        fields.SpanId = spanId;
                    }
                }
            }, result);
            return result;
        }
    }
}
