// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.DependencyInjection;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Tests;

internal static class OrderProductTestData
{
    internal static OrderProductRequest Product() => new()
    {
        ProductName = "Тестовый товар",
        SellerPrice = new(10m, Currency.Usd)
    };

    internal static async Task SeedRates()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        database.ExchangeRateHistory.AddRange(Rate(Currency.Usd, 80), Rate(Currency.Eur, 100));
        await database.SaveChangesAsync();
    }

    internal static ExchangeRateHistory Rate(Currency currency, decimal value, int nominal = 1,
        DateOnly? date = null) => new()
        {
            Provider = "CBR",
            Source = "fixture",
            BaseCurrency = currency,
            QuoteCurrency = Currency.Rub,
            Nominal = nominal,
            OfficialRate = value,
            SourceEffectiveDate = date ?? new DateOnly(2026, 9, 1),
            RetrievedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z")
        };
}
