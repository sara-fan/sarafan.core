// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Services;

public static class ConsentCalendar
{
    public const string TimeZoneId = "Europe/Moscow";
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Moscow).DateTime);
    public static DateTimeOffset Midnight(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(
        date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Moscow));
}
