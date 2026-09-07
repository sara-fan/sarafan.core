// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Services;

public static class ConsentCalendar
{
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    public static DateTimeOffset Midnight(DateOnly date) => new(TimeZoneInfo.ConvertTimeToUtc(
        date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), Moscow));

    public static DateTimeOffset WorkingDeadline(DateTimeOffset received, int days, ConsentOptions options)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(received, Moscow).DateTime);
        while (days > 0)
        {
            date = date.AddDays(1);
            var key = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var holiday = date.Month == 1 && date.Day <= 8 || (date.Month, date.Day) is (2, 23) or (3, 8) or (5, 1) or (5, 9) or (6, 12) or (11, 4);
            var working = options.WorkingDates.Contains(key)
                || !(holiday || date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || options.NonWorkingDates.Contains(key));
            if (working) days--;
        }
        return Midnight(date.AddDays(1)).AddTicks(-1);
    }
}
