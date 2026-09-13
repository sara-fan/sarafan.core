// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Sarafan.Core.RestModels;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class OrderOperationsTests
{
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
        }

        Assert.That(body!.Statuses, Is.EqualTo(new[]
        {
            new OrderStatusOpsItemDto(0, "На проверке", "under_review", 0, "На проверке", "under_review"),
            new OrderStatusOpsItemDto(100, "Расчёт готов", "quote_ready", 100, "Расчёт готов", "quote_ready"),
            new OrderStatusOpsItemDto(200, "Расчёт истёк", "quote_expired", 200, "Расчёт истёк", "quote_expired"),
            new OrderStatusOpsItemDto(300, "Оплачен", "paid", 300, "Выполняется", "in_progress"),
            new OrderStatusOpsItemDto(310, "Выкупаем товар", "purchasing_item", 300, "Выполняется", "in_progress"),
            new OrderStatusOpsItemDto(320, "Доставляем на склад в США", "delivering_to_us_warehouse", 300, "Выполняется", "in_progress"),
            new OrderStatusOpsItemDto(330, "Получен на складе в США", "delivered_to_us_warehouse", 300, "Выполняется", "in_progress"),
            new OrderStatusOpsItemDto(340, "Доставляем в Россию", "delivering_to_russia", 300, "Выполняется", "in_progress"),
            new OrderStatusOpsItemDto(360, "Получен на складе в России", "delivered_to_russian_warehouse", 300, "Выполняется", "in_progress"),
            new OrderStatusOpsItemDto(380, "Доставка по России", "delivering_in_russia", 300, "Выполняется", "in_progress"),
            new OrderStatusOpsItemDto(400, "Получен", "received", 400, "Завершён", "completed"),
            new OrderStatusOpsItemDto(500, "Отменён", "cancelled", 500, "Отменён", "cancelled")
        }));
    }
}
