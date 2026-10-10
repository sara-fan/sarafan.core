// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    private async Task<OrderPricingDto> CustomsQuote(decimal? amount, bool confirm = true)
    {
        db.Add(Tariff(ServiceKind.CustomsPayments, PriceMethod.Manual, Currency.Rub));
        await db.SaveChangesAsync();
        var inputs = OrderPricingInputs.Empty with { ManualAmounts = amount is null ? [] : new() { [ServiceKind.CustomsPayments] = amount.Value } };
        var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, inputs), actorId, Shift, default);
        return confirm ? await service.ConfirmPricingAsync("12345678-1", new(saved.UpdatedAt), actorId, Shift, default) : saved;
    }

    [TestCase(BackofficeRoles.Administrator)]
    [TestCase(BackofficeRoles.ShiftManager)]
    [TestCase(BackofficeRoles.SeniorOperator)]
    [TestCase(BackofficeRoles.Operator)]
    public async Task StaffCanRecordDutyPaymentIndependentlyWithImmutableHistory(string role)
    {
        var quote = await CustomsQuote(120);
        var version = order.UpdatedAt;
        var before = await db.OrderPricingSnapshots.AsNoTracking().OrderBy(row => row.Id).Select(row => row.Payload).ToArrayAsync();
        Assert.That((await service.GetForBackofficeAsync("12345678-1", [role], default)).CanMarkCustomsPaid, Is.True);
        var marked = await service.MarkCustomsPaidAsync("12345678-1", new(version), actorId, [role], default);
        var customer = await service.GetAsync(order.CustomerId, "12345678-1", default);
        Assert.Multiple(() =>
        {
            Assert.That(marked.CustomsPaid, Is.True);
            Assert.That(marked.CanMarkCustomsPaid, Is.False);
            Assert.That(marked.UpdatedAt, Is.GreaterThan(version));
            Assert.That(marked.Status, Is.EqualTo(OrderStatus.QuoteReady));
            Assert.That(customer.Pricing.CustomsPaid, Is.True);
            Assert.That(customer.Pricing.CustomsRub, Is.EqualTo(120));
            Assert.That(customer.Pricing.TotalRub, Is.EqualTo(quote.Calculation.TotalRub));
        });
        Assert.That((await service.ListAsync(order.CustomerId, default)).Single().Pricing.CustomsPaid, Is.True);
        Assert.That(await db.OrderPricingSnapshots.AsNoTracking().OrderBy(row => row.Id).Select(row => row.Payload).ToArrayAsync(), Is.EqualTo(before));
        var history = await db.Set<OrderHistoryEvent>().SingleAsync(row => row.Kind == OrderHistoryKind.CustomsPaid);
        Assert.That(history.ActorId, Is.EqualTo(actorId));
        Assert.That(history.Areas, Is.EqualTo(OrderHistoryArea.Customs));
        var detail = await service.HistoryDetailAsync("12345678-1", "0-" + history.Id, [role], default);
        Assert.Multiple(() =>
        {
            Assert.That(detail.Version, Is.EqualTo(5));
            Assert.That(detail.CustomsPaidBefore, Is.False);
            Assert.That(detail.CustomsPaidAfter, Is.True);
            Assert.That(detail.StatusBefore, Is.EqualTo(detail.StatusAfter));
            Assert.That(detail.PricingAfter, Is.Null);
        });
        var duplicate = await Assert.ThrowsAsync<ServiceException>(() => service.MarkCustomsPaidAsync("12345678-1", new(marked.UpdatedAt), actorId, [role], default));
        Assert.That(duplicate!.Code, Is.EqualTo("customs_payment_unavailable"));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.CustomsPaid), Is.EqualTo(1));
    }

    [TestCase(null)]
    [TestCase(0)]
    public async Task UnknownOrZeroDutyCannotBeMarkedPaid(decimal? amount)
    {
        await CustomsQuote(amount);
        Assert.That((await service.GetForBackofficeAsync("12345678-1", Shift, default)).CanMarkCustomsPaid, Is.False);
        var version = order.UpdatedAt;
        var error = await Assert.ThrowsAsync<ServiceException>(() => service.MarkCustomsPaidAsync("12345678-1", new(version), actorId, Shift, default));
        Assert.That(error!.Code, Is.EqualTo("customs_payment_unavailable"));
        Assert.That(order.CustomsPaid, Is.False);
        Assert.That(order.UpdatedAt, Is.EqualTo(version));
    }

    [Test]
    public async Task DutyActionRejectsMissingSnapshotBadVersionActorIdentityAndUnknownState()
    {
        var missing = await Assert.ThrowsAsync<ServiceException>(() => service.MarkCustomsPaidAsync("12345678-1", new(order.UpdatedAt), actorId, Shift, default));
        Assert.That(missing!.Code, Is.EqualTo("customs_payment_unavailable"));
        await CustomsQuote(120);
        foreach (var expected in new DateTimeOffset?[] { null, order.UpdatedAt.AddSeconds(-1) })
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkCustomsPaidAsync("12345678-1", new(expected), actorId, Shift, default)))!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkCustomsPaidAsync("12345678-1", new(order.UpdatedAt), actorId, ["unknown"], default)))!.Code, Is.EqualTo("access_denied"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkCustomsPaidAsync("12345678-1", new(order.UpdatedAt), int.MaxValue, Shift, default)))!.Code, Is.EqualTo("backoffice_user_not_found"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkCustomsPaidAsync("12345678-999", new(order.UpdatedAt), actorId, Shift, default)))!.Code, Is.EqualTo("resource_not_found"));
        db.Entry(order).Property(row => row.Status).CurrentValue = (OrderStatus)999;
        await db.SaveChangesAsync();
        Assert.That((await service.GetForBackofficeAsync("12345678-1", Shift, default)).CanMarkCustomsPaid, Is.False);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkCustomsPaidAsync("12345678-1", new(order.UpdatedAt), actorId, Shift, default)))!.Code, Is.EqualTo("customs_payment_unavailable"));
        Assert.That(order.CustomsPaid, Is.False);
    }

    [Test]
    public async Task PaidFlagSurvivesRecalculationAndItsCustomerVisibilityGate()
    {
        await CustomsQuote(120, false);
        var later = new OrderService(db, null!, null!, null!, null!, new OrderLimitService(db, new Clock(), NullLogger<OrderLimitService>.Instance),
            new OffsetClock(Now.AddHours(1)), NullLogger<OrderService>.Instance);
        await later.MarkCustomsPaidAsync("12345678-1", new(order.UpdatedAt), actorId, Shift, default);
        Assert.That(order.UpdatedAt, Is.EqualTo(Now.AddHours(1)));
        Assert.That((await service.GetAsync(order.CustomerId, "12345678-1", default)).Pricing.CustomsRub, Is.Null);
        await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, OrderPricingInputs.Empty), actorId, Shift, default);
        Assert.That(order.CustomsPaid, Is.True);
        Assert.That((await service.GetForBackofficeAsync("12345678-1", Shift, default)).CustomsPaid, Is.True);
    }

    [Test]
    public async Task DutySaveConflictDoesNotPersistFlagOrHistory()
    {
        await CustomsQuote(120);
        await using var failing = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(reviewDatabaseName, reviewDatabaseRoot).AddInterceptors(new DutySaveFailure()).Options);
        var action = new OrderService(failing, null!, null!, null!, null!, null!, new Clock(), NullLogger<OrderService>.Instance);
        var error = await Assert.ThrowsAsync<ServiceException>(() => action.MarkCustomsPaidAsync("12345678-1", new(order.UpdatedAt), actorId, Shift, default));
        Assert.That(error!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That((await db.Orders.AsNoTracking().SingleAsync()).CustomsPaid, Is.False);
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.CustomsPaid), Is.Zero);
        Assert.That(failing.ChangeTracker.Entries(), Is.Empty);
    }
    private sealed class DutySaveFailure : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new DbUpdateConcurrencyException();
    }
}
