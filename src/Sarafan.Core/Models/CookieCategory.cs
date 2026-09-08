// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum CookieCategory
{
    Mandatory = 0
}

public static class CookieCategoryExtensions
{
    public static string GetDisplayName(this CookieCategory category) => category switch
    {
        CookieCategory.Mandatory => "Обязательные",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    public static bool IsRequired(this CookieCategory category) => category switch
    {
        CookieCategory.Mandatory => true,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };
}
