// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;
namespace Sarafan.Core.Data.Configurations.Logibooks;

internal sealed class LogibooksReconciliationConfiguration : IEntityTypeConfiguration<LogibooksReconciliation>
{
    public void Configure(EntityTypeBuilder<LogibooksReconciliation> b)
    {
        b.ToTable("logibooks_reconciliations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.RegisterId).HasColumnName("register_id");
        b.Property(x => x.AttemptId).HasColumnName("attempt_id");
        b.Property(x => x.ActorId).HasColumnName("actor_id");
        b.Property(x => x.ActorName).HasColumnName("actor_name").HasMaxLength(605);
        b.Property(x => x.At).HasColumnName("at");
        b.Property(x => x.Note).HasColumnName("note").HasMaxLength(2000);
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.HasOne(x => x.Register).WithMany().HasForeignKey(x => x.RegisterId).OnDelete(DeleteBehavior.Restrict);
        foreach (var property in b.Metadata.GetProperties().Where(x => !x.IsPrimaryKey()))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
