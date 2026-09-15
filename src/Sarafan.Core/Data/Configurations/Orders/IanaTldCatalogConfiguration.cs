// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Orders;

internal sealed class IanaTldCatalogConfiguration : IEntityTypeConfiguration<IanaTldCatalog>
{
    public void Configure(EntityTypeBuilder<IanaTldCatalog> builder)
    {
        builder.ToTable("iana_tld_catalog", table =>
        {
            table.HasCheckConstraint("ck_iana_tld_catalog_singleton", "id = 1");
            table.HasCheckConstraint("ck_iana_tld_catalog_version", "version ~ '^[0-9]{10}$'");
            table.HasCheckConstraint("ck_iana_tld_catalog_content_sha256", "content_sha256 ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_iana_tld_catalog_not_empty", "cardinality(top_level_domains) > 0");
        });

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(item => item.Version).HasColumnName("version").HasMaxLength(10).IsRequired();
        builder.Property(item => item.Source).HasColumnName("source").HasMaxLength(256).IsRequired();
        builder.Property(item => item.SourceUpdatedAt).HasColumnName("source_updated_at");
        builder.Property(item => item.RetrievedAt).HasColumnName("retrieved_at");
        builder.Property(item => item.ContentSha256).HasColumnName("content_sha256").HasMaxLength(64).IsRequired();
        builder.Property(item => item.TopLevelDomains).HasColumnName("top_level_domains").HasColumnType("text[]").IsRequired();
    }
}
