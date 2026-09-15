// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Orders;

internal sealed class OrderProductAuditEventConfiguration : IEntityTypeConfiguration<OrderProductAuditEvent>
{
    public void Configure(EntityTypeBuilder<OrderProductAuditEvent> builder)
    {
        builder.ToTable("order_product_audit_events");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.OrderId).HasColumnName("order_id");
        builder.Property(item => item.ActorId).HasColumnName("actor_id");
        builder.Property(item => item.OccurredAt).HasColumnName("occurred_at");
        builder.Property(item => item.Before).HasColumnName("before").HasColumnType("jsonb").IsRequired();
        builder.Property(item => item.After).HasColumnName("after").HasColumnType("jsonb").IsRequired();
        builder.Property(item => item.UsdRateId).HasColumnName("usd_rate_id");
        builder.Property(item => item.EurRateId).HasColumnName("eur_rate_id");
        builder.HasOne<Order>().WithMany().HasForeignKey(item => item.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackofficeUser>().WithMany().HasForeignKey(item => item.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExchangeRateHistory>().WithMany().HasForeignKey(item => item.UsdRateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ExchangeRateHistory>().WithMany().HasForeignKey(item => item.EurRateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.OrderId, item.OccurredAt });
        foreach (var property in builder.Metadata.GetProperties())
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
