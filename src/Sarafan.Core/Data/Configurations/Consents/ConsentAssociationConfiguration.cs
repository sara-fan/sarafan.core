// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Consents;

internal sealed class ConsentAssociationConfiguration : IEntityTypeConfiguration<ConsentAssociation>
{
    public void Configure(EntityTypeBuilder<ConsentAssociation> builder)
    {
        builder.ToTable("consent_associations");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.ConsentEventId).HasColumnName("consent_event_id");
        builder.Property(item => item.CustomerId).HasColumnName("customer_id");
        builder.Property(item => item.AssociatedAt).HasColumnName("associated_at");
        builder.Property(item => item.AuthenticationTokenId).HasColumnName("authentication_token_id");

        builder.HasIndex(item => new { item.CustomerId, item.ConsentEventId }).IsUnique();

        builder.HasOne(item => item.Event)
            .WithMany()
            .HasForeignKey(item => item.ConsentEventId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
