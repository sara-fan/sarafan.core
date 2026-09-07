// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Customers;

internal sealed class CustomerProfileConfiguration : IEntityTypeConfiguration<CustomerProfile>
{
    public void Configure(EntityTypeBuilder<CustomerProfile> builder)
    {
        builder.ToTable("customer_profiles");

        builder.HasKey(item => item.CustomerId);

        builder.Property(item => item.CustomerId).HasColumnName("customer_id");
        builder.Property(item => item.LastName).HasColumnName("last_name").HasMaxLength(100);
        builder.Property(item => item.FirstName).HasColumnName("first_name").HasMaxLength(100);
        builder.Property(item => item.Patronymic).HasColumnName("patronymic").HasMaxLength(100);
        builder.Property(item => item.Email).HasColumnName("email").HasMaxLength(254);
        builder.Property(item => item.PassportSeries).HasColumnName("passport_series").HasMaxLength(32);
        builder.Property(item => item.PassportNumber).HasColumnName("passport_number").HasMaxLength(32);
        builder.Property(item => item.PassportIssueDate).HasColumnName("passport_issue_date").HasColumnType("date");
        builder.Property(item => item.PassportIssuedBy).HasColumnName("passport_issued_by").HasMaxLength(500);
        builder.Property(item => item.Inn).HasColumnName("inn").HasMaxLength(16);
        builder.Property(item => item.PostalCode).HasColumnName("postal_code").HasMaxLength(20);
        builder.Property(item => item.City).HasColumnName("city").HasMaxLength(150);
        builder.Property(item => item.Address).HasColumnName("address").HasMaxLength(500);

        builder.HasOne(item => item.Customer)
            .WithOne(item => item.Profile)
            .HasForeignKey<CustomerProfile>(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
