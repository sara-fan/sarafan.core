// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;
namespace Sarafan.Core.Data.Configurations.Logibooks;

internal sealed class LogibooksReceiptConfiguration : IEntityTypeConfiguration<LogibooksReceipt>
{
    public void Configure(EntityTypeBuilder<LogibooksReceipt> b)
    {
        b.ToTable("logibooks_receipts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.RegisterId).HasColumnName("register_id");
        b.Property(x => x.AttemptId).HasColumnName("attempt_id");
        b.Property(x => x.At).HasColumnName("at");
        b.Property(x => x.Hash).HasColumnName("hash").HasMaxLength(64);
        b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        b.Property(x => x.ReconcilerId).HasColumnName("reconciler_id");
        b.Property(x => x.ReconcilerName).HasColumnName("reconciler_name").HasMaxLength(605);
        b.HasOne(x => x.Register).WithMany().HasForeignKey(x => x.RegisterId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.RegisterId, x.AttemptId, x.Hash }).IsUnique();
        foreach (var property in b.Metadata.GetProperties().Where(x => !x.IsPrimaryKey()))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
