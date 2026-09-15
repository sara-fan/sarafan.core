// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.RegularExpressions;

namespace Sarafan.Core.Services;

internal static partial class ProductSourceUrl
{
    internal const int MaximumLength = 2048;

    internal static string Normalize(string? sourceUrl, IReadOnlySet<string> topLevelDomains)
    {
        if (!TryCanonicalize(
                sourceUrl,
                allowProtocolFree: true,
                rejectWhitespace: true,
                requireValidDomainSuffix: true,
                enforceCanonicalLength: true,
                topLevelDomains,
                out var normalized))
        {
            throw Invalid();
        }

        return normalized;
    }

    internal static string NormalizeStored(string sourceUrl)
    {
        var stored = sourceUrl.Trim();
        return TryCanonicalize(
            stored,
            allowProtocolFree: false,
            rejectWhitespace: false,
            requireValidDomainSuffix: false,
            enforceCanonicalLength: true,
            null,
            out var normalized)
            ? normalized
            : stored;
    }

    internal static bool MatchesStored(string storedSourceUrl, string? requestedSourceUrl)
        => TryCanonicalize(
                storedSourceUrl,
                allowProtocolFree: false,
                rejectWhitespace: false,
                requireValidDomainSuffix: false,
                enforceCanonicalLength: false,
                null,
                out var stored)
            && TryCanonicalize(
                requestedSourceUrl,
                allowProtocolFree: true,
                rejectWhitespace: false,
                requireValidDomainSuffix: false,
                enforceCanonicalLength: false,
                null,
                out var requested)
            && string.Equals(stored, requested, StringComparison.Ordinal);

    private static bool TryCanonicalize(
        string? sourceUrl,
        bool allowProtocolFree,
        bool rejectWhitespace,
        bool requireValidDomainSuffix,
        bool enforceCanonicalLength,
        IReadOnlySet<string>? topLevelDomains,
        out string normalized)
    {
        normalized = string.Empty;
        var trimmed = sourceUrl?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Length > MaximumLength
            || rejectWhitespace && trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        string candidate;
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            candidate = $"https:{trimmed}";
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var authority = trimmed[(trimmed.IndexOf("//", StringComparison.Ordinal) + 2)..];
            if (string.IsNullOrEmpty(authority) || authority[0] is '/' or '?' or '#')
            {
                return false;
            }

            candidate = trimmed;
        }
        else if (ExplicitScheme().IsMatch(trimmed) && !DottedHostWithPort().IsMatch(trimmed))
        {
            return false;
        }
        else if (allowProtocolFree)
        {
            candidate = $"https://{trimmed}";
        }
        else
        {
            return false;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        try
        {
            var idnHost = uri.IdnHost;
            if (string.IsNullOrEmpty(idnHost)
                || requireValidDomainSuffix
                    && (topLevelDomains is null || !IanaTopLevelDomainRules.HasValidSuffix(idnHost, topLevelDomains)))
            {
                return false;
            }

            normalized = new UriBuilder(uri) { Host = idnHost }.Uri.AbsoluteUri;
            if (enforceCanonicalLength && normalized.Length > MaximumLength)
            {
                normalized = string.Empty;
                return false;
            }

            return true;
        }
        catch (UriFormatException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static ServiceException Invalid()
        => new(StatusCodes.Status400BadRequest, "invalid_order_url");

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitScheme();

    [GeneratedRegex("^(?:[A-Za-z0-9-]+(?:\\.[A-Za-z0-9-]+)+(?:\\.)?|\\[[0-9A-Fa-f:.]+\\]):[0-9]+(?:[/\\?#]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DottedHostWithPort();
}
