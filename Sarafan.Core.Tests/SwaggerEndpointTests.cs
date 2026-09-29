// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sarafan.Core.Tests;

[TestFixture]
public sealed class SwaggerEndpointTests
{
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _client = IntegrationTestEnvironment.Factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
    }

    [Test]
    public async Task SwaggerUi_IsUnavailableOutsideDevelopment()
    {
        using var response = await _client.GetAsync("/swagger/index.html");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task SwaggerDocument_IsUnavailableOutsideDevelopment()
    {
        using var response = await _client.GetAsync("/swagger/v1/swagger.json");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task SwaggerAndRootRedirect_AreAvailableInDevelopment()
    {
        using var development = IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development"));
        using var client = development.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var root = await client.GetAsync("/");
        using var swagger = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Multiple(() =>
        {
            Assert.That(root.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            Assert.That(root.Headers.Location?.OriginalString, Is.EqualTo("/swagger"));
            Assert.That(swagger.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }
}
