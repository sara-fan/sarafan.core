// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum LegalDocumentKind
{
    CookieConsent = 0,
    PersonalDataConsent = 1,
    UserAgreement = 2,
    OrderRules = 3,
    PrivacyPolicy = 4
}

public static class LegalDocumentKindExtensions
{
    public static string GetDisplayName(this LegalDocumentKind kind) => kind switch
    {
        LegalDocumentKind.CookieConsent => "Согласие на использование куки",
        LegalDocumentKind.PersonalDataConsent => "Согласие на обработку персональных данных",
        LegalDocumentKind.UserAgreement => "Пользовательское соглашение",
        LegalDocumentKind.OrderRules => "Правила заказа товаров",
        LegalDocumentKind.PrivacyPolicy => "Политика обработки персональных данных",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static string GetRouteAlias(this LegalDocumentKind kind) => kind switch
    {
        LegalDocumentKind.CookieConsent => "cookie-consent",
        LegalDocumentKind.PersonalDataConsent => "personal-data-consent",
        LegalDocumentKind.UserAgreement => "user-agreement",
        LegalDocumentKind.OrderRules => "order-rules",
        LegalDocumentKind.PrivacyPolicy => "privacy-policy",
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
