// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Diagnostics.CodeAnalysis;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

using Sarafan.Core.Models;
using Sarafan.Core.Services;

namespace Sarafan.Core.Data;

[ExcludeFromCodeCoverage]
internal sealed class PostgreSqlAppDatabaseOperations : IAppDatabaseOperations
{
    private const int CustomerLockNamespace = 938802021;
    private const string AdministratorMutationLockSql = "SELECT pg_advisory_xact_lock(1397301386)";
    private const string IanaTldCatalogLockSql = "SELECT pg_advisory_xact_lock(1397315804)";
    private const string CustomerOrderCodeIndex = "ux_customers_order_code";

    internal static PostgreSqlAppDatabaseOperations Instance { get; } = new();

    private PostgreSqlAppDatabaseOperations()
    {
    }

    public async Task<IAppDatabaseTransaction> BeginTransactionAsync(
        AppDbContext database,
        CancellationToken cancellationToken)
        => new PostgreSqlAppDatabaseTransaction(
            await database.Database.BeginTransactionAsync(cancellationToken));

    public Task LockConsentsAsync(AppDbContext database, CancellationToken cancellationToken)
        => database.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(938802020)",
            cancellationToken);

    public Task LockCustomerAsync(
        AppDbContext database,
        int customerId,
        CancellationToken cancellationToken)
        => database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({CustomerLockNamespace}, {customerId})",
            cancellationToken);

    public Task LockAdministratorMutationsAsync(
        AppDbContext database,
        CancellationToken cancellationToken)
        => database.Database.ExecuteSqlRawAsync(
            AdministratorMutationLockSql,
            cancellationToken);

    public Task LockPaymentInformationMutationsAsync(AppDbContext database, CancellationToken cancellationToken)
        => database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1397315807)", cancellationToken);

    public Task LockStoreMutationsAsync(AppDbContext database, CancellationToken cancellationToken)
        => database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1397315805)", cancellationToken);

    public Task LockServiceCatalogueMutationsAsync(AppDbContext database, CancellationToken cancellationToken)
        => database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1397315806)", cancellationToken);

    public Task LockLogibooksAsync(AppDbContext database, CancellationToken cancellationToken)
        => database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1397315808)", cancellationToken);

    public IQueryable<LogibooksRegisterRow> SearchLogibooksRows(AppDbContext database, Guid id, string search)
    {
        var term = ListDisplaySearch.Normalize(search);
        // strpos is a literal substring comparison; %, _ and backslashes have no wildcard meaning.
        return database.Set<LogibooksRegisterRow>().FromSqlInterpolated($"""
            SELECT r.* FROM logibooks_register_rows r
            JOIN logibooks_registers o ON o.id = r.register_id
            CROSS JOIN LATERAL (SELECT o.request->'parcels'->(r.row_number - 1) AS goods) g
            WHERE r.register_id = {id} AND EXISTS (
                SELECT 1 FROM (VALUES
                    (g.goods->>'postingNumber'), (g.goods->>'tnVed'), (g.goods->>'productName'), (g.goods->>'productLink'),
                    (g.goods->>'lastName'), (g.goods->>'firstName'), (g.goods->>'patronymic'),
                    (g.goods->>'phone'), (g.goods->>'email'), (g.goods->>'postalCode'), (g.goods->>'city'), (g.goods->>'address'),
                    (concat_ws(' ', nullif(g.goods->>'passportSeries', ''), nullif(g.goods->>'passportNumber', ''),
                        CASE WHEN nullif(g.goods->>'passportIssuedBy', '') IS NOT NULL OR g.goods->>'passportIssueDate' IS NOT NULL THEN 'выдан' END,
                        nullif(g.goods->>'passportIssuedBy', ''), to_char((g.goods->>'passportIssueDate')::date, 'DD.MM.YYYY'))),
                    (g.goods->>'inn'), (g.goods->>'sender'), (g.goods->>'senderPhone'), (g.goods->>'senderEmail'),
                    (g.goods->>'senderAddress'), (g.goods->>'countryCode'), (g.goods->>'senderCountryCode'), (g.goods->>'tradingCountryCode'),
                    (replace(replace(to_char((g.goods->>'unitPrice')::numeric,
                        'FM999,999,999,999,999,999,990.00'), ',', ' '), '.', ',') || {Currency.Usd.GetSymbol()}),
                    ({Currency.Usd.GetSymbol()}), (g.goods->>'quantity'),
                    (replace(to_char((g.goods->>'weightKg')::numeric, 'FM999999999999999990.000'), '.', ',')),
                    (CASE r.outcome WHEN 1 THEN 'Создана' WHEN 2 THEN 'Отклонена' ELSE 'Результат неизвестен' END),
                    (coalesce(r.parcel_id::text, '—')),
                    (coalesce((SELECT string_agg(issue->>'msg', '; ' ORDER BY ordinal)
                        FROM jsonb_array_elements(r.issues) WITH ORDINALITY AS i(issue, ordinal)), '—'))
                ) AS fields(value)
                WHERE strpos(lower(trim(replace(replace(coalesce(nullif(fields.value, ''), '—'), chr(160), ' '), chr(8239), ' '))), {term}) > 0)
            """);
    }

    public Task LockOrdersAsync(AppDbContext database, long[] ids, CancellationToken cancellationToken)
        => database.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM orders WHERE id = ANY({ids}) ORDER BY id FOR UPDATE", cancellationToken);

    public Task LockIanaTldCatalogAsync(
        AppDbContext database,
        CancellationToken cancellationToken)
        => database.Database.ExecuteSqlRawAsync(
            IanaTldCatalogLockSql,
            cancellationToken);

    public Task<Customer?> FindCustomerForUpdateAsync(
        AppDbContext database,
        int customerId,
        CancellationToken cancellationToken)
        => database.Customers
            .FromSqlInterpolated($"SELECT * FROM customers WHERE id = {customerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public bool IsCustomerOrderCodeCollision(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CustomerOrderCodeIndex
        };

    public bool IsStoreDisplayOrderCollision(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_stores_display_order"
        };

    public bool IsServiceCataloguePeriodCollision(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ExclusionViolation,
            ConstraintName: "ex_service_catalogue_entries_service_period"
        };

    public async Task<bool> InsertExchangeRateAsync(
        AppDbContext database,
        CbrRate rate,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken)
        => await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO exchange_rate_history
                (provider, source, base_currency, quote_currency, nominal, official_rate, source_effective_date, retrieved_at)
            VALUES ({"CBR"}, {CbrRateClient.Endpoint}, {(int)rate.BaseCurrency}, {(int)Currency.Rub}, {rate.Nominal},
                {rate.OfficialRate}, {rate.SourceEffectiveDate}, {retrievedAt})
            ON CONFLICT (provider, base_currency, quote_currency, source_effective_date) DO NOTHING
            """, cancellationToken) == 1;

    public Task<int> DeleteAsync<TEntity>(
        AppDbContext database,
        IQueryable<TEntity> query,
        CancellationToken cancellationToken)
        where TEntity : class
        => query.ExecuteDeleteAsync(cancellationToken);

    public Task<int> MarkWithdrawalProcessedAsync(
        AppDbContext database,
        IQueryable<CustomerConsentWithdrawalRequest> query,
        CancellationToken cancellationToken)
        => query.ExecuteUpdateAsync(
            properties => properties.SetProperty(candidate => candidate.Processed, true),
            cancellationToken);

    public IQueryable<LegalDocumentAuditEvent> ApplyLegalDocumentAuditSearch(
        IQueryable<LegalDocumentAuditEvent> query, string search)
        => ListDisplaySearch.LegalAudit(query, search);

    public IQueryable<CustomerConsentWithdrawalRequest> ApplyWithdrawalSearch(
        IQueryable<CustomerConsentWithdrawalRequest> query, string search)
        => ListDisplaySearch.Withdrawals(query, search);

    public IQueryable<Order> ApplyOrderSearch(IQueryable<Order> query, string search)
        => ListDisplaySearch.Orders(query, search);

    public IQueryable<Store> ApplyStoreSearch(IQueryable<Store> query, string search)
    {
        var escaped = search
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return query.Where(item => EF.Functions.ILike(item.Name, $"%{escaped}%", "\\"));
    }

    public IQueryable<ServiceCatalogueAuditEvent> ApplyServiceCatalogueAuditSearch(
        AppDbContext database, IQueryable<ServiceCatalogueAuditEvent> query, string search)
        => PostgreSqlCatalogueSearch.Apply(database, query, search);

}

[ExcludeFromCodeCoverage]
internal sealed class PostgreSqlAppDatabaseTransaction(IDbContextTransaction transaction) : IAppDatabaseTransaction
{
    public Task CommitAsync(CancellationToken cancellationToken)
        => transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken)
        => transaction.RollbackAsync(cancellationToken);

    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}
