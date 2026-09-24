// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Diagnostics;

using Microsoft.Extensions.Logging.Console;

using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Sarafan.Core.Observability;

public static class SarafanObservability
{
    public const string ServiceName = "sarafan.core";

    public static void Configure(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.FormatterName = SarafanConsoleFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<SarafanConsoleFormatter, ConsoleFormatterOptions>();
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            options.SetResourceBuilder(CreateResource(builder));
            options.AddProcessor(new HumanReadableLogRecordProcessor());
            if (IsOtlpEnabled(builder.Configuration, "LOGS"))
            {
                options.AddOtlpExporter();
            }
        });

        var telemetry = builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => ConfigureResource(resource, builder));
        telemetry.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation(options =>
                options.Filter = context => !context.Request.Path.StartsWithSegments(
                    "/api/v1/status/status"));
            if (IsOtlpEnabled(builder.Configuration, "TRACES"))
            {
                tracing.AddOtlpExporter();
            }
        });
    }

    internal static bool IsOtlpEnabled(IConfiguration configuration, string signal)
    {
        var exporters = configuration[$"OTEL_{signal}_EXPORTER"];
        if (!string.IsNullOrWhiteSpace(exporters))
        {
            return exporters
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains("otlp", StringComparer.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(configuration[$"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT"])
            || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    private static ResourceBuilder CreateResource(WebApplicationBuilder builder)
        => ConfigureResource(ResourceBuilder.CreateDefault(), builder);

    private static ResourceBuilder ConfigureResource(
        ResourceBuilder resource,
        WebApplicationBuilder builder)
    {
        resource.AddService(
            ServiceName,
            serviceVersion: VersionInfo.AppVersion,
            serviceInstanceId: builder.Configuration["Observability:ServiceInstanceId"]);
        resource.AddAttributes(new Dictionary<string, object>
        {
            ["deployment.environment.name"] = builder.Environment.EnvironmentName
        });
        return resource;
    }
}
