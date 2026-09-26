// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Orders;

internal sealed class OrderHistoryEventConfiguration : IEntityTypeConfiguration<OrderHistoryEvent>
{
    public void Configure(EntityTypeBuilder<OrderHistoryEvent> builder)
    {
        builder.ToTable("order_history_events");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.OrderId).HasColumnName("order_id");
        builder.Property(item => item.At).HasColumnName("at");
        builder.Property(item => item.Kind).HasColumnName("kind");
        builder.Property(item => item.Areas).HasColumnName("areas");
        builder.Property(item => item.ActorType).HasColumnName("actor_type");
        builder.Property(item => item.ActorId).HasColumnName("actor_id");
        builder.Property(item => item.ActorName).HasColumnName("actor_name").HasMaxLength(605);
        builder.Property(item => item.ProductAuditId).HasColumnName("product_audit_id");
        builder.Property(item => item.PricingSnapshotId).HasColumnName("pricing_snapshot_id");
        builder.Property(item => item.Payload).HasColumnName("payload").HasColumnType("jsonb");
        builder.HasOne(item => item.Order).WithMany().HasForeignKey(item => item.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(item => item.ProductAudit).WithMany().HasForeignKey(item => item.ProductAuditId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(item => item.PricingSnapshot).WithMany().HasForeignKey(item => item.PricingSnapshotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.OrderId, item.At, item.Id });
        builder.HasIndex(item => item.ProductAuditId).IsUnique();
        builder.HasIndex(item => item.PricingSnapshotId).IsUnique();
        foreach (var property in builder.Metadata.GetProperties().Where(item => !item.IsPrimaryKey()))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
