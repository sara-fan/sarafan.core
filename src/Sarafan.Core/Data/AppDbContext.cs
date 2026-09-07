// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
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
    public DbSet<ConsentAssociation> ConsentAssociations => Set<ConsentAssociation>();
    public DbSet<ConsentOnboarding> ConsentOnboarding => Set<ConsentOnboarding>();
    public DbSet<ConsentRightsCase> ConsentRightsCases => Set<ConsentRightsCase>();
    public DbSet<LegalAuditEvent> LegalAuditEvents => Set<LegalAuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
