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
    [Test]
    public async Task ConsentMigrationCreatesTheMvpSchemaWithoutChangingCustomerData()
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
            Assert.That(migrations[^1], Is.EqualTo("20260908181115_0_0_7_CustomerConsents"));
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
            Assert.That(await database.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM customer_consents").SingleAsync(),
                Is.Zero);

            await migrator.MigrateAsync();
            Assert.That(await database.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.customer_consents') IS NULL AS \"Value\"").SingleAsync(), Is.True);
            Assert.That(await database.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.legal_document_audit_events') IS NOT NULL AS \"Value\"").SingleAsync(), Is.True);
            Assert.That(await database.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.customer_consent_withdrawal_requests') IS NOT NULL AS \"Value\"").SingleAsync(), Is.True);
            Assert.That(await database.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.consent_rights_cases') IS NULL AS \"Value\"").SingleAsync(), Is.True);
            Assert.That(await database.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.consent_rights_audit_events') IS NULL AS \"Value\"").SingleAsync(), Is.True);
            Assert.That(await database.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'customer_consent_withdrawal_requests'
                  AND column_name IN ('customer_id', 'requested_at', 'processed')
                """).SingleAsync(), Is.EqualTo(3));
            Assert.That(await database.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'customer_consent_withdrawal_requests'
                """).SingleAsync(), Is.EqualTo(3));
            Assert.That(await database.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value"
                FROM pg_constraint
                WHERE conrelid = 'customer_consent_withdrawal_requests'::regclass
                  AND contype = 'p' AND conkey = ARRAY[
                    (SELECT attnum FROM pg_attribute WHERE attrelid = 'customer_consent_withdrawal_requests'::regclass AND attname = 'customer_id'),
                    (SELECT attnum FROM pg_attribute WHERE attrelid = 'customer_consent_withdrawal_requests'::regclass AND attname = 'requested_at')
                  ]::smallint[]
                """).SingleAsync(), Is.EqualTo(1));
            Assert.That(await database.Database.SqlQueryRaw<bool>("""
                SELECT indexdef LIKE '%UNIQUE INDEX%' AND indexdef LIKE '%WHERE (processed = false)%' AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = 'customer_consent_withdrawal_requests'
                  AND indexname = 'IX_customer_consent_withdrawal_requests_customer_id'
                """).SingleAsync(), Is.True);
            Assert.That(await database.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value"
                FROM pg_constraint
                WHERE conrelid = 'customer_consent_withdrawal_requests'::regclass
                  AND confrelid = 'customers'::regclass
                  AND conname = 'FK_customer_consent_withdrawal_requests_customers_customer_id'
                  AND contype = 'f' AND confdeltype = 'r'
                """).SingleAsync(), Is.EqualTo(1));
            Assert.That(await database.Database.SqlQueryRaw<bool>("""
                SELECT is_nullable = 'NO' AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'legal_documents' AND column_name = 'effective_at'
                """).SingleAsync(), Is.True);
            Assert.That(await database.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name IN ('legal_documents', 'legal_document_audit_events', 'consent_events')
                  AND column_name = 'kind' AND data_type = 'integer'
                """).SingleAsync(), Is.EqualTo(3));
            Assert.That(await database.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value"
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'legal_documents'
                  AND column_name IN ('state', 'updated_at', 'published_at', 'disposed_at', 'revision')
                """).SingleAsync(), Is.Zero);
            Assert.That(await database.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = 'legal_documents'
                  AND indexname IN ('IX_legal_documents_kind_locale_display_version', 'IX_legal_documents_kind_locale_effective_at')
                  AND indexdef LIKE 'CREATE UNIQUE INDEX%'
                """).SingleAsync(), Is.EqualTo(2));
            Assert.That(await database.Database.SqlQueryRaw<bool>("""
                SELECT indexdef LIKE '%WHERE (kind = 0)' AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = 'consent_events'
                  AND indexname = 'IX_consent_events_idempotency_key'
                """).SingleAsync(), Is.True);
            Assert.That(await database.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value"
                FROM pg_constraint
                WHERE conrelid = 'legal_document_audit_events'::regclass
                  AND confrelid = 'backoffice_users'::regclass
                  AND conname = 'FK_legal_document_audit_events_backoffice_users_actor_id'
                  AND contype = 'f' AND confdeltype = 'r'
                """).SingleAsync(), Is.EqualTo(1));
            Assert.That(await database.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*)::int AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = 'legal_document_audit_events'
                  AND indexname = 'IX_legal_document_audit_events_actor_id'
                """).SingleAsync(), Is.EqualTo(1));
            Assert.That(await database.Database.GetAppliedMigrationsAsync(), Does.Contain("20260908181115_0_0_7_CustomerConsents"));
            Assert.That(await database.ConsentEvents.CountAsync(), Is.Zero);
            Assert.That(await database.ConsentAssociations.CountAsync(), Is.Zero);
            Assert.That(await database.LegalDocuments.CountAsync(), Is.Zero);
            Assert.That(await database.CustomerConsentWithdrawalRequests.CountAsync(), Is.Zero);
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
