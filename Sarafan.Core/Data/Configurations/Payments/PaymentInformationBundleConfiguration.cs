// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Payments;

internal sealed class PaymentInformationBundleConfiguration : IEntityTypeConfiguration<PaymentInformationBundle>
{
    public void Configure(EntityTypeBuilder<PaymentInformationBundle> builder)
    {
        builder.ToTable("payment_information_bundles", table =>
        {
            table.HasCheckConstraint("ck_payment_bundles_enabled", "NOT enabled OR published");
            table.HasCheckConstraint("ck_payment_bundles_timestamps", "updated_at >= created_at");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.RecipientType).HasColumnName("recipient_type").HasConversion<int?>();
        builder.Property(item => item.RecipientName).HasColumnName("recipient_name").HasMaxLength(200);
        builder.Property(item => item.Inn).HasColumnName("inn").HasMaxLength(12);
        builder.Property(item => item.Kpp).HasColumnName("kpp").HasMaxLength(9);
        builder.Property(item => item.SettlementAccount).HasColumnName("settlement_account").HasMaxLength(20);
        builder.Property(item => item.BankName).HasColumnName("bank_name").HasMaxLength(200);
        builder.Property(item => item.Bik).HasColumnName("bik").HasMaxLength(9);
        builder.Property(item => item.CorrespondentAccount).HasColumnName("correspondent_account").HasMaxLength(20);
        builder.Property(item => item.PaymentLink).HasColumnName("payment_link").HasMaxLength(2048);
        builder.Property(item => item.QrContent).HasColumnName("qr_content").HasColumnType("bytea");
        builder.Property(item => item.QrContentType).HasColumnName("qr_content_type").HasMaxLength(32);
        builder.Property(item => item.QrSha256).HasColumnName("qr_sha256").HasMaxLength(64);
        builder.Property(item => item.Enabled).HasColumnName("enabled").HasDefaultValue(false);
        builder.Property(item => item.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone")
            .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(item => item.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
        builder.Property(item => item.CreatedBy).HasColumnName("created_by").Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(item => item.UpdatedBy).HasColumnName("updated_by");
        builder.Property(item => item.Published).HasColumnName("published").HasDefaultValue(false);
        builder.Property(item => item.Version).HasColumnName("version").HasColumnType("uuid").IsConcurrencyToken().ValueGeneratedNever();
        builder.HasIndex(item => item.Enabled).IsUnique().HasFilter("enabled = TRUE").HasDatabaseName("ux_payment_bundles_enabled");
        builder.HasOne<BackofficeUser>().WithMany().HasForeignKey(item => item.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<BackofficeUser>().WithMany().HasForeignKey(item => item.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
