// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json.Serialization;
using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed class PaymentInformationWriteRequest
{
    public PaymentRecipientType? RecipientType { get; set; }
    public string? RecipientName { get; set; }
    public string? Inn { get; set; }
    public string? Kpp { get; set; }
    public string? SettlementAccount { get; set; }
    public string? BankName { get; set; }
    public string? Bik { get; set; }
    public string? CorrespondentAccount { get; set; }
    public string? PaymentLink { get; set; }
    public Guid? Version { get; set; }
    public IFormFile? Qr { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PaymentBundleVersionRequest(Guid? Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnabledPaymentBundle(long Id, Guid Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnablePaymentBundleRequest(Guid? Version,
    [property: JsonRequired] EnabledPaymentBundle? ExpectedEnabled);

public sealed record PaymentBundleDto(long Id, Guid Version, PaymentInformationFields Information,
    string State, bool Enabled, string? QrUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int CreatedBy, int UpdatedBy,
    bool CanEdit, bool CanEnable, bool CanDisable, bool CanDelete, bool CanCopy);

public sealed class PaymentBundlePageDto : PagedResult<PaymentBundleDto>
{
    public string? State { get; init; }
    public EnabledPaymentBundle? EnabledBundle { get; init; }
}

public sealed record PaymentBundleLimitsDto(int NameMaxLength, int LinkMaxLength, int QrMaxBytes,
    string[] QrContentTypes, int QrMaxDimension, int QrMaxPixels, int QrMaxMetadataBytes);
public sealed record PaymentBundleOptionDto(int Value, string Name, string RouteAlias);
public sealed record PaymentBundleStateDto(string Value, string Name);
public sealed record PaymentBundleOpsDto(PaymentBundleOptionDto[] RecipientTypes, PaymentBundleStateDto[] States,
    PaymentBundleLimitsDto Limits, bool CanManage);
public sealed record CurrentPaymentInformationDto(PublicPaymentInformationDto? PaymentInformation);
public sealed record PublicPaymentInformationDto(long BundleId, PaymentInformationFields Information, string QrUrl);
