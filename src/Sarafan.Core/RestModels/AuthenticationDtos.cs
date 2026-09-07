// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

namespace Sarafan.Core.RestModels;

public class RequestCodeRequest
{
    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(64, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [RegularExpression("^(register|login)$", ErrorMessage = "Укажите register или login.")]
    public string Purpose { get; set; } = string.Empty;
    public bool TermsAccepted { get; set; }
    public Guid TermsDocumentId { get; set; }
    public ConsentDecisionRequest? PersonalDataConsent { get; set; }
}

public sealed class VerifyCodeRequest : RequestCodeRequest
{
    [Required(ErrorMessage = "Поле обязательно для заполнения.")]
    [StringLength(16, ErrorMessage = "Длина поля не должна превышать {1} символов.")]
    public string Code { get; set; } = string.Empty;

    [StringLength(128)] public string? OnboardingToken { get; set; }
}

public sealed record CodeRequestDto(string? OnboardingToken);

public sealed record AuthenticationSessionDto(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    CustomerDto Customer);
