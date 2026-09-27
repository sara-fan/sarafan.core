// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Text.Json;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Data;

internal static class CatalogueDisplaySearch
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static IQueryable<ServiceCatalogueAuditEvent> Apply(IQueryable<ServiceCatalogueAuditEvent> query, string search)
        => ListDisplaySearch.Apply(query, search,
            row => AppDbContext.SearchDate(AppDbContext.SearchLocalTime("Europe/Moscow", row.At), "DD.MM.YYYY, HH24:MI") + " МСК",
            ListDisplaySearch.Label<ServiceCatalogueAuditEvent, ServiceCatalogueAuditAction>(row => row.Action, value => value.GetDisplayName()),
            ListDisplaySearch.Label<ServiceCatalogueAuditEvent, ServiceKind>(row => row.Service, value => value.GetDisplayName()),
            row => row.ActorName, row => Snapshot(row.Before), row => Snapshot(row.After));

    internal static string Snapshot(string? json)
    {
        if (json is null) return "—";
        var item = JsonSerializer.Deserialize<ServiceCatalogueSnapshotDto>(json, Json)!;
        return $"{item.Service.GetDisplayName()}; {item.PriceMethod.GetDisplayName()}; {Parameters(item)}; {Availability(item)}";
    }

    private static string Money(decimal value) => value.ToString("N2", Russian);
    private static string Parameters(ServiceCatalogueSnapshotDto item)
    {
        var unit = item.Currency!.Value.GetSymbol();
        if (item.PriceMethod == PriceMethod.Percent)
        {
            var parts = new List<string> { item.Percentage!.Value.ToString("#,0.####", Russian) + "%" };
            if (item.MinimumAmount is { } minimum) parts.Add($"мин. {Money(minimum)}{unit}");
            if (item.MaximumAmount is { } maximum) parts.Add($"макс. {Money(maximum)}{unit}");
            return string.Join(", ", parts);
        }
        if (item.PriceMethod != PriceMethod.Stepped)
            return item.PriceMethod == PriceMethod.Fixed ? Money(item.Amount!.Value) + unit : unit;
        var intervalUnit = Currency.Usd.GetSymbol();
        var bands = (item.Bands ?? []).Select(band =>
            (band.From is { } from ? $"свыше {Money(from)}{intervalUnit}" : band.By is null ? "любое значение" : "")
            + (band.By is { } by ? $"{(band.From is null ? "" : " ")}до {Money(by)}{intervalUnit}" : "")
            + $": {Money(band.Amount)}{unit}");
        var text = string.Join("; ", bands);
        return text.Length == 0 ? text : char.ToUpper(text[0], Russian) + text[1..];
    }

    private static string Availability(ServiceCatalogueSnapshotDto item)
    {
        static string Date(DateOnly value) => value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        if (item.AvailableFrom is null) return item.AvailableBy is { } end ? "по " + Date(end) : "в любое время";
        return item.AvailableBy is { } by ? $"{Date(item.AvailableFrom.Value)} — {Date(by)}" : "с " + Date(item.AvailableFrom.Value);
    }
}
