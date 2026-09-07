// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Customers;

internal sealed class RefreshSessionConfiguration : IEntityTypeConfiguration<RefreshSession>
{
    public void Configure(EntityTypeBuilder<RefreshSession> builder)
    {
        builder.ToTable("refresh_sessions");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.CustomerId).HasColumnName("customer_id");
        builder.Property(item => item.FamilyId).HasColumnName("family_id");
        builder.Property(item => item.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
        builder.Property(item => item.ReplacedByTokenHash).HasColumnName("replaced_by_token_hash").HasMaxLength(64);
        builder.Property(item => item.CreatedAt).HasColumnName("created_at");
        builder.Property(item => item.ExpiresAt).HasColumnName("expires_at");
        builder.Property(item => item.RevokedAt).HasColumnName("revoked_at");
        builder.Property(item => item.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(64);
        builder.Property(item => item.UserAgent).HasColumnName("user_agent").HasMaxLength(256);
        builder.Property(item => item.Version).HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(item => item.TokenHash).IsUnique();
        builder.HasIndex(item => new { item.CustomerId, item.FamilyId });

        builder.HasOne(item => item.Customer)
            .WithMany(item => item.RefreshSessions)
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
