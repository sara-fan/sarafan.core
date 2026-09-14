// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();
    public DbSet<CustomerPhoto> CustomerPhotos => Set<CustomerPhoto>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    public DbSet<BackofficeUser> BackofficeUsers => Set<BackofficeUser>();
    public DbSet<BackofficeRole> BackofficeRoles => Set<BackofficeRole>();
    public DbSet<BackofficeUserRole> BackofficeUserRoles => Set<BackofficeUserRole>();
    public DbSet<BackofficeRefreshSession> BackofficeRefreshSessions => Set<BackofficeRefreshSession>();
    public DbSet<ExchangeRateHistory> ExchangeRateHistory => Set<ExchangeRateHistory>();
    public DbSet<LegalDocument> LegalDocuments => Set<LegalDocument>();
    public DbSet<ConsentEvent> ConsentEvents => Set<ConsentEvent>();
    public DbSet<ConsentReplayTombstone> ConsentReplayTombstones => Set<ConsentReplayTombstone>();
    public DbSet<ConsentOnboarding> ConsentOnboarding => Set<ConsentOnboarding>();
    public DbSet<CustomerConsentWithdrawalRequest> CustomerConsentWithdrawalRequests => Set<CustomerConsentWithdrawalRequest>();
    public DbSet<LegalDocumentAuditEvent> LegalDocumentAuditEvents => Set<LegalDocumentAuditEvent>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAssignedOrderCodesRemainImmutable();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnsureAssignedOrderCodesRemainImmutable();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

    private void EnsureAssignedOrderCodesRemainImmutable()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries<Customer>().Where(item => item.State == EntityState.Modified))
        {
            var orderCode = entry.Property(item => item.OrderCode);
            if (orderCode.IsModified
                && orderCode.OriginalValue is not null
                && !string.Equals(orderCode.OriginalValue, orderCode.CurrentValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A customer's assigned order code is immutable.");
            }
        }
    }
}
