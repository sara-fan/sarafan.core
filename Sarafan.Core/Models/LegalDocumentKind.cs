// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum LegalDocumentKind
{
    // Values 0, 3 and 4 are retired and must never be reused.
    PersonalDataConsent = 1,
    UserAgreement = 2
}

public static class LegalDocumentKindExtensions
{
    public static string GetDisplayName(this LegalDocumentKind kind) => kind switch
    {
        LegalDocumentKind.PersonalDataConsent => "Согласие на обработку персональных данных",
        LegalDocumentKind.UserAgreement => "Пользовательское соглашение",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static string GetRouteAlias(this LegalDocumentKind kind) => kind switch
    {
        LegalDocumentKind.PersonalDataConsent => "personal-data-consent",
        LegalDocumentKind.UserAgreement => "user-agreement",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryFromRouteAlias(string? routeAlias, out LegalDocumentKind kind)
    {
        foreach (var candidate in Enum.GetValues<LegalDocumentKind>())
        {
            if (string.Equals(candidate.GetRouteAlias(), routeAlias, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
