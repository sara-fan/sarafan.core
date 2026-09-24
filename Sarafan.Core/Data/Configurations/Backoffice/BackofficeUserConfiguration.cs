// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Backoffice;

internal sealed class BackofficeUserConfiguration : IEntityTypeConfiguration<BackofficeUser>
{
    public void Configure(EntityTypeBuilder<BackofficeUser> builder)
    {
        builder.ToTable("backoffice_users");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
        builder.Property(item => item.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(254).IsRequired();
        builder.Property(item => item.FirstName).HasColumnName("first_name").HasMaxLength(100).IsRequired();
        builder.Property(item => item.LastName).HasColumnName("last_name").HasMaxLength(100).IsRequired();
        builder.Property(item => item.Patronymic).HasColumnName("patronymic").HasMaxLength(100);
        builder.Property(item => item.PasswordHash).HasColumnName("password_hash").HasMaxLength(128).IsRequired();
        builder.Property(item => item.IsActive).HasColumnName("is_active");
        builder.Property(item => item.IsDemo).HasColumnName("is_demo");
        builder.Property(item => item.TokenVersion).HasColumnName("token_version");
        builder.Property(item => item.CreatedAt).HasColumnName("created_at");
        builder.Property(item => item.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(item => item.NormalizedEmail).IsUnique();
    }
}
