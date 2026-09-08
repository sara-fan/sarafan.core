// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Consents;

internal sealed class LegalDocumentAuditEventConfiguration : IEntityTypeConfiguration<LegalDocumentAuditEvent>
{
    public void Configure(EntityTypeBuilder<LegalDocumentAuditEvent> builder)
    {
        builder.ToTable("legal_document_audit_events");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.DocumentId).HasColumnName("document_id");
        builder.Property(item => item.ActorId).HasColumnName("actor_id");
        builder.Property(item => item.Action).HasColumnName("action").HasMaxLength(16);
        builder.Property(item => item.At).HasColumnName("at");
        builder.Property(item => item.Kind).HasColumnName("kind");
        builder.Property(item => item.Locale).HasColumnName("locale").HasMaxLength(8);
        builder.Property(item => item.Title).HasColumnName("title").HasMaxLength(200);
        builder.Property(item => item.DisplayVersion).HasColumnName("display_version").HasMaxLength(64);
        builder.Property(item => item.EffectiveAt).HasColumnName("effective_at");
        builder.Property(item => item.SourceHash).HasColumnName("source_hash").HasMaxLength(64);
        builder.Property(item => item.ContentHash).HasColumnName("content_hash").HasMaxLength(64);

        builder.HasOne(item => item.BackofficeUser)
            .WithMany()
            .HasForeignKey(item => item.ActorId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(item => item.DocumentId);
        builder.HasIndex(item => item.At);
    }
}

