// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Authentication;

public interface IPhoneNormalizer
{
    bool TryNormalize(string? value, out string normalized);
}

public sealed class PhoneNormalizer : IPhoneNormalizer
{
    public bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim(' ');
        if (trimmed.Length == 11 && trimmed[0] == '8' && trimmed.All(IsAsciiDigit))
        {
            normalized = $"+7{trimmed[1..]}";
            return true;
        }

        if (!trimmed.StartsWith("+7", StringComparison.Ordinal)
            || trimmed.Count(character => character == '+') != 1
            || trimmed.Any(character => !IsAsciiDigit(character) && character is not ('+' or '(' or ')' or '-' or ' ')))
        {
            return false;
        }

        var digits = new string(trimmed.Where(IsAsciiDigit).ToArray());
        if (digits.Length != 11 || digits[0] != '7')
        {
            return false;
        }

        normalized = $"+{digits}";
        return true;
    }

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';
}
