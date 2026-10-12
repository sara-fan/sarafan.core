// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using Sarafan.Core.Models;
using Sarafan.Core.Services;

namespace Sarafan.Core.Data;

// Equivalent presentation projection for the deterministic InMemory test provider.
internal static class LogibooksRowSearch
{
    internal static bool Matches(LogibooksRegisterRow row, string search)
    {
        var goods = LogibooksMapping.Read<RestModels.LogibooksPayload>(row.Register.Request).Parcels[row.RowNumber - 1];
        var issues = LogibooksMapping.Read<RestModels.LogibooksIssue[]>(row.Issues);
        string[] fields = [goods.PostingNumber, Text(goods.TnVed), goods.ProductName, Text(goods.ProductLink),
            Text(goods.LastName), Text(goods.FirstName), Text(goods.Patronymic), Text(goods.Phone), Text(goods.Email),
            Text(goods.PostalCode), Text(goods.City), Text(goods.Address), Passport(goods), Text(goods.Inn),
            goods.WeightKg.ToString("F3", CultureInfo.InvariantCulture).Replace(".", ","),
            goods.UnitPrice.ToString("N2", CultureInfo.InvariantCulture).Replace(",", " ").Replace(".", ",") + Currency.Usd.GetSymbol(),
            Currency.Usd.GetSymbol(), goods.Quantity.ToString(CultureInfo.InvariantCulture), Text(goods.Sender),
            Text(goods.SenderPhone), Text(goods.SenderEmail), Text(goods.SenderAddress),
            goods.CountryCode.ToString(CultureInfo.InvariantCulture), goods.SenderCountryCode.ToString(CultureInfo.InvariantCulture),
            goods.TradingCountryCode.ToString(CultureInfo.InvariantCulture),
            OutcomeName(row.Outcome), row.ParcelId?.ToString(CultureInfo.InvariantCulture) ?? "—",
            issues.Length == 0 ? "—" : string.Join("; ", issues.Select(x => x.Msg))];
        var term = ListDisplaySearch.Normalize(search);
        return fields.Any(x => ListDisplaySearch.Normalize(x).Contains(term, StringComparison.Ordinal));
    }

    private static string Text(string? value) => string.IsNullOrEmpty(value) ? "—" : value;
    private static string Passport(RestModels.LogibooksGoods goods)
        => Text(string.Join(" ", new[] { goods.PassportSeries, goods.PassportNumber,
            !string.IsNullOrEmpty(goods.PassportIssuedBy) || goods.PassportIssueDate.HasValue ? "выдан" : null,
            goods.PassportIssuedBy, goods.PassportIssueDate?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) }
            .Where(x => !string.IsNullOrEmpty(x))));

    internal static string OutcomeName(LogibooksRowOutcome outcome) => outcome switch
    {
        LogibooksRowOutcome.Created => "Создана",
        LogibooksRowOutcome.Rejected => "Отклонена",
        _ => "Результат неизвестен"
    };
}
