// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

namespace Sarafan.Core.Services;

public sealed class ConsentOptions
{
    public const string SectionName = "Consents";
    [Range(1, 3650)] public int EvidenceDays { get; set; } = 1095;
    [Range(1, 120)] public int OnboardingMinutes { get; set; } = 15;
}
