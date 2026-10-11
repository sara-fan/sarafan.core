// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    [TestCase(BackofficeRoles.Administrator)]
    [TestCase(BackofficeRoles.ShiftManager)]
    [TestCase(BackofficeRoles.SeniorOperator)]
    [TestCase(BackofficeRoles.Operator)]
    public async Task PilotStagesAreSequentialVersionedAndProjectedWithoutPrivateEvidence(string role)
    {
        Assert.That(BackofficeAuthorization.IsAllowed([role], BackofficeAction.AdvanceOrderFulfilment), Is.True);
        await PaymentCheckout(100);
        var before = await service.GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(before.Stages, Is.Empty);
        Assert.That(before.Pricing.MainPaymentPaid, Is.False);
        var savedPrices = await db.OrderPricingSnapshots.Select(row => row.Payload).ToArrayAsync();
        var staff = await service.MarkOrderPaidAsync("12345678-1", new(order.UpdatedAt), actorId, [role], default);
        var dates = new List<DateTimeOffset> { staff.UpdatedAt };
        Assert.That(staff.MainPaymentPaid, Is.True);
        foreach (var target in new[] { OrderStatus.Delivering, OrderStatus.Received })
        {
            Assert.That(staff.NextStage, Is.EqualTo(target));
            var oldVersion = staff.UpdatedAt;
            staff = await service.AdvanceFulfilmentAsync("12345678-1", new(oldVersion, target), actorId, [role], default);
            Assert.That(staff.Status, Is.EqualTo(target));
            Assert.That(staff.UpdatedAt, Is.GreaterThan(oldVersion));
            Assert.That(staff.MainPaymentPaid, Is.True);
            dates.Add(staff.UpdatedAt);
            var stale = await Assert.ThrowsAsync<ServiceException>(() => service.AdvanceFulfilmentAsync(
                "12345678-1", new(oldVersion, target), actorId, [role], default));
            Assert.That(stale!.Code, Is.EqualTo("order_update_conflict"));
        }
        Assert.That(staff.NextStage, Is.Null);
        var customer = await service.GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(customer.Status.GetUpperStatusRouteAlias(), Is.EqualTo("completed"));
        Assert.That(customer.Stages.Select(stage => stage.Status), Is.EqualTo(new[] { OrderStatus.Paid, OrderStatus.Delivering, OrderStatus.Received }));
        Assert.That(customer.Stages.Select(stage => stage.At), Is.EqualTo(dates));
        Assert.That(customer.Pricing.MainPaymentPaid, Is.True);
        Assert.That(customer.Pricing.DomesticDeliveryRub, Is.EqualTo(100));
        Assert.That(customer.Pricing.CustomsPaid, Is.False);
        Assert.That((await service.ListAsync(order.CustomerId, default)).Single().Pricing.MainPaymentPaid, Is.True);
        Assert.That(await db.OrderPricingSnapshots.Select(row => row.Payload).ToArrayAsync(), Is.EqualTo(savedPrices));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.GetAsync(int.MaxValue, "12345678-1", default)))!.StatusCode, Is.EqualTo(404));
        var events = await db.Set<OrderHistoryEvent>().Where(row => row.Kind == OrderHistoryKind.DeliveryStarted || row.Kind == OrderHistoryKind.OrderReceived).OrderBy(row => row.Id).ToArrayAsync();
        Assert.That(events, Has.Length.EqualTo(2));
        foreach (var item in events)
        {
            var evidence = await service.HistoryDetailAsync("12345678-1", "0-" + item.Id, [role], default);
            Assert.That(evidence.Version, Is.EqualTo(3));
            Assert.That(evidence.Event.Areas, Is.EqualTo(OrderHistoryArea.Status));
            Assert.That(item.ActorId, Is.EqualTo(actorId));
        }
        Assert.That(customer.CanCancel, Is.False);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.AdvanceFulfilmentAsync(
            "12345678-1", new(staff.UpdatedAt, OrderStatus.Received), actorId, [role], default)))!.Code, Is.EqualTo("order_fulfilment_unavailable"));
    }

    [Test]
    public async Task PilotStageGuardsRejectSkipRolesUnknownTargetsAndMissingVersionsWithoutWrites()
    {
        await PaymentCheckout();
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.AdvanceFulfilmentAsync(
            "12345678-1", new(order.UpdatedAt, OrderStatus.Delivering), actorId, Shift, default)))!.Code, Is.EqualTo("order_fulfilment_unavailable"));
        var paid = await service.MarkOrderPaidAsync("12345678-1", new(order.UpdatedAt), actorId, Shift, default);
        foreach (var target in new[] { OrderStatus.Received, OrderStatus.Paid, OrderStatus.Cancelled, (OrderStatus)999 })
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.AdvanceFulfilmentAsync(
                "12345678-1", new(paid.UpdatedAt, target), actorId, Shift, default)))!.Code, Is.EqualTo("order_fulfilment_unavailable"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.AdvanceFulfilmentAsync(
            "12345678-1", new(paid.UpdatedAt, OrderStatus.Delivering), actorId, ["unknown"], default)))!.Code, Is.EqualTo("access_denied"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.AdvanceFulfilmentAsync(
            "12345678-1", new(null, OrderStatus.Delivering), actorId, Shift, default)))!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.AdvanceFulfilmentAsync(
            "12345678-1", new(paid.UpdatedAt, OrderStatus.Delivering), int.MaxValue, Shift, default)))!.Code, Is.EqualTo("backoffice_user_not_found"));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.OrderPaid), Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(() => order.AdvanceFulfilment(OrderStatus.Received, Now));
    }

    [TestCase(null)]
    [TestCase(0)]
    [TestCase(120)]
    public async Task CancelledUnconfirmedOrderExposesSavedCustomsOnlyAsHistoricalData(decimal? amount)
    {
        await CustomsQuote(amount, false);
        Assert.That((await service.GetAsync(order.CustomerId, "12345678-1", default)).Pricing.CustomsRub, Is.Null);
        var cancelled = await service.CancelAsync(order.CustomerId, "12345678-1", new() { ExpectedUpdatedAt = order.UpdatedAt }, default);
        Assert.That(cancelled.Pricing.CustomsRub, Is.EqualTo(amount));
        Assert.That((await service.ListAsync(order.CustomerId, default)).Single().Pricing.CustomsRub, Is.EqualTo(amount));
        Assert.That(cancelled.Pricing.MainPaymentPaid, Is.False);
        Assert.That(cancelled.Stages, Is.Empty);
        Assert.That((await service.GetForBackofficeAsync("12345678-1", Shift, default)).CanMarkOrderPaid, Is.False);
    }
}
