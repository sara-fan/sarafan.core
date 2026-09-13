// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum Currency
{
    Rub = 643,
    Usd = 840
}

public static class CurrencyExtensions
{
    public static string GetDisplayName(this Currency currency) => currency switch
    {
        Currency.Rub => "Российский рубль",
        Currency.Usd => "Доллар США",
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, null)
    };

    public static string GetRouteAlias(this Currency currency) => currency switch
    {
        Currency.Rub => "rub",
        Currency.Usd => "usd",
        _ => throw new ArgumentOutOfRangeException(nameof(currency), currency, null)
    };
}
