// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Text.Json;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Pricing;

internal sealed class ServiceCatalogueEntryConfiguration : IEntityTypeConfiguration<ServiceCatalogueEntry>
{
    public void Configure(EntityTypeBuilder<ServiceCatalogueEntry> builder)
    {
        builder.ToTable("service_catalogue_entries", table =>
        {
            table.HasCheckConstraint("ck_service_catalogue_product_identity", "(service = 0 AND id = 1) OR (service <> 0 AND id <> 1)");
            table.HasCheckConstraint("ck_service_catalogue_method", "price_method IN (0, 100, 200, 300, 400)");
            table.HasCheckConstraint("ck_service_catalogue_currency", "currency IS NOT NULL AND currency IN (643, 840)");
            table.HasCheckConstraint("ck_service_catalogue_bands", "(price_method = 400 AND interval_currency IS NOT NULL AND interval_currency = 840 AND valid_service_price_bands(bands)) OR (price_method <> 400 AND interval_currency IS NULL AND bands = '[]'::jsonb)");
            table.HasCheckConstraint("ck_service_catalogue_period", "available_from IS NULL OR available_by IS NULL OR available_by >= available_from");
            table.HasCheckConstraint("ck_service_catalogue_version", "version <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_service_catalogue_timestamps", "updated_at >= created_at");
            table.HasCheckConstraint("ck_service_catalogue_parameters", """
                (price_method = 0 AND percentage IS NOT NULL AND percentage > 0 AND percentage <= 100
                    AND amount IS NULL AND currency = 840
                    AND (minimum_amount IS NULL OR minimum_amount >= 0)
                    AND (maximum_amount IS NULL OR maximum_amount >= 0)
                    AND (minimum_amount IS NULL OR maximum_amount IS NULL OR maximum_amount >= minimum_amount))
                OR (price_method = 100 AND percentage IS NULL AND minimum_amount IS NULL AND maximum_amount IS NULL
                    AND amount IS NOT NULL AND amount >= 0 AND currency IN (643, 840))
                OR (price_method IN (200, 300, 400) AND percentage IS NULL AND minimum_amount IS NULL AND maximum_amount IS NULL
                    AND amount IS NULL AND currency IN (643, 840))
                """);
        });

        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.Service).HasColumnName("service").HasConversion<int>();
        builder.Property(item => item.PriceMethod).HasColumnName("price_method").HasConversion<int>();
        builder.Property(item => item.Percentage).HasColumnName("percentage").HasPrecision(7, 4);
        builder.Property(item => item.MinimumAmount).HasColumnName("minimum_amount").HasPrecision(10, 2);
        builder.Property(item => item.MaximumAmount).HasColumnName("maximum_amount").HasPrecision(10, 2);
        builder.Property(item => item.Amount).HasColumnName("amount").HasPrecision(10, 2);
        builder.Property(item => item.Currency).HasColumnName("currency").HasConversion<int?>();
        builder.Property(item => item.IntervalCurrency).HasColumnName("interval_currency").HasConversion<int?>();
        builder.Property(item => item.Bands).HasColumnName("bands").HasColumnType("jsonb")
            .HasConversion(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<PriceBand[]>(value, (JsonSerializerOptions?)null)!)
            .Metadata.SetValueComparer(new ValueComparer<PriceBand[]>(
                (left, right) => left!.SequenceEqual(right!),
                value => value.Aggregate(0, (hash, band) => HashCode.Combine(hash, band.GetHashCode())),
                value => value.ToArray()));
        builder.Property(item => item.AvailableFrom).HasColumnName("available_from").HasColumnType("date");
        builder.Property(item => item.AvailableBy).HasColumnName("available_by").HasColumnType("date");
        builder.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone")
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.Version).HasColumnName("version").HasColumnType("uuid")
            .IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(item => new { item.Service, item.AvailableFrom, item.Id })
            .HasDatabaseName("ix_service_catalogue_service_available_from_id");
    }
}
