// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

namespace Sarafan.Core.RestModels;

public sealed class LegalDocumentRequest
{
    [Required, StringLength(40)] public string Kind { get; set; } = "";
    [Required, StringLength(8)] public string Locale { get; set; } = "ru";
    [Required, StringLength(200)] public string Title { get; set; } = "";
    [Required, StringLength(64)] public string DisplayVersion { get; set; } = "";
    [Required, StringLength(200)] public string FileName { get; set; } = "";
    [Required, MaxLength(262144)] public byte[] Source { get; set; } = [];
    [MaxLength(2)] public string[] CookieCategories { get; set; } = [];
    public int Revision { get; set; }
}

public sealed record LegalDocumentDto(Guid Id, string Kind, string Locale, string Title, string DisplayVersion,
    string Html, string SourceHash, string ContentHash, string RendererVersion, string[] CookieCategories,
    string State, DateTimeOffset? EffectiveAt, int Revision, DateTimeOffset CreatedAt, int? CreatedBy,
    DateOnly? EffectiveLocalDate, string EffectiveTimeZone);
public sealed record CurrentDocumentDto(LegalDocumentDto? Document, DateTimeOffset ServerNow, DateTimeOffset? NextChangeAt);
public sealed class PublishDocumentRequest
{
    public int Revision { get; set; }
    public bool Now { get; set; }
    public DateOnly? EffectiveDate { get; set; }
}
public sealed record DocumentRevisionRequest(int Revision);

public sealed class ConsentDecisionRequest
{
    public Guid DocumentId { get; set; }
    [Required, StringLength(64)] public string ContentHash { get; set; } = "";
    [Required, StringLength(16)] public string Decision { get; set; } = "";
    [MaxLength(2)] public string[] Categories { get; set; } = [];
    public Guid IdempotencyKey { get; set; }
}
public sealed record CookieConsentDto(string Status, string[] Categories, Guid? DocumentId,
    DateTimeOffset? DecidedAt, DateTimeOffset? ExpiresAt, DateTimeOffset ServerNow, DateTimeOffset? NextChangeAt);
public sealed record ConsentStatusDto(string Kind, string Status, Guid? RequiredVersion,
    Guid? AcceptedVersion, DateTimeOffset? DecidedAt);
public sealed record ConsentHistoryDto(string Id, string Kind, string Decision, Guid DocumentId,
    string DisplayVersion, string ContentHash, string[] Categories, DateTimeOffset At,
    string Source, string Scope, DateTimeOffset? AssociatedAt);
public sealed record CustomerConsentsDto(int CustomerId, DateTimeOffset ServerNow,
    ConsentStatusDto[] Statuses, ConsentHistoryDto[] History, RightsCaseDto[] RightsCases, DateTimeOffset? NextChangeAt = null);

public sealed class RightsRequest
{
    [Required, StringLength(32)] public string Kind { get; set; } = "withdrawal";
    public Guid IdempotencyKey { get; set; }
}
public sealed class RightsCaseUpdate
{
    public int Revision { get; set; }
    public int? ResponsibleStaffId { get; set; }
    [Required, StringLength(16)] public string State { get; set; } = "open";
    [StringLength(2000)] public string RetentionBasis { get; set; } = "";
    [StringLength(2000)] public string CompletionEvidence { get; set; } = "";
    public bool Extend { get; set; }
    [StringLength(1000)] public string ExtensionReason { get; set; } = "";
}
public sealed record RightsCaseDto(Guid Id, int CustomerId, string Kind, string State,
    DateTimeOffset ReceivedAt, DateTimeOffset DueAt, int? ResponsibleStaffId, string RetentionBasis,
    string CompletionEvidence, string ExtensionReason, bool Extended, DateTimeOffset? CompletedAt, int Revision);
public sealed record LegalAuditDto(long Id, int ActorId, string Action, DateTimeOffset At);
public sealed record ConsentRetentionDto(int Onboarding, int Events, int Artifacts, int Cases, int AuditEvents);
