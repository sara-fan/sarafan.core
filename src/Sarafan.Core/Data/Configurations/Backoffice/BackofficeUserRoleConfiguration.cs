// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Backoffice;

internal sealed class BackofficeUserRoleConfiguration : IEntityTypeConfiguration<BackofficeUserRole>
{
    public void Configure(EntityTypeBuilder<BackofficeUserRole> builder)
    {
        builder.ToTable("backoffice_user_roles");

        builder.HasKey(item => new { item.BackofficeUserId, item.RoleCode });

        builder.Property(item => item.BackofficeUserId).HasColumnName("backoffice_user_id");
        builder.Property(item => item.RoleCode).HasColumnName("role_code").HasMaxLength(32);

        builder.HasOne(item => item.BackofficeUser)
            .WithMany(item => item.UserRoles)
            .HasForeignKey(item => item.BackofficeUserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.Role)
            .WithMany(item => item.UserRoles)
            .HasForeignKey(item => item.RoleCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
