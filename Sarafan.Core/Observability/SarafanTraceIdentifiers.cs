// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Diagnostics;

namespace Sarafan.Core.Observability;

public static class SarafanTraceIdentifiers
{
    private static readonly object TraceIdItemKey = new();

    public static string GetOrCreate(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Activity.Current is { } activity)
        {
            return activity.TraceId.ToHexString();
        }

        if (context.Items.TryGetValue(TraceIdItemKey, out var value)
            && value is string traceId)
        {
            return traceId;
        }

        var generated = ActivityTraceId.CreateRandom().ToHexString();
        context.Items[TraceIdItemKey] = generated;
        return generated;
    }
}
