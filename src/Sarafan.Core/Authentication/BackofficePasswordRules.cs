// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text;

namespace Sarafan.Core.Authentication;

public static class BackofficePasswordRules
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 18;

    public static bool IsValid(string? password)
        => !string.IsNullOrWhiteSpace(password)
            && password.EnumerateRunes().Count() is >= MinimumLength and <= MaximumLength;

    public static void ValidateConfigurationPassword(string? password, string settingName)
    {
        if (!IsValid(password))
        {
            throw new InvalidOperationException(
                $"{settingName} must contain between {MinimumLength} and {MaximumLength} characters");
        }
    }
}
