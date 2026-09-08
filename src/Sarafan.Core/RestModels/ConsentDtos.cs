// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;
using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public class LegalDocumentPreviewRequest
{
    [Required] public LegalDocumentKind? Kind { get; set; }
    [Required, StringLength(8)] public string Locale { get; set; } = "ru";
    [Required, StringLength(200)] public string Title { get; set; } = "";
    [StringLength(64)] public virtual string DisplayVersion { get; set; } = "";
    [Required, StringLength(200)] public string FileName { get; set; } = "";
    [Required, MaxLength(262144)] public byte[] Source { get; set; } = [];
    public DateOnly EffectiveDate { get; set; }
}

public sealed class LegalDocumentRequest : LegalDocumentPreviewRequest
{
    [Required, StringLength(64)] public override string DisplayVersion { get; set; } = "";
}

public sealed record LegalDocumentDto(Guid Id, LegalDocumentKind Kind, string Locale, string Title, string DisplayVersion,
    string Html, string SourceHash, string ContentHash, string RendererVersion, CookieCategory[] CookieCategories,
    DateTimeOffset EffectiveAt, DateTimeOffset CreatedAt, int? CreatedBy,
    DateOnly EffectiveLocalDate, string EffectiveTimeZone, bool? CanDelete);
public sealed record CurrentDocumentDto(LegalDocumentDto? Document, DateTimeOffset ServerNow, DateTimeOffset? NextChangeAt);
public sealed record LegalDocumentPreviewDto(string Html);
public sealed record LegalDocumentOpsItemDto(int Value, string Name, string RouteAlias);
public sealed record CookieCategoryOpsItemDto(int Value, string Name, bool Required);
public sealed record LegalDocumentOpsDto(IReadOnlyList<LegalDocumentOpsItemDto> Kinds,
    IReadOnlyList<CookieCategoryOpsItemDto> CookieCategories);

public sealed class ConsentDecisionRequest
{
    public Guid DocumentId { get; set; }
    [Required, StringLength(64)] public string ContentHash { get; set; } = "";
    [Required, StringLength(16)] public string Decision { get; set; } = "";
    [MaxLength(16)] public CookieCategory[] Categories { get; set; } = [];
    public Guid IdempotencyKey { get; set; }
}
public sealed record CookieConsentDto(string Status, CookieCategory[] Categories, Guid? DocumentId,
    DateTimeOffset? DecidedAt, DateTimeOffset? ExpiresAt, DateTimeOffset ServerNow, DateTimeOffset? NextChangeAt);
public sealed record ConsentStatusDto(LegalDocumentKind Kind, string Status, Guid? RequiredVersion,
    Guid? AcceptedVersion, DateTimeOffset? DecidedAt);
public sealed record ConsentHistoryDto(string Id, LegalDocumentKind Kind, string Decision, Guid DocumentId,
    string DisplayVersion, string ContentHash, CookieCategory[] Categories, DateTimeOffset At,
    string Source, string Scope, DateTimeOffset? AssociatedAt);
public sealed record CustomerConsentsDto(int CustomerId, DateTimeOffset ServerNow,
    ConsentStatusDto[] Statuses, ConsentHistoryDto[] History,
    CustomerConsentWithdrawalRequestDto? WithdrawalRequest, DateTimeOffset? NextChangeAt = null);

public sealed record CustomerConsentWithdrawalRequestDto(int CustomerId, DateTimeOffset RequestedAt, bool Processed);
public sealed class ProcessConsentWithdrawalRequest
{
    [Range(1, int.MaxValue)] public int CustomerId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
}
public sealed record LegalDocumentAuditDto(long Id, Guid DocumentId, int ActorId, string ActorName,
    string Action, DateTimeOffset At, LegalDocumentKind Kind, string Locale, string Title, string DisplayVersion,
    DateTimeOffset EffectiveAt, DateOnly EffectiveLocalDate, string EffectiveTimeZone,
    string SourceHash, string ContentHash);
public sealed class LegalDocumentAuditPageDto : PagedResult<LegalDocumentAuditDto>;
public sealed class CustomerConsentWithdrawalRequestPageDto : PagedResult<CustomerConsentWithdrawalRequestDto>;
public sealed record ConsentRetentionDto(int Onboarding, int Events);
