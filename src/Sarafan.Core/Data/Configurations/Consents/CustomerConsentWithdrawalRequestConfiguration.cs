// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Consents;

internal sealed class CustomerConsentWithdrawalRequestConfiguration : IEntityTypeConfiguration<CustomerConsentWithdrawalRequest>
{
    public void Configure(EntityTypeBuilder<CustomerConsentWithdrawalRequest> builder)
    {
        builder.ToTable("customer_consent_withdrawal_requests");

        builder.HasKey(item => new { item.CustomerId, item.RequestedAt });

        builder.Property(item => item.CustomerId).HasColumnName("customer_id");
        builder.Property(item => item.RequestedAt).HasColumnName("requested_at");
        builder.Property(item => item.Processed).HasColumnName("processed").HasDefaultValue(false);

        builder.HasIndex(item => item.CustomerId)
            .IsUnique()
            .HasFilter("processed = FALSE");

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
