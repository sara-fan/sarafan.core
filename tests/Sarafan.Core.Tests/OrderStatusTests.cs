// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.Controllers;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.ModelTests;

[TestFixture]
public sealed class OrderStatusTests
{
    private static readonly ExpectedStatus[] ExpectedStatuses =
    [
        new(OrderStatus.UnderReview, 0, "На проверке", "under_review", 0, "На проверке", "under_review"),
        new(OrderStatus.QuoteReady, 100, "Расчёт готов", "quote_ready", 100, "Расчёт готов", "quote_ready"),
        new(OrderStatus.QuoteExpired, 200, "Расчёт истёк", "quote_expired", 200, "Расчёт истёк", "quote_expired"),
        new(OrderStatus.Paid, 300, "Оплачен", "paid", 300, "Выполняется", "in_progress"),
        new(OrderStatus.PurchasingItem, 310, "Выкупаем товар", "purchasing_item", 300, "Выполняется", "in_progress"),
        new(OrderStatus.DeliveringToUsWarehouse, 320, "Доставляем на склад в США", "delivering_to_us_warehouse", 300, "Выполняется", "in_progress"),
        new(OrderStatus.DeliveredToUsWarehouse, 330, "Получен на складе в США", "delivered_to_us_warehouse", 300, "Выполняется", "in_progress"),
        new(OrderStatus.DeliveringToRussia, 340, "Доставляем в Россию", "delivering_to_russia", 300, "Выполняется", "in_progress"),
        new(OrderStatus.DeliveredToRussianWarehouse, 360, "Получен на складе в России", "delivered_to_russian_warehouse", 300, "Выполняется", "in_progress"),
        new(OrderStatus.DeliveringInRussia, 380, "Доставка по России", "delivering_in_russia", 300, "Выполняется", "in_progress"),
        new(OrderStatus.Received, 400, "Получен", "received", 400, "Завершён", "completed"),
        new(OrderStatus.Cancelled, 500, "Отменён", "cancelled", 500, "Отменён", "cancelled")
    ];

    [Test]
    public void Catalogue_LocksSparseValuesNamesAliasesAndUpperMappings()
    {
        var statuses = Enum.GetValues<OrderStatus>();

        Assert.That(statuses, Has.Length.EqualTo(ExpectedStatuses.Length));
        foreach (var expected in ExpectedStatuses)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That((int)expected.Status, Is.EqualTo(expected.Value));
                Assert.That(expected.Status.GetDisplayName(), Is.EqualTo(expected.Name));
                Assert.That(expected.Status.GetRouteAlias(), Is.EqualTo(expected.RouteAlias));
                Assert.That(expected.Status.GetUpperStatusValue(), Is.EqualTo(expected.UpperStatusValue));
                Assert.That(expected.Status.GetUpperStatusDisplayName(), Is.EqualTo(expected.UpperStatusName));
                Assert.That(expected.Status.GetUpperStatusRouteAlias(), Is.EqualTo(expected.UpperStatusRouteAlias));
            }
        }

        Assert.That(
            statuses.Where(status => (int)status is >= 300 and < 400).Select(status => (int)status),
            Is.EqualTo(new[] { 300, 310, 320, 330, 340, 360, 380 }));
    }

    [TestCase(350)]
    [TestCase(370)]
    [TestCase(390)]
    public void UndefinedStatus_HasNoMetadata(int value)
    {
        var status = (OrderStatus)value;

        Assert.That(Enum.IsDefined(status), Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => status.GetDisplayName());
        Assert.Throws<ArgumentOutOfRangeException>(() => status.GetRouteAlias());
        Assert.Throws<ArgumentOutOfRangeException>(() => status.GetUpperStatusValue());
        Assert.Throws<ArgumentOutOfRangeException>(() => status.GetUpperStatusDisplayName());
        Assert.Throws<ArgumentOutOfRangeException>(() => status.GetUpperStatusRouteAlias());
    }

    [Test]
    public void Operations_BuildsTheCanonicalStatusCatalogue()
    {
        var result = new OrderOperationsController(new SarafanProblemDetailsFactory()).Operations();
        var response = (OkObjectResult)result.Result!;
        var body = (OrderOpsDto)response.Value!;

        Assert.That(response.StatusCode, Is.EqualTo(200));
        Assert.That(
            body.Statuses,
            Is.EqualTo(ExpectedStatuses.Select(expected => new OrderStatusOpsItemDto(
                expected.Value,
                expected.Name,
                expected.RouteAlias,
                expected.UpperStatusValue,
                expected.UpperStatusName,
                expected.UpperStatusRouteAlias))));
    }

    private sealed record ExpectedStatus(
        OrderStatus Status,
        int Value,
        string Name,
        string RouteAlias,
        int UpperStatusValue,
        string UpperStatusName,
        string UpperStatusRouteAlias);
}
