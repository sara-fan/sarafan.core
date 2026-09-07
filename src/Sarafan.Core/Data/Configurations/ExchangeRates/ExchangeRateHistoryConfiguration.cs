// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
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
        });

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.Provider).HasColumnName("provider").HasMaxLength(16);
        builder.Property(item => item.Source).HasColumnName("source").HasMaxLength(256);
        builder.Property(item => item.BaseCurrency).HasColumnName("base_currency").HasMaxLength(3);
        builder.Property(item => item.QuoteCurrency).HasColumnName("quote_currency").HasMaxLength(3);
        builder.Property(item => item.Nominal).HasColumnName("nominal");
        builder.Property(item => item.OfficialRate).HasColumnName("official_rate").HasPrecision(18, 6);
        builder.Property(item => item.SourceEffectiveDate).HasColumnName("source_effective_date").HasColumnType("date");
        builder.Property(item => item.RetrievedAt).HasColumnName("retrieved_at");

        builder.HasIndex(item => new { item.Provider, item.BaseCurrency, item.QuoteCurrency, item.SourceEffectiveDate })
            .IsUnique();
    }
}
