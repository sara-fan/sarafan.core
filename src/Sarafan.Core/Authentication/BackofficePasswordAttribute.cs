// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;

namespace Sarafan.Core.Authentication;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class BackofficePasswordAttribute : ValidationAttribute
{
    public BackofficePasswordAttribute()
        : base($"Пароль должен содержать от {BackofficePasswordRules.MinimumLength} до {BackofficePasswordRules.MaximumLength} символов.")
    {
    }

    public override bool IsValid(object? value)
        => value is null || value is string password && BackofficePasswordRules.IsValid(password);
}
