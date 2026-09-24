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
    {
        if (password is null)
        {
            return false;
        }

        var characterCount = 0;
        var containsNonWhitespace = false;
        foreach (var character in password.EnumerateRunes())
        {
            if (++characterCount > MaximumLength)
            {
                return false;
            }

            containsNonWhitespace |= !Rune.IsWhiteSpace(character);
        }

        return containsNonWhitespace && characterCount >= MinimumLength;
    }

    public static void ValidateConfigurationPassword(string? password, string settingName)
    {
        if (!IsValid(password))
        {
            throw new InvalidOperationException(
                $"{settingName} must contain between {MinimumLength} and {MaximumLength} characters");
        }
    }
}
