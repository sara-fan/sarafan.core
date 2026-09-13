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

    public async Task<bool> InsertExchangeRateAsync(
        AppDbContext database,
        CbrRate rate,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken)
        => await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO exchange_rate_history
                (provider, source, base_currency, quote_currency, nominal, official_rate, source_effective_date, retrieved_at)
            VALUES ({"CBR"}, {CbrRateClient.Endpoint}, {"USD"}, {"RUB"}, {rate.Nominal},
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
        IQueryable<LegalDocumentAuditEvent> query,
        string search,
        Guid? documentId)
        => query.Where(item => EF.Functions.ILike(item.Title, $"%{search}%")
            || EF.Functions.ILike(item.DisplayVersion, $"%{search}%")
            || documentId != null && item.DocumentId == documentId);

    public IQueryable<CustomerConsentWithdrawalRequest> ApplyWithdrawalSearch(
        IQueryable<CustomerConsentWithdrawalRequest> query,
        string search)
        => query.Where(item => EF.Functions.Like(item.CustomerId.ToString(), $"%{search}%"));
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
