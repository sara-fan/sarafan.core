// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sarafan.Core.Tests;

[TestFixture]
public sealed class ForwardedHeadersConfigurationTests
{
    [Test]
    public async Task Middleware_ProcessesEdgeAndUiProxyHops()
    {
        using var services = CreateServices();
        IPAddress? observedAddress = null;
        var observedScheme = string.Empty;
        var application = new ApplicationBuilder(services);
        application.UseForwardedHeaders();
        application.Run(context =>
        {
            observedAddress = context.Connection.RemoteIpAddress;
            observedScheme = context.Request.Scheme;
            return Task.CompletedTask;
        });
        var context = CreateContext(
            services,
            "192.0.2.99, 198.51.100.8, 172.20.0.2",
            "https",
            "172.20.0.3");

        await application.Build()(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observedAddress, Is.EqualTo(IPAddress.Parse("198.51.100.8")));
            Assert.That(observedScheme, Is.EqualTo("https"));
        }
    }

    [Test]
    public async Task Middleware_DoesNotTrustForwardedHeadersFromPublicPeer()
    {
        using var services = CreateServices();
        IPAddress? observedAddress = null;
        var observedScheme = string.Empty;
        var application = new ApplicationBuilder(services);
        application.UseForwardedHeaders();
        application.Run(context =>
        {
            observedAddress = context.Connection.RemoteIpAddress;
            observedScheme = context.Request.Scheme;
            return Task.CompletedTask;
        });
        var context = CreateContext(services, "203.0.113.9", "https", "198.51.100.10");

        await application.Build()(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observedAddress, Is.EqualTo(IPAddress.Parse("198.51.100.10")));
            Assert.That(observedScheme, Is.EqualTo("http"));
        }
    }

    [Test]
    public async Task Middleware_DoesNotTrustPrivateNetworksByDefault()
    {
        using var services = CreateServices(configureCloudTrust: false);
        IPAddress? observedAddress = null;
        var observedScheme = string.Empty;
        var application = new ApplicationBuilder(services);
        application.UseForwardedHeaders();
        application.Run(context =>
        {
            observedAddress = context.Connection.RemoteIpAddress;
            observedScheme = context.Request.Scheme;
            return Task.CompletedTask;
        });
        var context = CreateContext(services, "198.51.100.8", "https", "172.20.0.3");

        await application.Build()(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observedAddress, Is.EqualTo(IPAddress.Parse("172.20.0.3")));
            Assert.That(observedScheme, Is.EqualTo("http"));
        }
    }

    private static ServiceProvider CreateServices(bool configureCloudTrust = true)
    {
        var values = configureCloudTrust
            ? new Dictionary<string, string?>
            {
                ["ForwardedHeaders:ForwardLimit"] = "2",
                ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8",
                ["ForwardedHeaders:KnownNetworks:1"] = "172.16.0.0/12",
                ["ForwardedHeaders:KnownNetworks:2"] = "192.168.0.0/16",
                ["ForwardedHeaders:KnownNetworks:3"] = "fc00::/7"
            }
            : [];
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<ForwardedHeadersOptions>(options =>
            ForwardedHeadersConfiguration.Configure(options, configuration));
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(
        IServiceProvider services,
        string forwardedFor,
        string forwardedProto,
        string remoteAddress)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        return context;
    }
}
