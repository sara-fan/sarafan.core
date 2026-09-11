// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Customers;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.Phone).HasColumnName("phone").HasMaxLength(12).IsRequired();
        builder.Property(item => item.State).HasColumnName("state");
        builder.Property(item => item.TokenVersion).HasColumnName("token_version").HasDefaultValue(0);
        builder.Property(item => item.CreatedAt).HasColumnName("created_at");
        builder.Property(item => item.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(item => item.Phone).IsUnique();
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_customers_phone_russian", "phone ~ '^\\+7[0-9]{10}$'");
            table.HasCheckConstraint("ck_customers_state", "state IN (0, 1, 2)");
        });
    }
}
