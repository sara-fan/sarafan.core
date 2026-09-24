// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.Logging;

namespace Sarafan.Core.Observability;

internal static class SarafanLogPolicy
{
    internal const string FrameworkEventName = "framework.diagnostic";
    internal const string UnknownFrameworkCategory = "framework";

    internal static bool IsApplicationCategory(string? category)
        => category?.StartsWith("Sarafan.", StringComparison.Ordinal) == true;

    internal static string FrameworkMessage(LogLevel level, string? category)
        => $"Framework {Severity(level)} event emitted by {SafeFrameworkCategory(category)}.";

    internal static string SafeFrameworkCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category) || category.Length > 160)
        {
            return UnknownFrameworkCategory;
        }

        return category.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-' or '+' or '`')
            ? category
            : UnknownFrameworkCategory;
    }

    private static string Severity(LogLevel level) => level switch
    {
        LogLevel.Warning => "warning",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "diagnostic"
    };
}
