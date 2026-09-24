// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Customers;

internal sealed class CustomerPhotoConfiguration : IEntityTypeConfiguration<CustomerPhoto>
{
    public void Configure(EntityTypeBuilder<CustomerPhoto> builder)
    {
        builder.ToTable("customer_photos");

        builder.HasKey(item => item.CustomerId);

        builder.Property(item => item.CustomerId).HasColumnName("customer_id");
        builder.Property(item => item.FileName).HasColumnName("file_name").HasMaxLength(255);
        builder.Property(item => item.ContentType).HasColumnName("content_type").HasMaxLength(64);
        builder.Property(item => item.Content).HasColumnName("content").HasColumnType("bytea");
        builder.Property(item => item.Size).HasColumnName("size");
        builder.Property(item => item.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(item => item.Customer)
            .WithOne(item => item.Photo)
            .HasForeignKey<CustomerPhoto>(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
