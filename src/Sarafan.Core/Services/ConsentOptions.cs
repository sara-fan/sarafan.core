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
    [Range(1, 3650)] public int DraftDays { get; set; } = 90;
    public string[] NonWorkingDates { get; set; } = ["2026-01-09", "2026-03-09", "2026-05-11", "2026-12-31"];
    public string[] WorkingDates { get; set; } = [];
    public bool RetentionWorkerEnabled { get; set; } = true;
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (NonWorkingDates is null || WorkingDates is null
            || NonWorkingDates.Concat(WorkingDates).Any(x => !DateOnly.TryParseExact(x, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            || NonWorkingDates.Intersect(WorkingDates).Any())
            yield return new ValidationResult("Consents calendar dates must be distinct ISO dates with no conflicting working/non-working overrides.");
    }
}

public static class ConsentKinds
{
    public const string Cookies = "cookie-consent";
    public const string PersonalData = "personal-data-consent";
    public const string Agreement = "user-agreement";
    public static readonly string[] All = [Cookies, PersonalData, Agreement, "order-rules", "privacy-policy"];
}
