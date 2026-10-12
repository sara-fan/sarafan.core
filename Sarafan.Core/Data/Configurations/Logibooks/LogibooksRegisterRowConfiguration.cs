// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;
namespace Sarafan.Core.Data.Configurations.Logibooks;

internal sealed class LogibooksRegisterRowConfiguration : IEntityTypeConfiguration<LogibooksRegisterRow>
{
    public void Configure(EntityTypeBuilder<LogibooksRegisterRow> b)
    {
        b.ToTable("logibooks_register_rows");
        b.HasKey(x => new { x.RegisterId, x.RowNumber });
        b.Property(x => x.RegisterId).HasColumnName("register_id");
        b.Property(x => x.RowNumber).HasColumnName("row_number");
        b.Property(x => x.OrderId).HasColumnName("order_id");
        b.Property(x => x.OrderNumber).HasColumnName("order_number").HasMaxLength(29);
        b.Property(x => x.ExpectedUpdatedAt).HasColumnName("expected_updated_at");
        b.Property(x => x.Blocking).HasColumnName("blocking");
        b.Property(x => x.Outcome).HasColumnName("outcome");
        b.Property(x => x.ParcelId).HasColumnName("parcel_id");
        b.Property(x => x.StatusId).HasColumnName("status_id");
        b.Property(x => x.CheckStatus).HasColumnName("check_status");
        b.Property(x => x.Issues).HasColumnName("issues").HasColumnType("jsonb");
        b.Property(x => x.AcceptanceHistoryEventId).HasColumnName("acceptance_history_event_id");
        b.HasOne(x => x.Register).WithMany(x => x.Rows).HasForeignKey(x => x.RegisterId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AcceptanceHistoryEvent).WithMany().HasForeignKey(x => x.AcceptanceHistoryEventId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AcceptanceHistoryEventId).IsUnique();
        b.HasIndex(x => x.OrderId).IsUnique().HasFilter("blocking").HasDatabaseName("ux_logibooks_blocking_order");
    }
}
