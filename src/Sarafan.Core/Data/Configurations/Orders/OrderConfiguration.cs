// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Orders;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        var customerId = builder.Property(item => item.CustomerId).HasColumnName("customer_id");
        var customerOrderNumber = builder.Property(item => item.CustomerOrderNumber).HasColumnName("customer_order_number");
        builder.Property(item => item.Status).HasColumnName("status").HasDefaultValue(OrderStatus.UnderReview);
        var sourceUrl = builder.Property(item => item.SourceUrl).HasColumnName("source_url").HasMaxLength(2048).IsRequired();
        var idempotencyKey = builder.Property(item => item.CreationIdempotencyKey)
            .HasColumnName("creation_idempotency_key");

        customerId.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        customerOrderNumber.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        sourceUrl.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        idempotencyKey.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        builder.HasIndex(item => new { item.CustomerId, item.CustomerOrderNumber })
            .IsUnique()
            .HasDatabaseName("ux_orders_customer_order_number");
        builder.HasIndex(item => new { item.CustomerId, item.CreationIdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_orders_creation_idempotency");

        builder.HasOne(item => item.Customer)
            .WithMany(item => item.Orders)
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_orders_customer_order_number", "customer_order_number > 0");
            table.HasCheckConstraint("ck_orders_creation_idempotency_key", "creation_idempotency_key <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_orders_status", "status IN (0, 100, 200, 300, 310, 320, 330, 340, 360, 380, 400, 500)");
            table.HasCheckConstraint("ck_orders_source_url", "source_url ~* '^https?://' AND char_length(source_url) <= 2048");
        });
    }
}
