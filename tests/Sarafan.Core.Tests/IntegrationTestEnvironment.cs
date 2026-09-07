// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;

namespace Sarafan.Core.Tests;

[SetUpFixture]
public sealed class IntegrationTestEnvironment
{
    public const string BackofficeEmail = "administrator@sarafan.test";
    public const string BackofficePassword = "Backoffice_test_13";

    private static string _databaseName = string.Empty;
    private static string _adminConnectionString = string.Empty;
    private static readonly Dictionary<string, string?> PreviousEnvironment = new(StringComparer.Ordinal);

    public static WebApplicationFactory<Program> Factory { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("SARAFAN_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Set SARAFAN_TEST_POSTGRES to an explicitly disposable PostgreSQL instance.");
        _databaseName = $"sarafan_test_{Guid.NewGuid():N}";

        var adminBuilder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{_databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var applicationBuilder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
            Pooling = false
        };
        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");
        SetEnvironment("ConnectionStrings__DefaultConnection", applicationBuilder.ConnectionString);
        SetEnvironment("Database__ApplyMigrations", "true");
        SetEnvironment("ExchangeRates__Enabled", "false");
        SetEnvironment("Consents__RetentionWorkerEnabled", "false");
        SetEnvironment("Authentication__Issuer", "sarafan.core.tests");
        SetEnvironment("Authentication__Audience", "sarafan.ui.tests");
        SetEnvironment(
            "Authentication__SigningKey",
            "sarafan-tests-signing-key-with-at-least-thirty-two-characters");
        SetEnvironment("Authentication__AccessTokenMinutes", "15");
        SetEnvironment("Authentication__RefreshTokenDays", "30");
        SetEnvironment("Authentication__RefreshCookieName", "sarafan.refresh");
        SetEnvironment("Authentication__SecureCookies", "false");
        SetEnvironment("BackofficeAuthentication__Issuer", "sarafan.core.backoffice.tests");
        SetEnvironment("BackofficeAuthentication__Audience", "sarafan.backoffice.tests");
        SetEnvironment(
            "BackofficeAuthentication__SigningKey",
            "sarafan-backoffice-tests-signing-key-distinct-from-customer-key");
        SetEnvironment("BackofficeAuthentication__AccessTokenMinutes", "15");
        SetEnvironment("BackofficeAuthentication__RefreshTokenDays", "7");
        SetEnvironment("BackofficeAuthentication__RefreshCookieName", "sarafan.backoffice.refresh");
        SetEnvironment("BackofficeAuthentication__BCryptWorkFactor", "10");
        SetEnvironment("BackofficeAuthentication__SecureCookies", "false");
        SetEnvironment("BackofficeBootstrap__Enabled", "true");
        SetEnvironment("BackofficeBootstrap__FirstName", "Maxim");
        SetEnvironment("BackofficeBootstrap__LastName", "Samsonov");
        SetEnvironment("BackofficeBootstrap__Email", BackofficeEmail);
        SetEnvironment("BackofficeBootstrap__Password", BackofficePassword);
        Factory = new TestWebApplicationFactory();
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/status/status");
        response.EnsureSuccessStatusCode();
        await using var scope = Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var initialUsers = await database.BackofficeUsers
            .AsNoTracking()
            .Include(item => item.UserRoles)
            .ToListAsync();
        var documents = scope.ServiceProvider.GetRequiredService<Sarafan.Core.Services.LegalDocumentService>();
        foreach (var kind in Sarafan.Core.Services.ConsentKinds.All)
        {
            var draft = await documents.SaveAsync(null, new Sarafan.Core.RestModels.LegalDocumentRequest
            { Kind = kind, Title = "Тестовый документ", DisplayVersion = "test-v1", FileName = "test.md", Source = System.Text.Encoding.UTF8.GetBytes("# Только для тестов\n\nОтдельный текст документа."), CookieCategories = kind == "cookie-consent" ? ["analytics", "marketing"] : [] }, initialUsers[0].Id, default);
            await documents.PublishAsync(draft.Id, new() { Revision = draft.Revision, Now = true }, initialUsers[0].Id, default);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(initialUsers, Has.Count.EqualTo(1));
            Assert.That(initialUsers[0].FirstName, Is.EqualTo("Maxim"));
            Assert.That(initialUsers[0].LastName, Is.EqualTo("Samsonov"));
            Assert.That(initialUsers[0].NormalizedEmail, Is.EqualTo(BackofficeEmail));
            Assert.That(initialUsers[0].UserRoles.Select(item => item.RoleCode),
                Is.EqualTo(new[] { BackofficeRoles.Administrator }));
            Assert.That(initialUsers[0].IsDemo, Is.True);
        }
    }

    [OneTimeTearDown]
    public async Task TearDown()
    {
        Factory?.Dispose();
        NpgsqlConnection.ClearAllPools();

        if (string.IsNullOrEmpty(_databaseName))
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();

        foreach (var item in PreviousEnvironment)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    private sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            });
        }
    }

    private static void SetEnvironment(string name, string value)
    {
        PreviousEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }
}
