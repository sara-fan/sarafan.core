// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    [TestCase(PriceMethod.Manual)]
    [TestCase(PriceMethod.Auto)]
    [TestCase(null)]
    public async Task UnresolvedCustomsRejectsConfirmationWithoutAnyWrites(PriceMethod? method)
    {
        if (method is { } value) db.Add(Tariff(ServiceKind.CustomsPayments, value, Currency.Rub));
        await db.SaveChangesAsync();
        var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, OrderPricingInputs.Empty), actorId, Shift, default);
        var version = order.UpdatedAt;
        var snapshots = await db.OrderPricingSnapshots.CountAsync();
        var events = await db.Set<OrderHistoryEvent>().CountAsync();
        var payload = (await db.OrderPricingSnapshots.SingleAsync()).Payload;
        Assert.That(saved.CanConfirm, Is.False);
        Assert.That(saved.Calculation.TotalRub, Is.Not.Null);
        var error = Assert.ThrowsAsync<ServiceException>(() => service.ConfirmPricingAsync("12345678-1", new(version), actorId, Shift, default))!;
        var problem = new SarafanProblemDetailsFactory().Create(new DefaultHttpContext(), error.StatusCode, error.Code, error.Errors);
        Assert.Multiple(() =>
        {
            Assert.That(error.Code, Is.EqualTo("order_customs_unresolved"));
            Assert.That(problem.Type, Is.EqualTo("https://sarafan.sw.consulting/problems/order-customs-unresolved"));
            Assert.That(problem.Status, Is.EqualTo(409));
            Assert.That(problem.Title, Is.EqualTo("Таможенные платежи не определены"));
            Assert.That(problem.Detail, Does.Contain("Нулевая сумма"));
            Assert.That(problem.Instance, Does.StartWith("urn:sarafan:problem:"));
            Assert.That(order.Status, Is.EqualTo(OrderStatus.UnderReview));
            Assert.That(order.UpdatedAt, Is.EqualTo(version));
        });
        if (method == PriceMethod.Manual) Assert.That(problem.Errors, Does.ContainKey("manualAmounts"));
        else Assert.That(problem.Errors, Is.Null);
        Assert.That(await db.OrderPricingSnapshots.CountAsync(), Is.EqualTo(snapshots));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(), Is.EqualTo(events));
        Assert.That((await db.OrderPricingSnapshots.SingleAsync()).Payload, Is.EqualTo(payload));
    }

    [TestCase(0, Currency.Rub)]
    [TestCase(120, Currency.Rub)]
    [TestCase(2, Currency.Usd)]
    public async Task ResolvedCustomsPublishesOnlyAfterConfirmationAndSurvivesExpiry(decimal amount, Currency currency)
    {
        db.Add(Tariff(ServiceKind.CustomsPayments, PriceMethod.Manual, currency));
        await db.SaveChangesAsync();
        var inputs = OrderPricingInputs.Empty with { ManualAmounts = new() { [ServiceKind.CustomsPayments] = amount } };
        var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, inputs), actorId, Shift, default);
        var forecast = await service.GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(saved.CanConfirm, Is.True);
        Assert.That(forecast.Pricing.CustomsRub, Is.Null);
        var confirmed = await service.ConfirmPricingAsync("12345678-1", new(saved.UpdatedAt), actorId, Shift, default);
        var published = await service.GetAsync(order.CustomerId, "12345678-1", default);
        var rub = currency == Currency.Rub ? amount : amount * 80;
        Assert.Multiple(() =>
        {
            Assert.That(published.Pricing.CustomsRub, Is.EqualTo(rub));
            Assert.That(published.Pricing.DomesticDeliveryRub, Is.Null);
            Assert.That(published.Pricing.TotalRub, Is.EqualTo(saved.Calculation.TotalRub));
            Assert.That(confirmed.Calculation.Components.Single(item => item.Service == ServiceKind.CustomsPayments).Amount, Is.EqualTo(amount));
        });
        Assert.That((await service.ListAsync(order.CustomerId, default)).Single().Pricing, Is.EqualTo(published.Pricing));
        var later = new OrderService(db, null!, null!, null!, null!, null!, new OffsetClock(Now.AddHours(24)), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderService>.Instance);
        var expired = await later.GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(expired.Pricing.State, Is.EqualTo(CustomerPricingState.Expired));
        Assert.That(expired.Pricing.CustomsRub, Is.EqualTo(rub));
        var cancelled = await service.CancelAsync(order.CustomerId, "12345678-1",
            new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt }, default);
        Assert.That(cancelled.Status, Is.EqualTo(OrderStatus.Cancelled));
        Assert.That(cancelled.Pricing.CustomsRub, Is.EqualTo(rub));
        Assert.That(cancelled.Pricing.DomesticDeliveryRub, Is.Null);
    }

    [Test]
    public async Task ConfirmedLegacyQuoteWithUnknownCustomsRemainsReadableWithoutPayloadRewrite()
    {
        var calculation = await OrderPriceCalculator.CalculateAsync(db, order, Now, OrderPricingInputs.Empty, null, default);
        var payload = JsonSerializer.Serialize(calculation, WebJson);
        db.Add(new OrderPricingSnapshot { Order = order, At = Now, ValidUntil = Now.AddHours(24), Payload = payload });
        order.UpdatePricing(Now, true);
        await db.SaveChangesAsync();
        var customer = await service.GetAsync(order.CustomerId, "12345678-1", default);
        var staff = await service.GetPricingAsync("12345678-1", Shift, default);
        Assert.That(customer.Pricing.State, Is.EqualTo(CustomerPricingState.Confirmed));
        Assert.That(customer.Pricing.CustomsRub, Is.Null);
        Assert.That(staff.Confirmed, Is.True);
        Assert.That(staff.CanConfirm, Is.False);
        Assert.That((await db.OrderPricingSnapshots.SingleAsync()).Payload, Is.EqualTo(payload));
    }
}
