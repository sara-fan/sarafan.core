// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Pricing;

internal sealed class OrderPricingSnapshotConfiguration : IEntityTypeConfiguration<OrderPricingSnapshot>
{
    public void Configure(EntityTypeBuilder<OrderPricingSnapshot> builder)
    {
        builder.ToTable("order_pricing_snapshots", table =>
        {
            table.HasCheckConstraint("ck_order_pricing_validity", "valid_until IS NULL OR valid_until > at");
            table.HasCheckConstraint("ck_order_pricing_actor", "(actor_id IS NULL AND actor_name IS NULL) OR (actor_id IS NOT NULL AND actor_name IS NOT NULL AND btrim(actor_name) <> '')");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.OrderId).HasColumnName("order_id");
        builder.Property(item => item.At).HasColumnName("at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.ValidUntil).HasColumnName("valid_until").HasColumnType("timestamp with time zone");
        builder.Property(item => item.ActorId).HasColumnName("actor_id");
        builder.Property(item => item.ActorName).HasColumnName("actor_name").HasMaxLength(605);
        builder.Property(item => item.Payload).HasColumnName("payload").HasColumnType("jsonb");
        builder.HasOne(item => item.Order).WithMany().HasForeignKey(item => item.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(item => item.Actor).WithMany().HasForeignKey(item => item.ActorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.OrderId, item.Id }).HasDatabaseName("ix_order_pricing_order_id");
        foreach (var property in builder.Metadata.GetProperties().Where(item => !item.IsPrimaryKey()))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
