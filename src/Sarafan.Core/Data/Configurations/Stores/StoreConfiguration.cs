// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Stores;

internal sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> builder)
    {
        builder.ToTable("stores", table =>
        {
            table.HasCheckConstraint("ck_stores_status", "status IN (0, 1)");
            table.HasCheckConstraint("ck_stores_display_order", "display_order >= 0");
            table.HasCheckConstraint("ck_stores_name", "btrim(name) <> ''");
            table.HasCheckConstraint("ck_stores_description", "btrim(description) <> ''");
            table.HasCheckConstraint("ck_stores_official_url", "official_url ~* '^https?://'");
            table.HasCheckConstraint("ck_stores_version", "version <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_stores_timestamps", "updated_at >= created_at");
        });

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(item => item.Description).HasColumnName("description").HasMaxLength(160).IsRequired();
        builder.Property(item => item.OfficialUrl).HasColumnName("official_url").HasMaxLength(2048).IsRequired();
        builder.Property(item => item.Status).HasColumnName("status").HasConversion<int>().HasDefaultValue(StoreStatus.Hidden);
        builder.Property(item => item.ShowOnHome).HasColumnName("show_on_home").HasDefaultValue(false);
        builder.Property(item => item.DisplayOrder).HasColumnName("display_order").HasDefaultValue(0);
        builder.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone")
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.Version).HasColumnName("version").HasColumnType("uuid").IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(item => new { item.Status, item.DisplayOrder, item.Id })
            .HasDatabaseName("ix_stores_status_display_order_id");
        builder.HasIndex(item => new { item.Status, item.ShowOnHome, item.DisplayOrder, item.Id })
            .HasDatabaseName("ix_stores_status_show_on_home_display_order_id");
    }
}
