// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    [TestCase(OrderStatus.UnderReview)]
    [TestCase(OrderStatus.QuoteReady)]
    [TestCase(OrderStatus.QuoteExpired)]
    public async Task CustomerCanCancelUnpaidOrderOnceAndRetainReasonAndPricing(OrderStatus startingStatus)
    {
        db.Entry(order).Property(item => item.Status).CurrentValue = startingStatus;
        db.OrderPricingSnapshots.Add(new()
        {
            Order = order,
            At = Now,
            Payload = JsonSerializer.Serialize(await OrderPriceCalculator.CalculateAsync(db, order, Now,
                OrderPricingInputs.Empty, null, default), WebJson)
        });
        await db.SaveChangesAsync();
        var before = await service.GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(before.CanCancel, Is.True);

        var cancelled = await service.CancelAsync(order.CustomerId, before.OrderNumber,
            new CancelOrderRequest { ExpectedUpdatedAt = before.UpdatedAt, Reason = "  Не нужно  " }, default);
        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Status, Is.EqualTo(OrderStatus.Cancelled));
            Assert.That(cancelled.CanCancel, Is.False);
            Assert.That(cancelled.CancelledAt, Is.EqualTo(cancelled.UpdatedAt));
            Assert.That(cancelled.Pricing.TotalRub, Is.EqualTo(before.Pricing.TotalRub));
        });
        var replay = await service.CancelAsync(order.CustomerId, before.OrderNumber,
            new CancelOrderRequest { ExpectedUpdatedAt = before.UpdatedAt, Reason = "Other" }, default);
        Assert.That(replay.CancelledAt, Is.EqualTo(cancelled.CancelledAt));
        Assert.That(await db.OrderPricingSnapshots.CountAsync(), Is.EqualTo(1));
        var events = await db.Set<OrderHistoryEvent>().Where(item => item.Kind == OrderHistoryKind.CustomerCancelled).ToArrayAsync();
        Assert.That(events, Has.Length.EqualTo(1));
        var evidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(events[0].Payload, WebJson)!;
        Assert.That(evidence.StatusBefore, Is.EqualTo(startingStatus));
        Assert.That(evidence.StatusAfter, Is.EqualTo(OrderStatus.Cancelled));
        Assert.That(evidence.CancellationReason, Is.EqualTo("Не нужно"));
        var page = await service.HistoryAsync(before.OrderNumber, Admin, 1, 25, "timestamp", "desc",
            null, null, null, null, null, default);
        var detail = await service.HistoryDetailAsync(before.OrderNumber,
            page.Items.Single(item => item.Kind == OrderHistoryKind.CustomerCancelled).EventKey, Admin, default);
        Assert.That(detail.CancellationReason, Is.EqualTo("Не нужно"));
    }

    [Test]
    public async Task CustomerCancellationAcceptsBlankAndMaximumLengthReasons()
    {
        var blank = await service.CancelAsync(order.CustomerId, "12345678-1",
            new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt, Reason = "   " }, default);
        Assert.That(blank.Status, Is.EqualTo(OrderStatus.Cancelled));
        var second = new Order(order.CustomerId, 2, "https://example.com/product-2", 1,
            null, Guid.NewGuid(), Now);
        second.SetProduct(new("Другой товар", new(10, Currency.Usd), 1, null, null, null));
        db.Orders.Add(second);
        await db.SaveChangesAsync();
        await service.CancelAsync(order.CustomerId, "12345678-2",
            new CancelOrderRequest { ExpectedUpdatedAt = second.UpdatedAt, Reason = new string('x', 2000) }, default);
        var events = await db.Set<OrderHistoryEvent>().OrderBy(item => item.Id).ToArrayAsync();
        var firstEvidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(events[0].Payload, WebJson)!;
        var secondEvidence = JsonSerializer.Deserialize<OrderHistoryEvidence>(events[1].Payload, WebJson)!;
        Assert.That(firstEvidence.CancellationReason, Is.Null);
        Assert.That(secondEvidence.CancellationReason, Has.Length.EqualTo(2000));
    }

    [Test]
    public async Task CustomerCancellationRejectsStaleVersionsInvalidReasonAndOtherCustomers()
    {
        var wrongCustomer = Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId + 1,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt }, default));
        Assert.That(wrongCustomer!.Code, Is.EqualTo("resource_not_found"));
        var stale = Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt.AddSeconds(-1) }, default));
        Assert.That(stale!.Code, Is.EqualTo("order_update_conflict"));
        var invalid = Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt, Reason = new string('x', 2001) }, default));
        Assert.That(invalid!.Errors, Does.ContainKey("reason"));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.UnderReview));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(), Is.Zero);

        db.Entry(order).Property(item => item.Status).CurrentValue = OrderStatus.Paid;
        await db.SaveChangesAsync();
        var paid = Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = order.UpdatedAt }, default));
        Assert.That(paid!.Code, Is.EqualTo("order_not_cancellable"));
    }

    [Test]
    public async Task StaffQuoteConfirmationInvalidatesCustomerCancellationVersion()
    {
        var versionBeforeStaffChange = order.UpdatedAt;
        order.UpdatePricing(Now.AddSeconds(1), true);
        await db.SaveChangesAsync();

        var conflict = Assert.ThrowsAsync<ServiceException>(() => service.CancelAsync(order.CustomerId,
            "12345678-1", new CancelOrderRequest { ExpectedUpdatedAt = versionBeforeStaffChange }, default));
        Assert.That(conflict!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.QuoteReady));
        Assert.That(await db.Set<OrderHistoryEvent>().AnyAsync(item => item.Kind == OrderHistoryKind.CustomerCancelled),
            Is.False);
    }
}
