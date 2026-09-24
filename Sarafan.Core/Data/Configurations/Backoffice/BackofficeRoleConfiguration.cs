// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Backoffice;

internal sealed class BackofficeRoleConfiguration : IEntityTypeConfiguration<BackofficeRole>
{
    public void Configure(EntityTypeBuilder<BackofficeRole> builder)
    {
        builder.ToTable("backoffice_roles");

        builder.HasKey(item => item.Code);

        builder.Property(item => item.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(item => item.DisplayName).HasColumnName("display_name").HasMaxLength(64).IsRequired();

        builder.HasData(BackofficeRoles.Definitions.Select(item => new BackofficeRole
        {
            Code = item.Code,
            DisplayName = item.DisplayName
        }));
    }
}
