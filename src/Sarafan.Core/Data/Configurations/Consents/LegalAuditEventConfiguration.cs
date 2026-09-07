// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Consents;

internal sealed class LegalAuditEventConfiguration : IEntityTypeConfiguration<LegalAuditEvent>
{
    public void Configure(EntityTypeBuilder<LegalAuditEvent> builder)
    {
        builder.ToTable("legal_audit_events");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.DocumentId).HasColumnName("document_id");
        builder.Property(item => item.RightsCaseId).HasColumnName("rights_case_id");
        builder.Property(item => item.ActorId).HasColumnName("actor_id");
        builder.Property(item => item.Action).HasColumnName("action").HasMaxLength(32);
        builder.Property(item => item.At).HasColumnName("at");
    }
}
