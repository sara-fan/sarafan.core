// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Diagnostics;

using Microsoft.AspNetCore.Routing;

namespace Sarafan.Core.Observability;

public sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var unhandled = false;
        try
        {
            await next(context);
        }
        catch
        {
            unhandled = true;
            throw;
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (!IsHealthRoute(route))
            {
                SarafanEvents.RequestCompleted(
                    logger,
                    SafeMethod(context.Request.Method),
                    route ?? "<unmatched>",
                    unhandled && context.Response.StatusCode < 500
                        ? StatusCodes.Status500InternalServerError
                        : context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
    }

    private static bool IsHealthRoute(string? route)
        => string.Equals(
            route?.TrimStart('/'),
            "api/v1/status/status",
            StringComparison.OrdinalIgnoreCase);

    private static string SafeMethod(string method) => method switch
    {
        "DELETE" or "GET" or "HEAD" or "OPTIONS" or "PATCH" or "POST" or "PUT" => method,
        _ => "OTHER"
    };
}
