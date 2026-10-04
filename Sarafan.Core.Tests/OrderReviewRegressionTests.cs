// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    [TestCase("product", "order_not_editable")]
    [TestCase("calculate", "order_review_unavailable")]
    [TestCase("confirm", "order_review_unavailable")]
    [TestCase("reject", "order_review_unavailable")]
    public async Task ExpiredQuotesRemainExpiredAfterRejectedStaffActions(string action, string code)
    {
        var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, OrderPricingInputs.Empty), actorId, Shift, default);
        await service.ConfirmPricingAsync("12345678-1", new(saved.UpdatedAt), actorId, Shift, default);
        var snapshot = await db.OrderPricingSnapshots.OrderByDescending(row => row.Id).FirstAsync();
        var expired = At(snapshot.ValidUntil!.Value);
        var requestVersion = order.UpdatedAt;
        Func<Task> attempt = action switch
        {
            "product" => () => expired.UpdateProductAsync("12345678-1", new() { ExpectedUpdatedAt = requestVersion, ProductName = "Исправлено", SellerPrice = new(50, Currency.Usd), Quantity = 2 }, actorId, Shift, default),
            "calculate" => () => expired.UpdatePricingAsync("12345678-1", new(requestVersion, OrderPricingInputs.Empty), actorId, Shift, default),
            "confirm" => () => expired.ConfirmPricingAsync("12345678-1", new(requestVersion), actorId, Shift, default),
            _ => () => expired.RejectReviewAsync("12345678-1", new() { ExpectedUpdatedAt = requestVersion, Reason = "Причина" }, actorId, Shift, default)
        };
        Assert.That((await Assert.ThrowsAsync<ServiceException>(async () => await attempt()))!.Code, Is.EqualTo(code));
        Assert.That((await db.Orders.AsNoTracking().SingleAsync()).Status, Is.EqualTo(OrderStatus.QuoteExpired));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.QuoteExpired), Is.EqualTo(1));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.ProductChanged || row.Kind == OrderHistoryKind.ReviewRejected), Is.Zero);
        Assert.That(await db.OrderPricingSnapshots.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task LegacyConfirmedQuotesExposeCompletionTimeToCustomerAndStaff()
    {
        var calculation = await OrderPriceCalculator.CalculateAsync(db, order, Now, OrderPricingInputs.Empty, null, default);
        var payload = JsonSerializer.Serialize(calculation, WebJson);
        var confirmedAt = Now.AddHours(1);
        db.Add(new OrderPricingSnapshot { Order = order, At = confirmedAt, ValidUntil = confirmedAt.AddDays(1), Payload = payload });
        db.Add(new OrderPricingSnapshot { Order = order, At = Now, ValidUntil = Now.AddDays(1), Payload = payload });
        db.Add(new OrderPricingSnapshot { Order = order, At = confirmedAt.AddMinutes(1), Payload = payload });
        order.UpdatePricing(confirmedAt, true);
        await db.SaveChangesAsync();
        var customer = await service.GetAsync(order.CustomerId, "12345678-1", default);
        var staff = await service.GetForBackofficeAsync("12345678-1", Shift, default);
        Assert.That(customer.ReviewCompletedAt, Is.EqualTo(confirmedAt));
        Assert.That(staff.ReviewCompletedAt, Is.EqualTo(confirmedAt));
        Assert.That(customer.ReviewReason, Is.Null);
        Assert.That(staff.ReviewReason, Is.Null);
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(), Is.Zero);
    }

    [Test]
    public async Task UnifiedReviewResultTakesPrecedenceOverLegacyConfirmation()
    {
        var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, OrderPricingInputs.Empty), actorId, Shift, default);
        await service.ConfirmPricingAsync("12345678-1", new(saved.UpdatedAt), actorId, Shift, default);
        var result = await service.GetAsync(order.CustomerId, "12345678-1", default);
        var snapshot = await db.OrderPricingSnapshots.OrderByDescending(row => row.Id).FirstAsync();
        db.Add(new OrderPricingSnapshot { Order = order, At = Now.AddMinutes(-1), ValidUntil = Now.AddDays(1), Payload = snapshot.Payload });
        await db.SaveChangesAsync();
        Assert.That((await service.GetAsync(order.CustomerId, "12345678-1", default)).ReviewCompletedAt, Is.EqualTo(result.ReviewCompletedAt));
    }

    [Test]
    public async Task CompletionTimestampRemainsReadableWhenResultEvidenceIsNull()
    {
        db.Add(new OrderHistoryEvent
        {
            Order = order,
            At = Now,
            Kind = OrderHistoryKind.QuoteConfirmed,
            Areas = OrderHistoryArea.Status,
            ActorType = OrderHistoryActor.System,
            ActorName = "Система",
            Payload = "null"
        });
        await db.SaveChangesAsync();
        var result = await service.GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(result.ReviewCompletedAt, Is.EqualTo(Now));
        Assert.That(result.ReviewReason, Is.Null);
    }

    [TestCase("order_checkout_unavailable", "Оформление недоступно")]
    [TestCase("order_review_unavailable", "Проверка заказа недоступна")]
    public void StateFailuresHaveOperationSpecificRussianProblems(string code, string title)
    {
        var problem = new SarafanProblemDetailsFactory(NullLogger<SarafanProblemDetailsFactory>.Instance).Create(new DefaultHttpContext(), 409, code);
        Assert.That(problem.Status, Is.EqualTo(409));
        Assert.That(problem.Title, Is.EqualTo(title));
        Assert.That(problem.Type, Does.EndWith(code.Replace('_', '-')));
        Assert.That(problem.Detail, Does.Contain("Обновите заказ"));
        Assert.That(problem.Detail, Does.Not.Contain("Изменять товар"));
    }
}
