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
using Sarafan.Core.Models;
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
        database.IanaTldCatalog.Add(CreateIanaTldCatalog());
        await database.SaveChangesAsync();
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
            DisableJob(builder, nameof(ScheduledJobsOptions.ExchangeRates));
            DisableJob(builder, nameof(ScheduledJobsOptions.ConsentRetention));
            DisableJob(builder, nameof(ScheduledJobsOptions.IanaTldUpdate));
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

        private static void DisableJob(IWebHostBuilder builder, string name)
        {
            builder.UseSetting($"ScheduledJobs:{name}:Cron", string.Empty);
            builder.UseSetting($"ScheduledJobs:{name}:RunOnStartup", "false");
        }
    }

    internal static IanaTldCatalog CreateIanaTldCatalog(string version = "2026091400")
    {
        var values = Enumerable.Range(0, 1_100)
            .Select(index => $"T{index:D4}")
            .Append("COM")
            .Append("XN--P1AI")
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new IanaTldCatalog
        {
            Version = version,
            Source = IanaTldClient.Endpoint,
            SourceUpdatedAt = DateTimeOffset.Parse("2026-09-14T07:07:01Z"),
            RetrievedAt = DateTimeOffset.Parse("2026-09-14T07:08:00Z"),
            ContentSha256 = new string('0', 64),
            TopLevelDomains = values
        };
    }
}
