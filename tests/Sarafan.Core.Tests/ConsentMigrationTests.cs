// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Sarafan.Core.Data;
using Sarafan.Core.Models;

namespace Sarafan.Core.Tests;

[TestFixture, NonParallelizable]
public sealed class ConsentMigrationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ConsentMigrationDropsLegacyDataWithoutChangingCustomerData(bool hasLegacyData)
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var parent = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var builder = new NpgsqlConnectionStringBuilder(parent.Database.GetConnectionString())
        { Database = $"sarafan_consent_migration_test_{Guid.NewGuid():N}", Pooling = false };
        await using var admin = new NpgsqlConnection(parent.Database.GetConnectionString());
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{builder.Database}\"", admin);
        await create.ExecuteNonQueryAsync();
        try
        {
            await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(builder.ConnectionString).Options);
            var migrations = database.Database.GetMigrations().ToArray();
            Assert.That(migrations[^1], Is.EqualTo("20260907125724_VersionedCustomerConsents"));
            var migrator = database.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[^2]);
            var customer = new Customer
            {
                Phone = "+78889999998",
                State = CustomerState.Complete,
                Profile = new() { FirstName = "Migration test" },
                Photo = new() { FileName = "photo.png", ContentType = "image/png", Content = [1, 2, 3], Size = 3 },
                RefreshSessions = [new() { TokenHash = new string('a', 64), FamilyId = Guid.NewGuid() }]
            };
            database.Customers.Add(customer);
            await database.SaveChangesAsync();
            if (hasLegacyData)
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO customer_consents (customer_id, type, document_version, accepted_at)
                    VALUES ({customer.Id}, 'Terms', 'old-terms', {DateTimeOffset.UtcNow}),
                           ({customer.Id}, 'PersonalData', 'old-personal-data', {DateTimeOffset.UtcNow})
                    """);
            Assert.That(await database.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM customer_consents").SingleAsync(),
                Is.EqualTo(hasLegacyData ? 2 : 0));

            await migrator.MigrateAsync();
            Assert.That(await database.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.customer_consents') IS NULL AS \"Value\"").SingleAsync(), Is.True);
            Assert.That(await database.ConsentEvents.CountAsync(), Is.Zero);
            Assert.That(await database.ConsentAssociations.CountAsync(), Is.Zero);
            Assert.That(await database.LegalDocuments.CountAsync(), Is.Zero);
            database.ChangeTracker.Clear();
            var saved = await database.Customers.Include(x => x.Profile).Include(x => x.Photo).Include(x => x.RefreshSessions).SingleAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(saved.Id, Is.EqualTo(customer.Id));
                Assert.That(saved.Phone, Is.EqualTo(customer.Phone));
                Assert.That(saved.State, Is.EqualTo(customer.State));
                Assert.That(saved.Profile.FirstName, Is.EqualTo("Migration test"));
                Assert.That(saved.Photo!.Content, Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(saved.RefreshSessions.Single().TokenHash, Is.EqualTo(new string('a', 64)));
            }

            // Schema rollback recreates an empty table; deleted consent evidence is never restored.
            await migrator.MigrateAsync(migrations[^2]);
            Assert.That(await database.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM customer_consents").SingleAsync(), Is.Zero);
            await migrator.MigrateAsync();
            Assert.That(await database.ConsentEvents.CountAsync(), Is.Zero);
            Assert.That(await database.Customers.CountAsync(), Is.EqualTo(1));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{builder.Database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
