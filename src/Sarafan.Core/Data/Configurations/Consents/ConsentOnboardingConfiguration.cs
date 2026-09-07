// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Sarafan.Core.Models;

namespace Sarafan.Core.Data.Configurations.Consents;

internal sealed class ConsentOnboardingConfiguration : IEntityTypeConfiguration<ConsentOnboarding>
{
    public void Configure(EntityTypeBuilder<ConsentOnboarding> builder)
    {
        builder.ToTable("consent_onboarding");

        builder.HasKey(item => item.TokenHash);

        builder.Property(item => item.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
        builder.Property(item => item.PhoneHash).HasColumnName("phone_hash").HasMaxLength(64);
        builder.Property(item => item.PersonalDataDocumentId).HasColumnName("personal_data_document_id");
        builder.Property(item => item.TermsDocumentId).HasColumnName("terms_document_id");
        builder.Property(item => item.PersonalDataHash).HasColumnName("personal_data_hash").HasMaxLength(64);
        builder.Property(item => item.At).HasColumnName("at");
        builder.Property(item => item.ExpiresAt).HasColumnName("expires_at");
        builder.Property(item => item.UsedAt).HasColumnName("used_at");

        builder.HasIndex(item => item.ExpiresAt);

        builder.HasOne<LegalDocument>()
            .WithMany()
            .HasForeignKey(item => item.PersonalDataDocumentId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LegalDocument>()
            .WithMany()
            .HasForeignKey(item => item.TermsDocumentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
