// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

namespace Sarafan.Core.Services;

public sealed class ConsentOptions : IValidatableObject
{
    public const string SectionName = "Consents";
    [Range(1, 365)] public int CookieDays { get; set; } = 180;
    [Range(1, 3650)] public int EvidenceDays { get; set; } = 1095;
    [Range(1, 120)] public int OnboardingMinutes { get; set; } = 15;
    public bool RetentionWorkerEnabled { get; set; } = true;
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (EvidenceDays < CookieDays)
            yield return new ValidationResult("Consents evidence retention must cover the full cookie validity period.", [nameof(EvidenceDays), nameof(CookieDays)]);
    }
}
