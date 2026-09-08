// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Consents;

internal sealed class LegalDocumentConfiguration : IEntityTypeConfiguration<LegalDocument>
{
    public void Configure(EntityTypeBuilder<LegalDocument> builder)
    {
        builder.ToTable("legal_documents");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.Kind).HasColumnName("kind");
        builder.Property(item => item.Locale).HasColumnName("locale").HasMaxLength(8);
        builder.Property(item => item.Title).HasColumnName("title").HasMaxLength(200);
        builder.Property(item => item.DisplayVersion).HasColumnName("display_version").HasMaxLength(64);
        builder.Property(item => item.Source).HasColumnName("source");
        builder.Property(item => item.Html).HasColumnName("html");
        builder.Property(item => item.SourceHash).HasColumnName("source_hash").HasMaxLength(64);
        builder.Property(item => item.ContentHash).HasColumnName("content_hash").HasMaxLength(64);
        builder.Property(item => item.RendererVersion).HasColumnName("renderer_version").HasMaxLength(64);
        builder.Property(item => item.CookieCategories).HasColumnName("cookie_categories").HasColumnType("integer[]");
        builder.Property(item => item.CreatedBy).HasColumnName("created_by");
        builder.Property(item => item.CreatedAt).HasColumnName("created_at");
        builder.Property(item => item.EffectiveAt).HasColumnName("effective_at");

        builder.HasIndex(item => new { item.Kind, item.Locale, item.DisplayVersion }).IsUnique();
        builder.HasIndex(item => new { item.Kind, item.Locale, item.EffectiveAt }).IsUnique();
    }
}
