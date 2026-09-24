// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;

using Microsoft.AspNetCore.HttpOverrides;

namespace Sarafan.Core;

internal static class ForwardedHeadersConfiguration
{
    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;
        if (options.ForwardLimit <= 0)
        {
            throw new InvalidOperationException("ForwardedHeaders:ForwardLimit must be greater than zero");
        }

        foreach (var configuredNetwork in configuration
            .GetSection("ForwardedHeaders:KnownNetworks")
            .Get<string[]>() ?? [])
        {
            if (!System.Net.IPNetwork.TryParse(configuredNetwork, out var network))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks contains an invalid network: {configuredNetwork}");
            }

            options.KnownIPNetworks.Add(network);
        }

        foreach (var configuredProxy in configuration
            .GetSection("ForwardedHeaders:KnownProxies")
            .Get<string[]>() ?? [])
        {
            if (!IPAddress.TryParse(configuredProxy, out var proxy))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownProxies contains an invalid IP address: {configuredProxy}");
            }

            options.KnownProxies.Add(proxy);
        }
    }
}
