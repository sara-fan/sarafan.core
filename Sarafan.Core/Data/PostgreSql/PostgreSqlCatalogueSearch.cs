// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Models;

namespace Sarafan.Core.Data;

internal static class PostgreSqlCatalogueSearch
{
    // Only fixed schema expressions and Core-owned catalogue labels enter SQL text.
    // User input is always a parameter; strpos treats %, _ and backslashes literally.
    internal static IQueryable<ServiceCatalogueAuditEvent> Apply(AppDbContext database, IQueryable<ServiceCatalogueAuditEvent> query, string search)
    {
        var fields = new[] {
            "to_char(at AT TIME ZONE 'Europe/Moscow', 'DD.MM.YYYY, HH24:MI') || ' МСК'",
            Label<ServiceCatalogueAuditAction>("action", value => value.GetDisplayName()),
            Label<ServiceKind>("service", value => value.GetDisplayName()),
            "actor_name", Snapshot("before"), Snapshot("after")
        };
        var predicate = string.Join(" OR ", fields.Select(field => $"strpos(lower(translate(coalesce(({field}), ''), chr(160) || chr(8239), '  ')), {{0}}) > 0"));
        var sql = $"SELECT id AS \"Value\" FROM service_catalogue_audit_events WHERE {predicate}";
        var ids = database.Database.SqlQueryRaw<long>(sql, ListDisplaySearch.Normalize(search));
        return query.Where(row => ids.Contains(row.Id));
    }

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string Label<T>(string field, Func<T, string> label) where T : struct, Enum
        => "CASE " + field + string.Concat(Enum.GetValues<T>().Select(value => $" WHEN {Convert.ToInt32(value)} THEN {Literal(label(value))}")) + " ELSE '—' END";

    private static string Number(string field, bool percentage = false)
    {
        var pattern = percentage ? ListDisplaySearch.MoneyPattern.Replace(".00", ".0000", StringComparison.Ordinal) : ListDisplaySearch.MoneyPattern;
        var formatted = $"to_char(({field})::numeric, '{pattern}')";
        if (percentage) formatted = $"rtrim(rtrim({formatted}, '0'), '.')";
        return $"replace(replace({formatted}, ',', ' '), '.', ',')";
    }

    private static string Snapshot(string column)
    {
        var item = '"' + column + '"';
        string Field(string name) => $"({item}->>'{name}')";
        var unit = Label<Currency>($"{Field("currency")}::int", value => value.GetSymbol());
        var percent = Number(Field("percentage"), true) + " || '%'"
            + $" || CASE WHEN {Field("minimumAmount")} IS NULL THEN '' ELSE ', мин. ' || {Number(Field("minimumAmount"))} || ({unit}) END"
            + $" || CASE WHEN {Field("maximumAmount")} IS NULL THEN '' ELSE ', макс. ' || {Number(Field("maximumAmount"))} || ({unit}) END";
        var intervalUnit = Literal(Currency.Usd.GetSymbol());
        var band = $"CASE WHEN b->>'from' IS NOT NULL THEN 'свыше ' || {Number("b->>'from'")} || {intervalUnit} WHEN b->>'by' IS NULL THEN 'любое значение' ELSE '' END"
            + $" || CASE WHEN b->>'by' IS NULL THEN '' ELSE CASE WHEN b->>'from' IS NULL THEN '' ELSE ' ' END || 'до ' || {Number("b->>'by'")} || {intervalUnit} END"
            + $" || ': ' || {Number("b->>'amount'")} || ({unit})";
        var bands = $"(SELECT string_agg({band}, '; ' ORDER BY ordinal) FROM jsonb_array_elements({item}->'bands') WITH ORDINALITY AS bands(b, ordinal))";
        // Matching is case-insensitive, so the initial uppercase letter needs no separate SQL transformation.
        var parameters = $"CASE {Field("priceMethod")}::int WHEN {(int)PriceMethod.Percent} THEN {percent} WHEN {(int)PriceMethod.Fixed} THEN {Number(Field("amount"))} || ({unit}) WHEN {(int)PriceMethod.Stepped} THEN {bands} ELSE ({unit}) END";
        var from = Field("availableFrom");
        var by = Field("availableBy");
        string Date(string field) => $"to_char(({field})::date, 'DD.MM.YYYY')";
        var availability = $"CASE WHEN {from} IS NULL THEN CASE WHEN {by} IS NULL THEN 'в любое время' ELSE 'по ' || {Date(by)} END WHEN {by} IS NULL THEN 'с ' || {Date(from)} ELSE {Date(from)} || ' — ' || {Date(by)} END";
        return $"CASE WHEN {item} IS NULL THEN '—' ELSE ({Label<ServiceKind>($"{Field("service")}::int", value => value.GetDisplayName())}) || '; ' || ({Label<PriceMethod>($"{Field("priceMethod")}::int", value => value.GetDisplayName())}) || '; ' || ({parameters}) || '; ' || ({availability}) END";
    }
}
