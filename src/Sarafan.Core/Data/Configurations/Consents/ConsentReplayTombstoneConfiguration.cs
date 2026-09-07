// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Consents;

internal sealed class ConsentReplayTombstoneConfiguration : IEntityTypeConfiguration<ConsentReplayTombstone>
{
    public void Configure(EntityTypeBuilder<ConsentReplayTombstone> builder)
    {
        builder.ToTable("consent_replay_tombstones");
        builder.HasKey(item => item.KeyHash);
        builder.Property(item => item.KeyHash).HasColumnName("key_hash").HasMaxLength(64);
        builder.Property(item => item.DocumentId).HasColumnName("document_id");
        builder.HasIndex(item => item.DocumentId);
        builder.HasOne<LegalDocument>().WithMany().HasForeignKey(item => item.DocumentId).OnDelete(DeleteBehavior.Restrict);
    }
}
