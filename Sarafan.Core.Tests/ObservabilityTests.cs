// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Diagnostics;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
public sealed class ObservabilityTests
{
    private const string IncomingTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string IncomingSpanId = "00f067aa0ba902b7";

    [Test]
    public void EventCatalogue_UsesStableIdsNamesSeveritiesAndReadableMessages()
    {
        var logger = new CaptureLogger<ObservabilityTests>();
        var secretException = new InvalidOperationException("password=database-secret");

        SarafanEvents.ApplicationStarted(logger, "0.0.5", "Testing");
        SarafanEvents.ApplicationStopped(logger);
        SarafanEvents.MigrationStarted(logger);
        SarafanEvents.MigrationCompleted(logger);
        SarafanEvents.MigrationFailed(logger, secretException);
        SarafanEvents.RequestCompleted(logger, "GET", "/api/v1/customers/me", 200, 12.34);
        SarafanEvents.RequestCompleted(logger, "POST", "/api/v1/auth/refresh", 503, 45.67);
        SarafanEvents.ProblemEmitted(
            logger,
            401,
            "https://sarafan.sw.consulting/problems/invalid-access-token",
            "invalid_access_token",
            $"urn:sarafan:problem:{IncomingTraceId}",
            IncomingTraceId);
        SarafanEvents.ProblemEmitted(
            logger,
            500,
            "https://sarafan.sw.consulting/problems/internal-error",
            "internal_error",
            $"urn:sarafan:problem:{IncomingTraceId}",
            IncomingTraceId);
        SarafanEvents.UnhandledException(logger, secretException);
        SarafanEvents.AuthenticationRejected(logger);

        Assert.That(logger.Records.Select(record => record.EventId.Id), Is.EqualTo(new[]
        {
            1000, 1001, 1100, 1101, 1102, 1200, 1200, 1300, 1300, 1400, 1500
        }));
        Assert.That(logger.Records.Select(record => record.EventId.Name), Is.EqualTo(new[]
        {
            SarafanEvents.ApplicationStartedName,
            SarafanEvents.ApplicationStoppedName,
            SarafanEvents.MigrationStartedName,
            SarafanEvents.MigrationCompletedName,
            SarafanEvents.MigrationFailedName,
            SarafanEvents.RequestCompletedName,
            SarafanEvents.RequestCompletedName,
            SarafanEvents.ProblemEmittedName,
            SarafanEvents.ProblemEmittedName,
            SarafanEvents.UnhandledExceptionName,
            SarafanEvents.AuthenticationRejectedName
        }));
        Assert.That(logger.Records.Select(record => record.Level), Is.EqualTo(new[]
        {
            LogLevel.Information,
            LogLevel.Information,
            LogLevel.Information,
            LogLevel.Information,
            LogLevel.Error,
            LogLevel.Information,
            LogLevel.Error,
            LogLevel.Information,
            LogLevel.Error,
            LogLevel.Warning,
            LogLevel.Warning
        }));
        Assert.That(logger.Records, Has.All.Property(nameof(CapturedLog.Message)).Not.Empty);
        Assert.That(string.Join(' ', logger.Records.Select(record => record.Message)),
            Does.Not.Contain("database-secret"));
        Assert.That(logger.Records, Has.All.Property(nameof(CapturedLog.Exception)).Null);
        Assert.That(logger.Records.Single(record => record.EventId.Id == 1400).Scope["error.type"],
            Is.EqualTo(typeof(InvalidOperationException).FullName));
        Assert.That(logger.Records.Single(record => record.EventId.Id == 1400).Message,
            Is.EqualTo("An unhandled exception reached the centralized exception handler."));
    }

