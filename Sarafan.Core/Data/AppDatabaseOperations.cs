// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Models;
using Sarafan.Core.Services;

namespace Sarafan.Core.Data;

internal interface IAppDatabaseTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}

internal interface IAppDatabaseOperations
{
    Task<IAppDatabaseTransaction> BeginTransactionAsync(AppDbContext database, CancellationToken cancellationToken);
    Task LockConsentsAsync(AppDbContext database, CancellationToken cancellationToken);
    Task LockCustomerAsync(AppDbContext database, int customerId, CancellationToken cancellationToken);
    Task LockAdministratorMutationsAsync(AppDbContext database, CancellationToken cancellationToken);
    Task LockStoreMutationsAsync(AppDbContext database, CancellationToken cancellationToken);
    Task LockServiceCatalogueMutationsAsync(AppDbContext database, CancellationToken cancellationToken);
    Task LockIanaTldCatalogAsync(AppDbContext database, CancellationToken cancellationToken);
    Task<Customer?> FindCustomerForUpdateAsync(AppDbContext database, int customerId, CancellationToken cancellationToken);
    bool IsCustomerOrderCodeCollision(DbUpdateException exception);
    bool IsStoreDisplayOrderCollision(DbUpdateException exception);
    bool IsServiceCataloguePeriodCollision(DbUpdateException exception);
    Task<bool> InsertExchangeRateAsync(
        AppDbContext database,
        CbrRate rate,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken);
    Task<int> DeleteAsync<TEntity>(
        AppDbContext database,
        IQueryable<TEntity> query,
        CancellationToken cancellationToken)
        where TEntity : class;
    Task<int> MarkWithdrawalProcessedAsync(
        AppDbContext database,
        IQueryable<CustomerConsentWithdrawalRequest> query,
        CancellationToken cancellationToken);
    IQueryable<LegalDocumentAuditEvent> ApplyLegalDocumentAuditSearch(
        IQueryable<LegalDocumentAuditEvent> query,
        string search,
        Guid? documentId);
    IQueryable<CustomerConsentWithdrawalRequest> ApplyWithdrawalSearch(
        IQueryable<CustomerConsentWithdrawalRequest> query,
        string search);
    IQueryable<Order> ApplyOrderSearch(IQueryable<Order> query, string search);
    IQueryable<Store> ApplyStoreSearch(IQueryable<Store> query, string search);
    IQueryable<ServiceCatalogueAuditEvent> ApplyServiceCatalogueAuditSearch(
        IQueryable<ServiceCatalogueAuditEvent> query,
        string search,
        long? entryId);
}

internal static class AppDatabaseOperations
{
    internal static IAppDatabaseOperations For(AppDbContext database)
        => database.Database.IsRelational()
            ? PostgreSqlAppDatabaseOperations.Instance
            : InMemoryAppDatabaseOperations.Instance;
}

internal sealed class InMemoryAppDatabaseOperations : IAppDatabaseOperations
{
    internal static InMemoryAppDatabaseOperations Instance { get; } = new();

    private InMemoryAppDatabaseOperations()
    {
    }

    public Task<IAppDatabaseTransaction> BeginTransactionAsync(
        AppDbContext database,
        CancellationToken cancellationToken)
        => Task.FromResult<IAppDatabaseTransaction>(InMemoryAppDatabaseTransaction.Instance);

    public Task LockConsentsAsync(AppDbContext database, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task LockCustomerAsync(
        AppDbContext database,
        int customerId,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task LockAdministratorMutationsAsync(
        AppDbContext database,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task LockStoreMutationsAsync(AppDbContext database, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task LockServiceCatalogueMutationsAsync(AppDbContext database, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task LockIanaTldCatalogAsync(
        AppDbContext database,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<Customer?> FindCustomerForUpdateAsync(
        AppDbContext database,
        int customerId,
        CancellationToken cancellationToken)
        => database.Customers.SingleOrDefaultAsync(customer => customer.Id == customerId, cancellationToken);

    public bool IsCustomerOrderCodeCollision(DbUpdateException exception) => false;
    public bool IsStoreDisplayOrderCollision(DbUpdateException exception) => false;
    public bool IsServiceCataloguePeriodCollision(DbUpdateException exception) => false;

    public async Task<bool> InsertExchangeRateAsync(
        AppDbContext database,
        CbrRate rate,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken)
    {
        var exists = await database.ExchangeRateHistory.AnyAsync(
            item => item.Provider == "CBR"
                && item.BaseCurrency == rate.BaseCurrency
                && item.QuoteCurrency == Currency.Rub
                && item.SourceEffectiveDate == rate.SourceEffectiveDate,
            cancellationToken);
        if (exists)
        {
            return false;
        }

        database.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Provider = "CBR",
            Source = CbrRateClient.Endpoint,
            BaseCurrency = rate.BaseCurrency,
            QuoteCurrency = Currency.Rub,
            Nominal = rate.Nominal,
            OfficialRate = rate.OfficialRate,
            SourceEffectiveDate = rate.SourceEffectiveDate,
            RetrievedAt = retrievedAt
        });
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<int> DeleteAsync<TEntity>(
        AppDbContext database,
        IQueryable<TEntity> query,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var rows = await query.ToArrayAsync(cancellationToken);
        database.RemoveRange(rows);
        return rows.Length;
    }

    public async Task<int> MarkWithdrawalProcessedAsync(
        AppDbContext database,
        IQueryable<CustomerConsentWithdrawalRequest> query,
        CancellationToken cancellationToken)
    {
        var rows = await query.ToArrayAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.Processed = true;
        }

        return rows.Length;
    }

    public IQueryable<LegalDocumentAuditEvent> ApplyLegalDocumentAuditSearch(
        IQueryable<LegalDocumentAuditEvent> query,
        string search,
        Guid? documentId)
        => query.Where(item => item.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.DisplayVersion.Contains(search, StringComparison.OrdinalIgnoreCase)
            || documentId != null && item.DocumentId == documentId);

    public IQueryable<CustomerConsentWithdrawalRequest> ApplyWithdrawalSearch(
        IQueryable<CustomerConsentWithdrawalRequest> query,
        string search)
        => query.Where(item => item.CustomerId.ToString().Contains(search, StringComparison.Ordinal));

    public IQueryable<Order> ApplyOrderSearch(IQueryable<Order> query, string search)
        => query.Where(item =>
            $"{item.Customer.OrderCode}-{item.CustomerOrderNumber}".Contains(
                search,
                StringComparison.OrdinalIgnoreCase)
            || item.SourceUrl.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.ProductName != null && item.ProductName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.StoreName != null && item.StoreName.Contains(search, StringComparison.OrdinalIgnoreCase));

    public IQueryable<Store> ApplyStoreSearch(IQueryable<Store> query, string search)
        => query.Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

    public IQueryable<ServiceCatalogueAuditEvent> ApplyServiceCatalogueAuditSearch(
        IQueryable<ServiceCatalogueAuditEvent> query,
        string search,
        long? entryId)
        => query.Where(item => item.ActorName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entryId != null && item.EntryId == entryId);
}

internal sealed class InMemoryAppDatabaseTransaction : IAppDatabaseTransaction
{
    internal static InMemoryAppDatabaseTransaction Instance { get; } = new();

    private InMemoryAppDatabaseTransaction()
    {
    }

    public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
