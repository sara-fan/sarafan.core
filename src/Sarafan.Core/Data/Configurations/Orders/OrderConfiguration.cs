// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
        builder.Property(item => item.ProductName).HasColumnName("product_name").HasMaxLength(500);
        builder.Property(item => item.StoreName).HasColumnName("store_name").HasMaxLength(200);
        builder.Property(item => item.ImageUrl).HasColumnName("image_url").HasMaxLength(2048);
        builder.Property(item => item.SellerPrice).HasColumnName("seller_price").HasPrecision(10, 2);
        builder.Property(item => item.SellerPriceCurrency).HasColumnName("seller_price_currency");
        builder.Property(item => item.Color).HasColumnName("color").HasMaxLength(200);
        builder.Property(item => item.Size).HasColumnName("size").HasMaxLength(200);
        builder.Property(item => item.LengthCm).HasColumnName("length_cm").HasPrecision(10, 2);
        builder.Property(item => item.WidthCm).HasColumnName("width_cm").HasPrecision(10, 2);
        builder.Property(item => item.HeightCm).HasColumnName("height_cm").HasPrecision(10, 2);
        var characteristics = builder.Property(item => item.Characteristics)
            .HasColumnName("characteristics")
            .HasColumnType("jsonb")
            .HasConversion(
                value => SerializeCharacteristics(value),
                value => DeserializeCharacteristics(value));
        characteristics.Metadata.SetValueComparer(new ValueComparer<Dictionary<string, string>?>(
            (left, right) => CharacteristicsEqual(left, right),
            value => CharacteristicsHashCode(value),
            value => CloneCharacteristics(value)));
        builder.Property(item => item.Quantity).HasColumnName("quantity");
        builder.Property(item => item.Comment).HasColumnName("comment").HasMaxLength(2000);
        builder.Property(item => item.AppliedExchangeRateHistoryId).HasColumnName("applied_exchange_rate_history_id");
        var idempotencyKey = builder.Property(item => item.CreationIdempotencyKey)
            .HasColumnName("creation_idempotency_key");
        var createdAt = builder.Property(item => item.CreatedAt).HasColumnName("created_at");
        builder.Property(item => item.UpdatedAt).HasColumnName("updated_at").IsConcurrencyToken();
        builder.Property(item => item.Status).IsConcurrencyToken();
        builder.Property(item => item.CreatedLimitUsdRateId).HasColumnName("created_limit_usd_rate_id").Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne<ExchangeRateHistory>().WithMany().HasForeignKey(item => item.CreatedLimitUsdRateId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(item => item.CreatedLimitEurRateId).HasColumnName("created_limit_eur_rate_id").Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.HasOne<ExchangeRateHistory>().WithMany().HasForeignKey(item => item.CreatedLimitEurRateId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(item => item.UpdatedLimitUsdRateId).HasColumnName("updated_limit_usd_rate_id");
        builder.HasOne<ExchangeRateHistory>().WithMany().HasForeignKey(item => item.UpdatedLimitUsdRateId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(item => item.UpdatedLimitEurRateId).HasColumnName("updated_limit_eur_rate_id");
        builder.HasOne<ExchangeRateHistory>().WithMany().HasForeignKey(item => item.UpdatedLimitEurRateId).OnDelete(DeleteBehavior.Restrict);

        customerId.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        customerOrderNumber.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        sourceUrl.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        idempotencyKey.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        createdAt.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        builder.HasIndex(item => new { item.CustomerId, item.CustomerOrderNumber })
            .IsUnique()
            .HasDatabaseName("ux_orders_customer_order_number");
        builder.HasIndex(item => new { item.CustomerId, item.CreationIdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_orders_creation_idempotency");
        builder.HasIndex(item => item.AppliedExchangeRateHistoryId)
            .HasDatabaseName("ix_orders_applied_exchange_rate_history_id");
        builder.HasIndex(item => new { item.CreatedAt, item.Id })
            .HasDatabaseName("ix_orders_created_at_id");
        builder.HasIndex(item => new { item.Status, item.CreatedAt, item.Id })
            .HasDatabaseName("ix_orders_status_created_at_id");
        builder.HasIndex(item => new { item.UpdatedAt, item.Id })
            .HasDatabaseName("ix_orders_updated_at_id");

        builder.HasOne(item => item.Customer)
            .WithMany(item => item.Orders)
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(item => item.AppliedExchangeRateHistory)
            .WithMany()
            .HasForeignKey(item => item.AppliedExchangeRateHistoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_orders_created_limit_pair", "(created_limit_usd_rate_id IS NULL) = (created_limit_eur_rate_id IS NULL)");
            table.HasCheckConstraint("ck_orders_updated_limit_pair", "(updated_limit_usd_rate_id IS NULL) = (updated_limit_eur_rate_id IS NULL)");
            table.HasCheckConstraint("ck_orders_customer_order_number", "customer_order_number > 0");
            table.HasCheckConstraint("ck_orders_creation_idempotency_key", "creation_idempotency_key <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_orders_status", "status IN (0, 100, 200, 300, 310, 320, 330, 340, 360, 380, 400, 500)");
            table.HasCheckConstraint("ck_orders_source_url", "source_url ~* '^https?://' AND char_length(source_url) <= 2048");
            table.HasCheckConstraint("ck_orders_image_url", "image_url IS NULL OR image_url ~* '^https?://' AND char_length(image_url) <= 2048");
            table.HasCheckConstraint("ck_orders_quantity", "quantity > 0");
            table.HasCheckConstraint("ck_orders_seller_price", "(seller_price IS NULL AND seller_price_currency IS NULL) OR (seller_price > 0 AND seller_price_currency IN (643, 840, 978))");
            table.HasCheckConstraint("ck_orders_dimensions", "(length_cm IS NULL AND width_cm IS NULL AND height_cm IS NULL) OR (length_cm > 0 AND width_cm > 0 AND height_cm > 0)");
            table.HasCheckConstraint("ck_orders_applied_exchange_rate", "applied_exchange_rate_history_id IS NULL OR seller_price_currency IS NOT NULL");
        });
    }

    private static string SerializeCharacteristics(Dictionary<string, string>? value)
        => JsonSerializer.Serialize(value);

    private static Dictionary<string, string>? DeserializeCharacteristics(string value)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(value);

    private static bool CharacteristicsEqual(
        Dictionary<string, string>? left,
        Dictionary<string, string>? right)
        => ReferenceEquals(left, right)
            || left is not null
                && right is not null
                && left.Count == right.Count
                && left.All(item => right.TryGetValue(item.Key, out var value)
                    && string.Equals(item.Value, value, StringComparison.Ordinal));

    private static int CharacteristicsHashCode(Dictionary<string, string>? value)
    {
        if (value is null)
        {
            return 0;
        }

        var hash = new HashCode();
        foreach (var item in value.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            hash.Add(item.Key, StringComparer.Ordinal);
            hash.Add(item.Value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    private static Dictionary<string, string>? CloneCharacteristics(Dictionary<string, string>? value)
        => value?.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
}
