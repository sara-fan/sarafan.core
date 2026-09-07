// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Consents;

internal sealed class ConsentRightsCaseConfiguration : IEntityTypeConfiguration<ConsentRightsCase>
{
    public void Configure(EntityTypeBuilder<ConsentRightsCase> builder)
    {
        builder.ToTable("consent_rights_cases");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.CustomerId).HasColumnName("customer_id");
        builder.Property(item => item.IdempotencyKey).HasColumnName("idempotency_key");
        builder.Property(item => item.Kind).HasColumnName("kind").HasMaxLength(32);
        builder.Property(item => item.State).HasColumnName("state").HasMaxLength(16);
        builder.Property(item => item.ReceivedAt).HasColumnName("received_at");
        builder.Property(item => item.DueAt).HasColumnName("due_at");
        builder.Property(item => item.ResponsibleStaffId).HasColumnName("responsible_staff_id");
        builder.Property(item => item.RetentionBasis).HasColumnName("retention_basis").HasMaxLength(2000);
        builder.Property(item => item.CompletionEvidence).HasColumnName("completion_evidence").HasMaxLength(2000);
        builder.Property(item => item.ExtensionReason).HasColumnName("extension_reason").HasMaxLength(1000);
        builder.Property(item => item.Extended).HasColumnName("extended");
        builder.Property(item => item.CompletedAt).HasColumnName("completed_at");
        builder.Property(item => item.Revision).HasColumnName("revision").IsConcurrencyToken();

        builder.HasIndex(item => new { item.CustomerId, item.IdempotencyKey }).IsUnique();

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
