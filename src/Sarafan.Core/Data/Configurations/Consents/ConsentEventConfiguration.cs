// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Consents;

internal sealed class ConsentEventConfiguration : IEntityTypeConfiguration<ConsentEvent>
{
    public void Configure(EntityTypeBuilder<ConsentEvent> builder)
    {
        builder.ToTable("consent_events");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.CustomerId).HasColumnName("customer_id");
        builder.Property(item => item.SubjectKey).HasColumnName("subject_key").HasMaxLength(80);
        builder.Property(item => item.DocumentId).HasColumnName("document_id");
        builder.Property(item => item.ContentHash).HasColumnName("content_hash").HasMaxLength(64);
        builder.Property(item => item.Kind).HasColumnName("kind");
        builder.Property(item => item.Decision).HasColumnName("decision").HasMaxLength(16);
        builder.Property(item => item.Categories).HasColumnName("categories").HasColumnType("integer[]");
        builder.Property(item => item.Source).HasColumnName("source").HasMaxLength(32);
        builder.Property(item => item.IdempotencyKey).HasColumnName("idempotency_key");
        builder.Property(item => item.At).HasColumnName("at");
        builder.Property(item => item.ExpiresAt).HasColumnName("expires_at");
        builder.Property(item => item.RetainUntil).HasColumnName("retain_until");

        builder.HasIndex(item => new { item.SubjectKey, item.IdempotencyKey }).IsUnique();
        builder.HasIndex(item => item.IdempotencyKey).IsUnique().HasFilter("kind = 0");
        builder.HasIndex(item => new { item.CustomerId, item.Kind, item.Id });

        builder.HasOne(item => item.Document)
            .WithMany()
            .HasForeignKey(item => item.DocumentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Customer>()
            .WithMany(item => item.ConsentEvents)
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
