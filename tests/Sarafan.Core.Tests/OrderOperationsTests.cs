// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class OrderOperationsTests
{
    [Test]
    public void IanaSuffixValidation_AllowsAtMostOneTerminalRootDot()
    {
        using (Assert.EnterMultipleScope())
        {
            IReadOnlySet<string> values = new HashSet<string>(["COM"], StringComparer.OrdinalIgnoreCase);
            Assert.That(IanaTopLevelDomainRules.HasValidSuffix("shop.example.com", values), Is.True);
            Assert.That(IanaTopLevelDomainRules.HasValidSuffix("shop.example.com.", values), Is.True);
            Assert.That(IanaTopLevelDomainRules.HasValidSuffix("shop.example.com..", values), Is.False);
        }
    }

    [Test]
    public async Task Operations_ReturnsTheCanonicalStatusCatalogueWithoutConsentOrAuthentication()
    {
        using var client = IntegrationTestEnvironment.Factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = false
            });

        using var response = await client.GetAsync("/api/v1/orders/ops");
        var body = await response.Content.ReadFromJsonAsync<OrderOpsDto>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(body, Is.Not.Null);
            Assert.That(body!.Statuses, Has.Count.EqualTo(12));
            Assert.That(body.Currencies, Is.EqualTo(new[]
            {
                new EnumOpsItemDto((int)Currency.Rub, "Российский рубль", "rub"),
                new EnumOpsItemDto((int)Currency.Usd, "Доллар США", "usd")
            }));
            Assert.That(body.ProductSourceUrl.MaximumLength, Is.EqualTo(2048));
            Assert.That(body.ProductSourceUrl.TopLevelDomainListVersion, Is.EqualTo("2026091400"));
            Assert.That(body.ProductSourceUrl.TopLevelDomains, Has.Count.GreaterThan(1000));
            Assert.That(body.ProductSourceUrl.TopLevelDomains, Does.Contain("COM"));
            Assert.That(body.ProductSourceUrl.TopLevelDomains, Does.Contain("XN--P1AI"));
            Assert.That(body.ProductSourceUrl.TopLevelDomains, Does.Not.Contain("INVALID"));
            Assert.That(body.ProductSourceUrl.TopLevelDomains, Is.Ordered);
            Assert.That(body.ProductSourceUrl.TopLevelDomains, Is.Unique);
        }

        Assert.That(body!.Statuses, Is.EqualTo(new[]
        {
            new OrderStatusOpsItemDto(0, "На проверке", "under_review", 0, "На проверке", "under_review", false, 14),
            new OrderStatusOpsItemDto(100, "Расчёт готов", "quote_ready", 100, "Расчёт готов", "quote_ready", false, 32),
            new OrderStatusOpsItemDto(200, "Расчёт истёк", "quote_expired", 200, "Расчёт истёк", "quote_expired", false, 32),
            new OrderStatusOpsItemDto(300, "Оплачен", "paid", 300, "Выполняется", "in_progress", false, 48),
            new OrderStatusOpsItemDto(310, "Выкупаем товар", "purchasing_item", 300, "Выполняется", "in_progress", false, 56),
            new OrderStatusOpsItemDto(320, "Доставляем на склад в США", "delivering_to_us_warehouse", 300, "Выполняется", "in_progress", false, 64),
            new OrderStatusOpsItemDto(330, "Получен на складе в США", "delivered_to_us_warehouse", 300, "Выполняется", "in_progress", false, 70),
            new OrderStatusOpsItemDto(340, "Доставляем в Россию", "delivering_to_russia", 300, "Выполняется", "in_progress", false, 78),
            new OrderStatusOpsItemDto(360, "Получен на складе в России", "delivered_to_russian_warehouse", 300, "Выполняется", "in_progress", false, 86),
            new OrderStatusOpsItemDto(380, "Доставляем по России", "delivering_in_russia", 300, "Выполняется", "in_progress", false, 94),
            new OrderStatusOpsItemDto(400, "Получен", "received", 400, "Завершён", "completed", true, 100),
            new OrderStatusOpsItemDto(500, "Отменён", "cancelled", 500, "Отменён", "cancelled", true, 100)
        }));
    }
}
