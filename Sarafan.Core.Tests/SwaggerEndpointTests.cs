// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Text.Json;

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
    public async Task SwaggerUi_IsAvailable()
    {
        using var response = await _client.GetAsync("/swagger/index.html");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task SwaggerDocument_DescribesApiAndBearerAuthentication()
    {
        using var response = await _client.GetAsync("/swagger/v1/swagger.json");
        await using var responseStream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(responseStream);
        var root = document.RootElement;
        var bearerScheme = root
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("Bearer");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(root.GetProperty("info").GetProperty("title").GetString(), Is.EqualTo("Sarafan Core Api"));
            Assert.That(root.GetProperty("info").GetProperty("version").GetString(), Is.EqualTo("v1"));
            Assert.That(root.GetProperty("paths").TryGetProperty("/api/v1/Status/status", out _), Is.True);
            Assert.That(bearerScheme.GetProperty("type").GetString(), Is.EqualTo("http"));
            Assert.That(bearerScheme.GetProperty("scheme").GetString(), Is.EqualTo("bearer"));
            Assert.That(root.GetProperty("security")[0].TryGetProperty("Bearer", out _), Is.True);
            Assert.That(root.GetProperty("components").GetProperty("schemas").GetProperty("VerifyCodeRequest")
                .GetProperty("properties").EnumerateObject().Select(property => property.Name),
                Is.EquivalentTo(new[] { "phone", "code", "onboardingToken" }));
        }
    }
}