    [Test]
    public void EventCatalogue_UsesSemanticAllowlistedScopesAndHonorsFiltering()
    {
        var logger = new CaptureLogger<ObservabilityTests>(LogLevel.Error);
        SarafanEvents.RequestCompleted(logger, "GET", "/api/v1/customers/me", 200, 1);
        SarafanEvents.ProblemEmitted(
            logger,
            400,
            "https://sarafan.sw.consulting/problems/bad-request",
            "bad_request",
            $"urn:sarafan:problem:{IncomingTraceId}",
            IncomingTraceId);
        Assert.That(logger.Records, Is.Empty);

        SarafanEvents.RequestCompleted(logger, "POST", "/api/v1/auth/refresh", 500, 10);
        SarafanEvents.ProblemEmitted(
            logger,
            500,
            "https://sarafan.sw.consulting/problems/internal-error",
            "internal_error",
            $"urn:sarafan:problem:{IncomingTraceId}",
            IncomingTraceId);

        var request = logger.Records[0].Scope;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request["http.request.method"], Is.EqualTo("POST"));
            Assert.That(request["http.route"], Is.EqualTo("/api/v1/auth/refresh"));
            Assert.That(request["http.response.status_code"], Is.EqualTo(500));
            Assert.That(request["http.server.request.duration"], Is.EqualTo(0.01d));
            Assert.That(logger.Records[1].Scope, Does.ContainKey("sarafan.problem.type"));
            Assert.That(logger.Records[1].Scope["sarafan.problem.code"], Is.EqualTo("internal_error"));
            Assert.That(logger.Records[1].Scope["trace_id"], Is.EqualTo(IncomingTraceId));
        }
    }

    [Test]
    public void ProblemFactory_UsesW3CActivityTraceAndEmitsSafeIdentity()
    {
        var logger = new CaptureLogger<SarafanProblemDetailsFactory>();
        using var activity = new Activity("problem-test");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        var context = new DefaultHttpContext();

        var problem = new SarafanProblemDetailsFactory(logger).Create(context, 404, "customer_not_found");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.TraceId, Is.EqualTo(activity.TraceId.ToHexString()));
            Assert.That(problem.TraceId, Does.Match("^[0-9a-f]{32}$"));
            Assert.That(problem.Instance, Is.EqualTo($"urn:sarafan:problem:{problem.TraceId}"));
            Assert.That(logger.Records, Has.Count.EqualTo(3));
            Assert.That(logger.Records.Select(record => record.EventId.Name), Is.EqualTo(new[]
            {
                SarafanEvents.OperationEnteredName, SarafanEvents.ProblemEmittedName, SarafanEvents.OperationExitedName
            }));
            Assert.That(string.Join(' ', logger.Records.Select(record => record.Message)), Does.Not.Contain(problem.Title));
            Assert.That(string.Join(' ', logger.Records.Select(record => record.Message)), Does.Not.Contain(problem.Detail));
        }
    }

    [Test]
    public void TraceIdentifierFallback_IsValidAndStableForOneRequest()
    {
        var context = new DefaultHttpContext();
        var first = SarafanTraceIdentifiers.GetOrCreate(context);
        var second = SarafanTraceIdentifiers.GetOrCreate(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Does.Match("^[0-9a-f]{32}$"));
            Assert.That(second, Is.EqualTo(first));
        }
        Assert.Throws<ArgumentNullException>(() => SarafanTraceIdentifiers.GetOrCreate(null!));
    }

    [Test]
    public void ConsoleFormatter_WritesOneReadableSafeLineWithCorrelation()
    {
        var formatter = new SarafanConsoleFormatter();
        var scopeProvider = new LoggerExternalScopeProvider();
        using var scope = scopeProvider.Push(new List<KeyValuePair<string, object?>>
        {
            new("trace_id", IncomingTraceId),
            new("span_id", IncomingSpanId),
            new("Authorization", "Bearer secret")
        });
        var entry = new LogEntry<string>(
            LogLevel.Warning,
            "Sarafan.Test",
            new EventId(42, "sarafan.core.test.warning"),
            "Readable message.\nSecond line.",
            new InvalidOperationException("secret exception"),
            static (state, _) => state);
        using var writer = new StringWriter();

        formatter.Write(in entry, scopeProvider, writer);
        var output = writer.ToString();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(output, Does.Match(
                "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+Z WARN sarafan\\.core\\.test\\.warning "));
            Assert.That(output, Does.Contain($"trace_id={IncomingTraceId}"));
            Assert.That(output, Does.Contain($"span_id={IncomingSpanId}"));
            Assert.That(output, Does.Contain("Readable message. Second line."));
            Assert.That(output.TrimEnd().Count(character => character == '\n'), Is.Zero);
            Assert.That(output, Does.Not.Contain("Bearer secret"));
            Assert.That(output, Does.Not.Contain("secret exception"));
        }
    }

    [TestCase(LogLevel.Trace, "TRACE")]
    [TestCase(LogLevel.Debug, "DEBUG")]
    [TestCase(LogLevel.Information, "INFO")]
    [TestCase(LogLevel.Warning, "WARN")]
    [TestCase(LogLevel.Error, "ERROR")]
    [TestCase(LogLevel.Critical, "FATAL")]
    [TestCase(LogLevel.None, "NONE")]
    public void ConsoleFormatter_MapsEverySeverity(LogLevel level, string expected)
    {
        var entry = new LogEntry<string>(
            level,
            "Sarafan.Test",
            default,
            "Message",
            null,
            static (state, _) => state);
        using var writer = new StringWriter();

        new SarafanConsoleFormatter().Write(in entry, null, writer);

        Assert.That(writer.ToString(), Does.Contain($" {expected} Sarafan.Test Message"));
    }

    [Test]
    public void ConsoleFormatter_UsesActivityAndSkipsEmptyMessages()
    {
        using var activity = new Activity("console-test");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        var formatter = new SarafanConsoleFormatter();
        var empty = new LogEntry<string>(
            LogLevel.Information,
            "Sarafan.Test",
            default,
            " ",
            null,
            static (state, _) => state);
        using var emptyWriter = new StringWriter();
        formatter.Write(in empty, null, emptyWriter);
        Assert.That(emptyWriter.ToString(), Is.Empty);

        var message = new LogEntry<string>(
            LogLevel.Information,
            "Sarafan.Test",
            default,
            "Message",
            null,
            null!);
        using var writer = new StringWriter();
        formatter.Write(in message, null, writer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.ToString(), Does.Contain($"trace_id={activity.TraceId.ToHexString()}"));
            Assert.That(writer.ToString(), Does.Contain($"span_id={activity.SpanId.ToHexString()}"));
        }
    }

    [Test]
    public void ConsoleFormatter_RedactsFrameworkPayloads()
    {
        var entry = new LogEntry<string>(
            LogLevel.Critical,
            "Microsoft.Hosting.Lifetime",
            new EventId(42, "HostFailed"),
            "Customer /private?token=secret",
            new InvalidOperationException("password=secret"),
            static (state, _) => state);
        using var writer = new StringWriter();

        new SarafanConsoleFormatter().Write(in entry, null, writer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writer.ToString(), Does.Contain(
                "FATAL framework.diagnostic Framework critical event emitted by Microsoft.Hosting.Lifetime."));
            Assert.That(writer.ToString(), Does.Not.Contain("HostFailed"));
            Assert.That(writer.ToString(), Does.Not.Contain("/private"));
            Assert.That(writer.ToString(), Does.Not.Contain("secret"));
        }
    }

    [Test]
    public void LogPolicy_AllowsOnlyStableCategoriesAndMessages()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SarafanLogPolicy.IsApplicationCategory("Sarafan.Core.Service"), Is.True);
            Assert.That(SarafanLogPolicy.IsApplicationCategory("Microsoft.Hosting"), Is.False);
            Assert.That(SarafanLogPolicy.IsApplicationCategory(null), Is.False);
            Assert.That(
                SarafanLogPolicy.SafeFrameworkCategory("Framework.A_B-C+D`1"),
                Is.EqualTo("Framework.A_B-C+D`1"));
            Assert.That(
                SarafanLogPolicy.SafeFrameworkCategory("unsafe/category"),
                Is.EqualTo(SarafanLogPolicy.UnknownFrameworkCategory));
            Assert.That(
                SarafanLogPolicy.SafeFrameworkCategory(null),
                Is.EqualTo(SarafanLogPolicy.UnknownFrameworkCategory));
            Assert.That(
                SarafanLogPolicy.SafeFrameworkCategory(string.Empty),
                Is.EqualTo(SarafanLogPolicy.UnknownFrameworkCategory));
            Assert.That(
                SarafanLogPolicy.SafeFrameworkCategory(new string('A', 161)),
                Is.EqualTo(SarafanLogPolicy.UnknownFrameworkCategory));
            Assert.That(
                SarafanLogPolicy.FrameworkMessage(LogLevel.Information, null),
                Is.EqualTo("Framework diagnostic event emitted by framework."));
        }
    }

    [Test]
    public void HumanReadableOtlpBody_IsSingleLine()
    {
        Assert.That(
            HumanReadableLogRecordProcessor.OneLine("Readable\r\nmessage"),
            Is.EqualTo("Readable message"));

        var exporter = new CapturingLogExporter();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.AddProcessor(new HumanReadableLogRecordProcessor());
            options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
        }));
        loggerFactory.CreateLogger("Sarafan.Test")
            .LogInformation(
                new EventId(1),
                new InvalidOperationException("secret exporter exception"),
                "Readable {Value}\nmessage",
                "formatted");
        loggerFactory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command")
            .LogError(
                new EventId(2, "CommandFailed"),
                new InvalidOperationException("password=secret"),
                "Failed SQL with {Phone}",
                "+79991234567");
        loggerFactory.CreateLogger("unsafe\ncategory")
            .LogWarning("Secret state");

        Assert.That(exporter.Bodies, Is.EqualTo(new[]
        {
            "Readable formatted message",
            "Framework error event emitted by Microsoft.EntityFrameworkCore.Database.Command.",
            "Framework warning event emitted by framework."
        }));
        Assert.That(exporter.HasExceptions, Is.EqualTo(new[] { false, false, false }));
        Assert.That(exporter.Attributes.Skip(1), Has.All.Null);
        Assert.That(exporter.EventNames.Skip(1), Is.EqualTo(new[]
        {
            SarafanLogPolicy.FrameworkEventName,
            SarafanLogPolicy.FrameworkEventName
        }));
        Assert.That(string.Join(' ', exporter.Bodies), Does.Not.Contain("secret"));
        Assert.That(string.Join(' ', exporter.Bodies), Does.Not.Contain("+79991234567"));

        Assert.Throws<ArgumentNullException>(() =>
            new HumanReadableLogRecordProcessor().OnEnd(null!));
    }

    [Test]
    public void Configure_UsesSarafanCategoriesAndReadableConsoleProvider()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Sarafan"] = "Information",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning"
        });
        SarafanObservability.Configure(builder);
        using var app = builder.Build();
        var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(loggerFactory.CreateLogger("Sarafan.Test").IsEnabled(LogLevel.Information),
                Is.True);
            Assert.That(loggerFactory.CreateLogger("Sarafan.Test").IsEnabled(LogLevel.Debug),
                Is.False);
            Assert.That(loggerFactory.CreateLogger("Microsoft.Test").IsEnabled(LogLevel.Information),
                Is.False);
            Assert.That(loggerFactory.CreateLogger("Microsoft.Test").IsEnabled(LogLevel.Warning),
                Is.True);
            Assert.That(loggerFactory.CreateLogger("Microsoft.AspNetCore.Test").IsEnabled(LogLevel.Warning),
                Is.True);
        }
    }

    [Test]
    public void Configure_RegistersOptionalOtlpExporters()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_LOGS_EXPORTER"] = "otlp",
            ["OTEL_TRACES_EXPORTER"] = "otlp",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4317"
        });
        SarafanObservability.Configure(builder);
        using var app = builder.Build();

        Assert.That(app.Services.GetRequiredService<ILoggerFactory>(), Is.Not.Null);
        Assert.That(app.Services.GetRequiredService<TracerProvider>(), Is.Not.Null);
    }

    [Test]
    public void Source_UsesTheStableEventCatalogueInsteadOfDirectLogging()
    {
        var sourceRoot = FindRepositoryRoot();
        var forbidden = new[]
        {
            ".LogTrace(",
            ".LogDebug(",
            ".LogInformation(",
            ".LogWarning(",
            ".LogError(",
            ".LogCritical(",
            ".CreateLogger(\"",
            "Console.Write"
        };
        var violations = Directory
            .EnumerateFiles(
                Path.Combine(sourceRoot, "Sarafan.Core"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .SelectMany(path => forbidden
                .Where(marker => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal))
                .Select(marker => $"{Path.GetRelativePath(sourceRoot, path)}: {marker}"))
            .ToArray();

        Assert.That(violations, Is.Empty);
    }

    [Test]
    public async Task RequestMiddleware_LogsRouteTemplateAndExcludesHealth()
    {
        var logger = new CaptureLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = 204;
                return Task.CompletedTask;
            },
            logger);
        var context = ContextWithRoute("/api/v1/customers/me");
        context.Request.Method = "DELETE";

        await middleware.InvokeAsync(context);
        await middleware.InvokeAsync(ContextWithRoute("/api/v1/status/status"));
        var customMethod = new DefaultHttpContext();
        customMethod.Request.Method = "PERSONAL-DATA";
        await middleware.InvokeAsync(customMethod);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.Records, Has.Count.EqualTo(2));
            Assert.That(logger.Records[0].Message, Does.Contain("DELETE /api/v1/customers/me"));
            Assert.That(logger.Records[0].Message, Does.Contain("status 204"));
            Assert.That(logger.Records[1].Message, Does.Contain("HTTP OTHER <unmatched>"));
            Assert.That(logger.Records[1].Message, Does.Not.Contain("PERSONAL-DATA"));
        }
    }

    [Test]
    public void RequestMiddleware_ReportsUnhandledFailureAsServerError()
    {
        var logger = new CaptureLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(
            _ => throw new InvalidOperationException("secret"),
            logger);
        var context = new DefaultHttpContext();

        Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logger.Records, Has.Count.EqualTo(1));
            Assert.That(logger.Records[0].Level, Is.EqualTo(LogLevel.Error));
            Assert.That(logger.Records[0].Message, Does.Contain("<unmatched>"));
            Assert.That(logger.Records[0].Message, Does.Contain("status 500"));
            Assert.That(logger.Records[0].Message, Does.Not.Contain("secret"));
        }
    }

    [TestCase("LOGS")]
    [TestCase("TRACES")]
    public void OtlpConfiguration_IsOptionalAndHonorsStandardSelectors(string signal)
    {
        Assert.That(SarafanObservability.IsOtlpEnabled(Configuration(), signal), Is.False);
        Assert.That(SarafanObservability.IsOtlpEnabled(Configuration(
            ($"OTEL_{signal}_EXPORTER", "none"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317")), signal), Is.False);
        Assert.That(SarafanObservability.IsOtlpEnabled(Configuration(
            ($"OTEL_{signal}_EXPORTER", "console, otlp")), signal), Is.True);
        Assert.That(SarafanObservability.IsOtlpEnabled(Configuration(
            ($"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT", "http://collector:4318")), signal), Is.True);
        Assert.That(SarafanObservability.IsOtlpEnabled(Configuration(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317")), signal), Is.True);
    }

    [Test]
    [NonParallelizable]
    public async Task IncomingTraceparent_CorrelatesProblemResponse()
    {
        using var client = IntegrationTestEnvironment.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/missing-observability-test");
        request.Headers.TryAddWithoutValidation(
            "traceparent",
            $"00-{IncomingTraceId}-{IncomingSpanId}-01");

        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<SarafanProblemDetails>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(problem.TraceId, Is.EqualTo(IncomingTraceId));
            Assert.That(problem.Instance, Is.EqualTo($"urn:sarafan:problem:{IncomingTraceId}"));
        }
    }

    private static DefaultHttpContext ContextWithRoute(string route)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(route),
            0,
            EndpointMetadataCollection.Empty,
            route));
        return context;
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sarafan.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Sarafan repository root.");
    }

    private sealed class CaptureLogger<T>(LogLevel minimum = LogLevel.Trace) : ILogger<T>
    {
        private IReadOnlyDictionary<string, object?> _scope = new Dictionary<string, object?>();

        public List<CapturedLog> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            var previous = _scope;
            _scope = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>();
            return new Scope(() => _scope = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            Records.Add(new CapturedLog(
                logLevel,
                eventId,
                formatter(state, exception),
                exception,
                new Dictionary<string, object?>(_scope)));
        }

        private sealed class Scope(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed record CapturedLog(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Scope);

    private sealed class CapturingLogExporter : BaseExporter<LogRecord>
    {
        public List<string?> Bodies { get; } = [];

        public List<bool> HasExceptions { get; } = [];

        public List<IReadOnlyList<KeyValuePair<string, object?>>?> Attributes { get; } = [];

        public List<string?> EventNames { get; } = [];

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                Bodies.Add(record.Body);
                HasExceptions.Add(record.Exception is not null);
                Attributes.Add(record.Attributes);
                EventNames.Add(record.EventId.Name);
            }
            return ExportResult.Success;
        }
    }
}
