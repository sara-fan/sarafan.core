// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public class RequestCodeRequest
{
    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(64, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string Phone { get; set; } = string.Empty;

    public bool TermsAccepted { get; set; }
    public Guid? TermsDocumentId { get; set; }
    public ConsentDecisionRequest? PersonalDataConsent { get; set; }
}

public sealed class VerifyCodeRequest
{
    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(64, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(16, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string Code { get; set; } = string.Empty;

    [StringLength(128)] public string? OnboardingToken { get; set; }

    // Keep forbidden consent values opaque until the verification code has been checked.
    [JsonExtensionData, ValidateNever]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }
}

public sealed record CodeRequestDto(string? OnboardingToken);

public sealed record PhoneResolveRequest(
    [param: Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [param: StringLength(64, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    string Phone);

public sealed record PhoneResolveDto(
    AuthenticationFlowStep NextStep,
    IReadOnlyList<LegalDocumentKind> RequiredDocumentKinds);

public sealed record EnumOpsItemDto(int Value, string Name, string RouteAlias);
public sealed record AuthenticationOpsDto(IReadOnlyList<EnumOpsItemDto> Steps);

public sealed record AuthenticationSessionDto(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    CustomerDto Customer);
