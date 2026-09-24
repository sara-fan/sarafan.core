// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Stores;

internal sealed class StoreLogoConfiguration : IEntityTypeConfiguration<StoreLogo>
{
    public void Configure(EntityTypeBuilder<StoreLogo> builder)
    {
        builder.ToTable("store_logos", table =>
        {
            table.HasCheckConstraint("ck_store_logos_content_type", "content_type IN ('image/png', 'image/jpeg', 'image/webp')");
            table.HasCheckConstraint("ck_store_logos_content_size", "octet_length(content) BETWEEN 1 AND 2097152");
            table.HasCheckConstraint("ck_store_logos_content_sha256", "content_sha256 ~ '^[0-9a-f]{64}$'");
        });

        builder.HasKey(item => item.StoreId);
        builder.Property(item => item.StoreId).HasColumnName("store_id").ValueGeneratedNever();
        builder.Property(item => item.ContentType).HasColumnName("content_type").HasMaxLength(64).IsRequired();
        builder.Property(item => item.Content).HasColumnName("content").HasColumnType("bytea")
            .HasField("content").UsePropertyAccessMode(PropertyAccessMode.Field).IsRequired();
        builder.Property(item => item.ContentSha256).HasColumnName("content_sha256").HasMaxLength(64).IsRequired();

        builder.HasOne(item => item.Store).WithOne(item => item.Logo)
            .HasForeignKey<StoreLogo>(item => item.StoreId).OnDelete(DeleteBehavior.Cascade);
    }
}
