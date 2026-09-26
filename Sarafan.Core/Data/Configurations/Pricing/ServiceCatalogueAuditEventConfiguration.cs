// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Pricing;

internal sealed class ServiceCatalogueAuditEventConfiguration : IEntityTypeConfiguration<ServiceCatalogueAuditEvent>
{
    public void Configure(EntityTypeBuilder<ServiceCatalogueAuditEvent> builder)
    {
        builder.ToTable("service_catalogue_audit_events", table =>
        {
            table.HasCheckConstraint("ck_service_catalogue_audit_action", "action IN (0, 100, 200)");
            table.HasCheckConstraint("ck_service_catalogue_audit_actor_name", "btrim(actor_name) <> ''");
            table.HasCheckConstraint("ck_service_catalogue_audit_snapshots", """
                (action = 0 AND before IS NULL AND after IS NOT NULL)
                OR (action = 100 AND before IS NOT NULL AND after IS NOT NULL)
                OR (action = 200 AND before IS NOT NULL AND after IS NULL)
                """);
        });

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.EntryId).HasColumnName("entry_id");
        builder.Property(item => item.Service).HasColumnName("service").HasConversion<int>();
        builder.Property(item => item.Action).HasColumnName("action").HasConversion<int>();
        builder.Property(item => item.ActorId).HasColumnName("actor_id");
        builder.Property(item => item.ActorName).HasColumnName("actor_name").HasMaxLength(605).IsRequired();
        builder.Property(item => item.At).HasColumnName("at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.Before).HasColumnName("before").HasColumnType("jsonb");
        builder.Property(item => item.After).HasColumnName("after").HasColumnType("jsonb");

        builder.HasOne(item => item.Actor).WithMany().HasForeignKey(item => item.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.At, item.Id }).HasDatabaseName("ix_service_catalogue_audit_at_id");
        builder.HasIndex(item => new { item.EntryId, item.At, item.Id }).HasDatabaseName("ix_service_catalogue_audit_entry_at_id");
        builder.HasIndex(item => new { item.Service, item.At, item.Id }).HasDatabaseName("ix_service_catalogue_audit_service_at_id");

        foreach (var property in builder.Metadata.GetProperties())
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
