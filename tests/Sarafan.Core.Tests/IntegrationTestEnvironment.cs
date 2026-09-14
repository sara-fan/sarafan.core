// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[SetUpFixture]
public sealed class IntegrationTestEnvironment
{
    public const string BackofficeEmail = "administrator@sarafan.test";
    public const string BackofficePassword = "Backoffice_test_13";

    public static WebApplicationFactory<Program> Factory { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        Factory = new TestWebApplicationFactory($"sarafan_test_{Guid.NewGuid():N}");
        using var client = Factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/status/status");
        response.EnsureSuccessStatusCode();
        await ResetAsync();
    }

    public static async Task ResetAsync()
    {
        Factory.Services.GetRequiredService<VerificationAttemptStore>().Reset();
        await using var scope = Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Database.EnsureDeletedAsync();
        await database.Database.EnsureCreatedAsync();
        await scope.ServiceProvider.GetRequiredService<BackofficeBootstrapService>()
            .ProvisionAsync(default);
        var initialUsers = await database.BackofficeUsers
            .AsNoTracking()
            .Include(item => item.UserRoles)
            .ToListAsync();
        var documents = scope.ServiceProvider.GetRequiredService<Sarafan.Core.Services.LegalDocumentService>();
        foreach (var kind in Enum.GetValues<Sarafan.Core.Models.LegalDocumentKind>())
        {
            await documents.CreateAsync(new Sarafan.Core.RestModels.LegalDocumentRequest
            {
                Kind = kind,
                Title = "Тестовый документ",
                DisplayVersion = "test-v1",
                FileName = "test.md",
                Source = System.Text.Encoding.UTF8.GetBytes("# Только для тестов\n\nОтдельный текст документа."),
                EffectiveDate = Sarafan.Core.Services.ConsentCalendar.LocalDate(DateTimeOffset.UtcNow)
            }, initialUsers[0].Id, default);
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
        if (Factory is null)
        {
            return;
        }

        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
        Factory.Dispose();
    }

    private sealed class TestWebApplicationFactory(string databaseName) : WebApplicationFactory<Program>
    {
        private readonly InMemoryDatabaseRoot _databaseRoot = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=unused;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("Database:ApplyMigrations", "false");
            builder.UseSetting("ExchangeRates:Enabled", "false");
            builder.UseSetting("Consents:RetentionWorkerEnabled", "false");
            builder.UseSetting("Authentication:Issuer", "sarafan.core.tests");
            builder.UseSetting("Authentication:Audience", "sarafan.ui.tests");
            builder.UseSetting("Authentication:SigningKey", "sarafan-tests-signing-key-with-at-least-thirty-two-characters");
            builder.UseSetting("Authentication:AccessTokenMinutes", "15");
            builder.UseSetting("Authentication:RefreshTokenDays", "30");
            builder.UseSetting("Authentication:RefreshCookieName", "sarafan.refresh");
            builder.UseSetting("Authentication:SecureCookies", "false");
            builder.UseSetting("BackofficeAuthentication:Issuer", "sarafan.core.backoffice.tests");
            builder.UseSetting("BackofficeAuthentication:Audience", "sarafan.backoffice.tests");
            builder.UseSetting("BackofficeAuthentication:SigningKey", "sarafan-backoffice-tests-signing-key-distinct-from-customer-key");
            builder.UseSetting("BackofficeAuthentication:AccessTokenMinutes", "15");
            builder.UseSetting("BackofficeAuthentication:RefreshTokenDays", "7");
            builder.UseSetting("BackofficeAuthentication:RefreshCookieName", "sarafan.backoffice.refresh");
            builder.UseSetting("BackofficeAuthentication:BCryptWorkFactor", "10");
            builder.UseSetting("BackofficeAuthentication:SecureCookies", "false");
            builder.UseSetting("BackofficeBootstrap:Enabled", "true");
            builder.UseSetting("BackofficeBootstrap:FirstName", "Maxim");
            builder.UseSetting("BackofficeBootstrap:LastName", "Samsonov");
            builder.UseSetting("BackofficeBootstrap:Email", BackofficeEmail);
            builder.UseSetting("BackofficeBootstrap:Password", BackofficePassword);
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            });
            builder.ConfigureServices(services =>
            {
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
                services.RemoveAll<AppDbContext>();
                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase(databaseName, _databaseRoot));
            });
        }
    }
}
