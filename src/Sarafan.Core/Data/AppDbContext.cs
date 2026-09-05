// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerProfile> CustomerProfiles => Set<CustomerProfile>();
    public DbSet<CustomerPhoto> CustomerPhotos => Set<CustomerPhoto>();
    public DbSet<CustomerConsent> CustomerConsents => Set<CustomerConsent>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    public DbSet<BackofficeUser> BackofficeUsers => Set<BackofficeUser>();
    public DbSet<BackofficeRole> BackofficeRoles => Set<BackofficeRole>();
    public DbSet<BackofficeUserRole> BackofficeUserRoles => Set<BackofficeUserRole>();
    public DbSet<BackofficeRefreshSession> BackofficeRefreshSessions => Set<BackofficeRefreshSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var customer = modelBuilder.Entity<Customer>();
        customer.ToTable("customers");
        customer.HasKey(item => item.Id);
        customer.Property(item => item.Id).HasColumnName("id");
        customer.Property(item => item.Phone).HasColumnName("phone").HasMaxLength(16).IsRequired();
        customer.HasIndex(item => item.Phone).IsUnique();
        customer.Property(item => item.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        customer.Property(item => item.CreatedAt).HasColumnName("created_at");
        customer.Property(item => item.UpdatedAt).HasColumnName("updated_at");

        var profile = modelBuilder.Entity<CustomerProfile>();
        profile.ToTable("customer_profiles");
        profile.HasKey(item => item.CustomerId);
        profile.Property(item => item.CustomerId).HasColumnName("customer_id");
        profile.Property(item => item.LastName).HasColumnName("last_name").HasMaxLength(100);
        profile.Property(item => item.FirstName).HasColumnName("first_name").HasMaxLength(100);
        profile.Property(item => item.Patronymic).HasColumnName("patronymic").HasMaxLength(100);
        profile.Property(item => item.Email).HasColumnName("email").HasMaxLength(254);
        profile.Property(item => item.PassportSeries).HasColumnName("passport_series").HasMaxLength(32);
        profile.Property(item => item.PassportNumber).HasColumnName("passport_number").HasMaxLength(32);
        profile.Property(item => item.PassportIssueDate).HasColumnName("passport_issue_date").HasColumnType("date");
        profile.Property(item => item.PassportIssuedBy).HasColumnName("passport_issued_by").HasMaxLength(500);
        profile.Property(item => item.Inn).HasColumnName("inn").HasMaxLength(16);
        profile.Property(item => item.PostalCode).HasColumnName("postal_code").HasMaxLength(20);
        profile.Property(item => item.City).HasColumnName("city").HasMaxLength(150);
        profile.Property(item => item.Address).HasColumnName("address").HasMaxLength(500);
        profile.HasOne(item => item.Customer)
            .WithOne(item => item.Profile)
            .HasForeignKey<CustomerProfile>(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        var photo = modelBuilder.Entity<CustomerPhoto>();
        photo.ToTable("customer_photos");
        photo.HasKey(item => item.CustomerId);
        photo.Property(item => item.CustomerId).HasColumnName("customer_id");
        photo.Property(item => item.FileName).HasColumnName("file_name").HasMaxLength(255);
        photo.Property(item => item.ContentType).HasColumnName("content_type").HasMaxLength(64);
        photo.Property(item => item.Content).HasColumnName("content").HasColumnType("bytea");
        photo.Property(item => item.Size).HasColumnName("size");
        photo.Property(item => item.UpdatedAt).HasColumnName("updated_at");
        photo.HasOne(item => item.Customer)
            .WithOne(item => item.Photo)
            .HasForeignKey<CustomerPhoto>(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        var consent = modelBuilder.Entity<CustomerConsent>();
        consent.ToTable("customer_consents");
        consent.HasKey(item => item.Id);
        consent.Property(item => item.Id).HasColumnName("id");
        consent.Property(item => item.CustomerId).HasColumnName("customer_id");
        consent.Property(item => item.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(32);
        consent.Property(item => item.DocumentVersion).HasColumnName("document_version").HasMaxLength(64);
        consent.Property(item => item.AcceptedAt).HasColumnName("accepted_at");
        consent.HasIndex(item => new { item.CustomerId, item.Type, item.DocumentVersion }).IsUnique();
        consent.HasOne(item => item.Customer)
            .WithMany(item => item.Consents)
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        var session = modelBuilder.Entity<RefreshSession>();
        session.ToTable("refresh_sessions");
        session.HasKey(item => item.Id);
        session.Property(item => item.Id).HasColumnName("id");
        session.Property(item => item.CustomerId).HasColumnName("customer_id");
        session.Property(item => item.FamilyId).HasColumnName("family_id");
        session.Property(item => item.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
        session.Property(item => item.ReplacedByTokenHash).HasColumnName("replaced_by_token_hash").HasMaxLength(64);
        session.Property(item => item.CreatedAt).HasColumnName("created_at");
        session.Property(item => item.ExpiresAt).HasColumnName("expires_at");
        session.Property(item => item.RevokedAt).HasColumnName("revoked_at");
        session.Property(item => item.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(64);
        session.Property(item => item.UserAgent).HasColumnName("user_agent").HasMaxLength(256);
        session.Property(item => item.Version).HasColumnName("xmin").IsRowVersion();
        session.HasIndex(item => item.TokenHash).IsUnique();
        session.HasIndex(item => new { item.CustomerId, item.FamilyId });
        session.HasOne(item => item.Customer)
            .WithMany(item => item.RefreshSessions)
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        ConfigureBackoffice(modelBuilder);
    }

    private static void ConfigureBackoffice(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<BackofficeUser>();
        user.ToTable("backoffice_users");
        user.HasKey(item => item.Id);
        user.Property(item => item.Id).HasColumnName("id");
        user.Property(item => item.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
        user.Property(item => item.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(254).IsRequired();
        user.HasIndex(item => item.NormalizedEmail).IsUnique();
        user.Property(item => item.FirstName).HasColumnName("first_name").HasMaxLength(100).IsRequired();
        user.Property(item => item.LastName).HasColumnName("last_name").HasMaxLength(100).IsRequired();
        user.Property(item => item.Patronymic).HasColumnName("patronymic").HasMaxLength(100);
        user.Property(item => item.PasswordHash).HasColumnName("password_hash").HasMaxLength(128).IsRequired();
        user.Property(item => item.IsActive).HasColumnName("is_active");
        user.Property(item => item.IsDemo).HasColumnName("is_demo");
        user.Property(item => item.TokenVersion).HasColumnName("token_version");
        user.Property(item => item.CreatedAt).HasColumnName("created_at");
        user.Property(item => item.UpdatedAt).HasColumnName("updated_at");

        var role = modelBuilder.Entity<BackofficeRole>();
        role.ToTable("backoffice_roles");
        role.HasKey(item => item.Code);
        role.Property(item => item.Code).HasColumnName("code").HasMaxLength(32);
        role.Property(item => item.DisplayName).HasColumnName("display_name").HasMaxLength(64).IsRequired();
        role.HasData(Sarafan.Core.Authentication.BackofficeRoles.Definitions.Select(item => new BackofficeRole
        {
            Code = item.Code,
            DisplayName = item.DisplayName
        }));

        var userRole = modelBuilder.Entity<BackofficeUserRole>();
        userRole.ToTable("backoffice_user_roles");
        userRole.HasKey(item => new { item.BackofficeUserId, item.RoleCode });
        userRole.Property(item => item.BackofficeUserId).HasColumnName("backoffice_user_id");
        userRole.Property(item => item.RoleCode).HasColumnName("role_code").HasMaxLength(32);
        userRole.HasOne(item => item.BackofficeUser)
            .WithMany(item => item.UserRoles)
            .HasForeignKey(item => item.BackofficeUserId)
            .OnDelete(DeleteBehavior.Cascade);
        userRole.HasOne(item => item.Role)
            .WithMany(item => item.UserRoles)
            .HasForeignKey(item => item.RoleCode)
            .OnDelete(DeleteBehavior.Restrict);

        var refreshSession = modelBuilder.Entity<BackofficeRefreshSession>();
        refreshSession.ToTable("backoffice_refresh_sessions");
        refreshSession.HasKey(item => item.Id);
        refreshSession.Property(item => item.Id).HasColumnName("id");
        refreshSession.Property(item => item.BackofficeUserId).HasColumnName("backoffice_user_id");
        refreshSession.Property(item => item.FamilyId).HasColumnName("family_id");
        refreshSession.Property(item => item.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
        refreshSession.Property(item => item.ReplacedByTokenHash).HasColumnName("replaced_by_token_hash").HasMaxLength(64);
        refreshSession.Property(item => item.CreatedAt).HasColumnName("created_at");
        refreshSession.Property(item => item.ExpiresAt).HasColumnName("expires_at");
        refreshSession.Property(item => item.RevokedAt).HasColumnName("revoked_at");
        refreshSession.Property(item => item.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(64);
        refreshSession.Property(item => item.UserAgent).HasColumnName("user_agent").HasMaxLength(256);
        refreshSession.Property(item => item.Version).HasColumnName("xmin").IsRowVersion();
        refreshSession.HasIndex(item => item.TokenHash).IsUnique();
        refreshSession.HasIndex(item => new { item.BackofficeUserId, item.FamilyId });
        refreshSession.HasOne(item => item.BackofficeUser)
            .WithMany(item => item.RefreshSessions)
            .HasForeignKey(item => item.BackofficeUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
