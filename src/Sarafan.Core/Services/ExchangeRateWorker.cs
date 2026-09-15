// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Services;

internal static class ExchangeRateSchedule
{
    private static readonly TimeZoneInfo Moscow = ResolveMoscow(TimeZoneInfo.FindSystemTimeZoneById);

    internal static TimeZoneInfo ResolveMoscow(Func<string, TimeZoneInfo> resolve)
    {
        try { return resolve("Europe/Moscow"); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Native Windows ID also works when IANA mapping is unavailable (for example under NLS).
            return resolve("Russian Standard Time");
        }
    }

    internal static DateOnly MoscowDate(DateTimeOffset utcNow)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, Moscow).DateTime);

}
