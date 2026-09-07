// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class ConsentEvent
{
    public long Id { get; set; }
    public int? CustomerId { get; set; }
    public string SubjectKey { get; set; } = "";
    public Guid DocumentId { get; set; }
    public LegalDocument Document { get; set; } = null!;
    public string ContentHash { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Decision { get; set; } = "";
    public string[] Categories { get; set; } = [];
    public string Source { get; set; } = "";
    public Guid IdempotencyKey { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
}

public sealed class ConsentAssociation
{
    public long Id { get; set; }
    public long ConsentEventId { get; set; }
    public ConsentEvent Event { get; set; } = null!;
    public int CustomerId { get; set; }
    public DateTimeOffset AssociatedAt { get; set; }
}

public sealed class ConsentOnboarding
{
    public string TokenHash { get; set; } = "";
    public string PhoneHash { get; set; } = "";
    public Guid PersonalDataDocumentId { get; set; }
    public Guid TermsDocumentId { get; set; }
    public string PersonalDataHash { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class ConsentRightsCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int CustomerId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public string Kind { get; set; } = "withdrawal";
    public string State { get; set; } = "open";
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public int? ResponsibleStaffId { get; set; }
    public string RetentionBasis { get; set; } = "";
    public string CompletionEvidence { get; set; } = "";
    public string ExtensionReason { get; set; } = "";
    public bool Extended { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Revision { get; set; } = 1;
}
