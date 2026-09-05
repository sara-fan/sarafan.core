// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text;

namespace Sarafan.Core.Authentication;

public static class BackofficePasswordRules
{
    public const int MinimumLength = 12;
    public const int MaximumUtf8Bytes = 72;

    public static bool IsValid(string? password)
        => !string.IsNullOrWhiteSpace(password)
            && password.Length >= MinimumLength
            && Encoding.UTF8.GetByteCount(password) <= MaximumUtf8Bytes;

    public static void ValidateConfigurationPassword(string? password, string settingName)
    {
        if (!IsValid(password))
        {
            throw new InvalidOperationException(
                $"{settingName} must contain at least {MinimumLength} characters and no more than {MaximumUtf8Bytes} UTF-8 bytes");
        }
    }
}
