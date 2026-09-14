// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Collections.Frozen;

namespace Sarafan.Core.Services;

internal static class IanaTopLevelDomains
{
    private const string ResourceName = "Sarafan.Core.Resources.iana-tlds.txt";
    internal const string ListVersion = "2026091400";
    private static readonly IReadOnlyList<string> OrderedValues = Array.AsReadOnly(Load());
    private static readonly FrozenSet<string> Values = OrderedValues.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> All => OrderedValues;

    internal static bool HasValidSuffix(string host)
    {
        if (host.EndsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = host.EndsWith('.') ? host[..^1] : host;
        var labels = normalized.Split('.');
        return normalized.Length <= 253
            && labels.Length > 1
            && labels.All(IsValidLabel)
            && Values.Contains(labels[^1]);
    }

    private static bool IsValidLabel(string label)
        => label.Length is > 0 and <= 63
            && IsAsciiLetterOrDigit(label[0])
            && IsAsciiLetterOrDigit(label[^1])
            && label.All(value => IsAsciiLetterOrDigit(value) || value is '-');

    private static bool IsAsciiLetterOrDigit(char value)
        => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static string[] Load()
    {
        using var stream = typeof(IanaTopLevelDomains).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !value.StartsWith('#'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
