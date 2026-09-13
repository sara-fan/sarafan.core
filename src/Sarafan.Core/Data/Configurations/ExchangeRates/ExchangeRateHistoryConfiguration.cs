// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.ExchangeRates;

internal sealed class ExchangeRateHistoryConfiguration : IEntityTypeConfiguration<ExchangeRateHistory>
{
    public void Configure(EntityTypeBuilder<ExchangeRateHistory> builder)
    {
        builder.ToTable("exchange_rate_history", table =>
        {
            table.HasCheckConstraint("CK_exchange_rate_nominal", "nominal BETWEEN 1 AND 1000000");
            table.HasCheckConstraint("CK_exchange_rate_positive", "official_rate > 0");
            table.HasCheckConstraint("CK_exchange_rate_base_currency", "base_currency IN (643, 840)");
            table.HasCheckConstraint("CK_exchange_rate_quote_currency", "quote_currency IN (643, 840)");
            table.HasCheckConstraint("CK_exchange_rate_distinct_currencies", "base_currency <> quote_currency");
        });

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        var provider = builder.Property(item => item.Provider).HasColumnName("provider").HasMaxLength(16);
        var source = builder.Property(item => item.Source).HasColumnName("source").HasMaxLength(256);
        var baseCurrency = builder.Property(item => item.BaseCurrency).HasColumnName("base_currency");
        var quoteCurrency = builder.Property(item => item.QuoteCurrency).HasColumnName("quote_currency");
        var nominal = builder.Property(item => item.Nominal).HasColumnName("nominal");
        var officialRate = builder.Property(item => item.OfficialRate).HasColumnName("official_rate").HasPrecision(18, 6);
        var sourceEffectiveDate = builder.Property(item => item.SourceEffectiveDate).HasColumnName("source_effective_date").HasColumnType("date");
        var retrievedAt = builder.Property(item => item.RetrievedAt).HasColumnName("retrieved_at");

        foreach (var property in new[]
        {
            provider.Metadata,
            source.Metadata,
            baseCurrency.Metadata,
            quoteCurrency.Metadata,
            nominal.Metadata,
            officialRate.Metadata,
            sourceEffectiveDate.Metadata,
            retrievedAt.Metadata
        })
        {
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }

        builder.HasIndex(item => new { item.Provider, item.BaseCurrency, item.QuoteCurrency, item.SourceEffectiveDate })
            .IsUnique();
    }
}
