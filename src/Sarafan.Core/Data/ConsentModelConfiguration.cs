// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data;

internal static class ConsentModelConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var document = model.Entity<LegalDocument>();
        document.ToTable("legal_documents");
        document.HasKey(x => x.Id);
        document.Property(x => x.Revision).IsConcurrencyToken();
        document.Property(x => x.Kind).HasMaxLength(40);
        document.Property(x => x.Locale).HasMaxLength(8);
        document.Property(x => x.Title).HasMaxLength(200);
        document.Property(x => x.DisplayVersion).HasMaxLength(64);
        document.Property(x => x.SourceHash).HasMaxLength(64);
        document.Property(x => x.ContentHash).HasMaxLength(64);
        document.Property(x => x.RendererVersion).HasMaxLength(64);
        document.Property(x => x.State).HasMaxLength(16);
        document.HasIndex(x => new { x.Kind, x.Locale, x.DisplayVersion }).IsUnique();
        document.HasIndex(x => new { x.Kind, x.Locale, x.EffectiveAt });

        var evidence = model.Entity<ConsentEvent>();
        evidence.ToTable("consent_events");
        evidence.HasKey(x => x.Id);
        evidence.Property(x => x.SubjectKey).HasMaxLength(80);
        evidence.Property(x => x.ContentHash).HasMaxLength(64);
        evidence.Property(x => x.Kind).HasMaxLength(40);
        evidence.Property(x => x.Decision).HasMaxLength(16);
        evidence.Property(x => x.Source).HasMaxLength(32);
        evidence.HasIndex(x => new { x.SubjectKey, x.IdempotencyKey }).IsUnique();
        evidence.HasIndex(x => new { x.CustomerId, x.Kind, x.Id });
        evidence.HasOne(x => x.Document).WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        evidence.HasOne<Customer>().WithMany(x => x.ConsentEvents).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

        var association = model.Entity<ConsentAssociation>();
        association.ToTable("consent_associations");
        association.HasKey(x => x.Id);
        association.HasIndex(x => new { x.CustomerId, x.ConsentEventId }).IsUnique();
        association.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.ConsentEventId).OnDelete(DeleteBehavior.Cascade);
        association.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);

        var onboarding = model.Entity<ConsentOnboarding>();
        onboarding.ToTable("consent_onboarding");
        onboarding.HasKey(x => x.TokenHash);
        onboarding.Property(x => x.TokenHash).HasMaxLength(64);
        onboarding.Property(x => x.PhoneHash).HasMaxLength(64);
        onboarding.Property(x => x.PersonalDataHash).HasMaxLength(64);
        onboarding.HasIndex(x => x.ExpiresAt);
        onboarding.HasOne<LegalDocument>().WithMany().HasForeignKey(x => x.PersonalDataDocumentId).OnDelete(DeleteBehavior.Restrict);
        onboarding.HasOne<LegalDocument>().WithMany().HasForeignKey(x => x.TermsDocumentId).OnDelete(DeleteBehavior.Restrict);

        var rights = model.Entity<ConsentRightsCase>();
        rights.ToTable("consent_rights_cases");
        rights.HasKey(x => x.Id);
        rights.Property(x => x.Revision).IsConcurrencyToken();
        rights.Property(x => x.Kind).HasMaxLength(32);
        rights.Property(x => x.State).HasMaxLength(16);
        rights.Property(x => x.RetentionBasis).HasMaxLength(2000);
        rights.Property(x => x.CompletionEvidence).HasMaxLength(2000);
        rights.Property(x => x.ExtensionReason).HasMaxLength(1000);
        rights.HasIndex(x => new { x.CustomerId, x.IdempotencyKey }).IsUnique();
        rights.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);

        var audit = model.Entity<LegalAuditEvent>();
        audit.ToTable("legal_audit_events");
        audit.HasKey(x => x.Id);
        audit.Property(x => x.Action).HasMaxLength(32);
        foreach (var type in new[] { typeof(LegalDocument), typeof(ConsentEvent), typeof(ConsentAssociation), typeof(ConsentOnboarding), typeof(ConsentRightsCase), typeof(LegalAuditEvent) })
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
    }
}
