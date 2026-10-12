// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;
namespace Sarafan.Core.Data.Configurations.Logibooks;

internal sealed class LogibooksRegisterConfiguration : IEntityTypeConfiguration<LogibooksRegister>
{
    public void Configure(EntityTypeBuilder<LogibooksRegister> b)
    {
        b.ToTable("logibooks_registers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        b.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(200);
        b.Property(x => x.Target).HasColumnName("target").HasMaxLength(2048);
        b.Property(x => x.MappingVersion).HasColumnName("mapping_version");
        b.Property(x => x.IntentHash).HasColumnName("intent_hash").HasMaxLength(64);
        b.Property(x => x.Request).HasColumnName("request").HasColumnType("jsonb");
        b.Property(x => x.State).HasColumnName("state");
        b.Property(x => x.Phase).HasColumnName("phase");
        b.Property(x => x.ActorId).HasColumnName("actor_id");
        b.Property(x => x.ActorName).HasColumnName("actor_name").HasMaxLength(605);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.AttemptId).HasColumnName("attempt_id");
        b.Property(x => x.Deadline).HasColumnName("deadline");
        b.Property(x => x.AuthenticationRetried).HasColumnName("authentication_retried");
        b.Property(x => x.InvestigationRequired).HasColumnName("investigation_required");
        b.Property(x => x.RemoteRegisterId).HasColumnName("remote_register_id");
        b.Property(x => x.Failure).HasColumnName("failure").HasColumnType("jsonb");
        b.HasIndex(x => new { x.Phase, x.CreatedAt });
    }
}
