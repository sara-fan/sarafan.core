// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Authentication;

public interface IPhoneNormalizer
{
    bool TryNormalize(string? value, out string normalized);
    bool TryNormalize(string? value, out string normalized, out PhoneValidationReason? reason);
}

public enum PhoneValidationReason
{
    Empty,
    UnsupportedCharacters,
    WrongPrefix,
    TooShort,
    TooLong,
    FormattedDomesticNumber
}

public sealed class PhoneNormalizer : IPhoneNormalizer
{
    public bool TryNormalize(string? value, out string normalized)
        => TryNormalize(value, out normalized, out _);

    public bool TryNormalize(string? value, out string normalized, out PhoneValidationReason? reason)
    {
        normalized = string.Empty;
        reason = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            reason = PhoneValidationReason.Empty;
            return false;
        }

        var trimmed = value.Trim(' ');
        if (trimmed.Any(character => !IsAsciiDigit(character) && character is not ('+' or '(' or ')' or '-' or ' '))
            || trimmed.Count(character => character == '+') > 1
            || trimmed.Contains('+') && trimmed[0] != '+')
        {
            reason = PhoneValidationReason.UnsupportedCharacters;
            return false;
        }

        if (!trimmed.StartsWith("+7", StringComparison.Ordinal)
            && trimmed[0] != '8')
        {
            reason = PhoneValidationReason.WrongPrefix;
            return false;
        }

        var digits = new string(trimmed.Where(IsAsciiDigit).ToArray());
        if (digits.Length != 11)
        {
            reason = digits.Length < 11 ? PhoneValidationReason.TooShort : PhoneValidationReason.TooLong;
            return false;
        }

        if (trimmed[0] == '8' && !trimmed.All(IsAsciiDigit))
        {
            reason = PhoneValidationReason.FormattedDomesticNumber;
            return false;
        }

        normalized = $"+7{digits[1..]}";
        return true;
    }

    private static bool IsAsciiDigit(char character) => character is >= '0' and <= '9';
}
