// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    private async Task PaymentCheckout(decimal? delivery = null)
    {
        if (delivery.HasValue) db.Add(Tariff(ServiceKind.DomesticDelivery, PriceMethod.Fixed, Currency.Rub, amount: delivery));
        await CustomsQuote(120);
        order.SaveCheckout(JsonSerializer.Serialize(new OrderCheckoutDto(CustomerProfileDto.From(await db.Customers.SingleAsync()),
            new("courier", "Курьер", "Адрес")), WebJson), Now);
        await db.SaveChangesAsync();
    }
    private PaymentInformationService PaymentInformation(TimeProvider? clock = null)
        => new(db, clock ?? new Clock(), NullLogger<PaymentInformationService>.Instance);
    private OrderPaymentService PaymentRead(TimeProvider? clock = null)
    {
        var time = clock ?? new Clock();
        var orders = new OrderService(db, null!, null!, null!, null!, null!, time, NullLogger<OrderService>.Instance);
        return new(orders, PaymentInformation(time), time, NullLogger<OrderPaymentService>.Instance);
    }
    private async Task<PaymentBundleDto> EnabledPaymentBundle()
    {
        var information = PaymentInformation();
        var draft = await information.CreateAsync(PaymentInformationTests.Complete(), actorId, PaymentInformationTests.Admin, default);
        return await information.EnableAsync(draft.Id, new(draft.Version, null), actorId, PaymentInformationTests.Admin, default);
    }

    [TestCase(null)]
    [TestCase(0)]
    [TestCase(100)]
    public async Task PaymentReadUsesSavedTotalAndKnownDeliveryExcludingCustoms(decimal? delivery)
    {
        await PaymentCheckout(delivery);
        var noBundle = await PaymentRead().GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(noBundle.CanPay, Is.False); Assert.That(noBundle.PaymentInformation, Is.Null);
        var bundle = await EnabledPaymentBundle();
        var result = await PaymentRead().GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(result.CanPay, Is.True); Assert.That(result.PaymentInformation!.BundleId, Is.EqualTo(bundle.Id));
        Assert.That(result.Order.Pricing.DomesticDeliveryRub, Is.EqualTo(delivery));
        Assert.That(result.MainPaymentRub, Is.EqualTo(result.Order.Pricing.TotalRub + (delivery ?? 0)));
        Assert.That((await service.GetForBackofficeAsync("12345678-1", Shift, default)).MainPaymentRub, Is.EqualTo(result.MainPaymentRub));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => PaymentRead().GetAsync(int.MaxValue, "12345678-1", default)))!.StatusCode, Is.EqualTo(404));
        await PaymentInformation().DisableAsync(bundle.Id, bundle.Version, actorId, PaymentInformationTests.Admin, default);
        Assert.That((await PaymentRead().GetAsync(order.CustomerId, "12345678-1", default)).CanPay, Is.False);
    }

    [TestCase(BackofficeRoles.Administrator)]
    [TestCase(BackofficeRoles.ShiftManager)]
    [TestCase(BackofficeRoles.SeniorOperator)]
    [TestCase(BackofficeRoles.Operator)]
    public async Task PaymentStaffConfirmationHasOneStatusEventAndPreservesCustoms(string role)
    {
        await PaymentCheckout(100);
        var before = order.UpdatedAt;
        var snapshots = await db.OrderPricingSnapshots.Select(row => row.Payload).ToArrayAsync();
        var paid = await service.MarkOrderPaidAsync("12345678-1", new(before), actorId, [role], default);
        Assert.That(paid.Status, Is.EqualTo(OrderStatus.Paid)); Assert.That(paid.CanMarkOrderPaid, Is.False);
        Assert.That(paid.UpdatedAt, Is.GreaterThan(before)); Assert.That(paid.CustomsPaid, Is.False);
        Assert.That(await db.OrderPricingSnapshots.Select(row => row.Payload).ToArrayAsync(), Is.EqualTo(snapshots));
        var item = await db.Set<OrderHistoryEvent>().SingleAsync(row => row.Kind == OrderHistoryKind.OrderPaid);
        Assert.That(item.ActorId, Is.EqualTo(actorId)); Assert.That(item.At, Is.EqualTo(paid.UpdatedAt));
        var evidence = await service.HistoryDetailAsync("12345678-1", "0-" + item.Id, [role], default);
        Assert.That(evidence.Version, Is.EqualTo(3)); Assert.That(evidence.StatusBefore, Is.EqualTo(OrderStatus.QuoteReady));
        Assert.That(evidence.StatusAfter, Is.EqualTo(OrderStatus.Paid)); Assert.That(evidence.Event.ActorName, Is.EqualTo("Иванов Иван"));
        Assert.That((await PaymentRead().GetAsync(order.CustomerId, "12345678-1", default)).CanPay, Is.False);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkOrderPaidAsync("12345678-1", new(before), actorId, [role], default)))!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkOrderPaidAsync("12345678-1", new(paid.UpdatedAt), actorId, [role], default)))!.Code, Is.EqualTo("order_payment_unavailable"));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.OrderPaid), Is.EqualTo(1));
        Assert.Throws<InvalidOperationException>(() => order.MarkPaid(Now));
    }

    [Test]
    public async Task PaymentExpiryDisablesCustomerButPermitsReconciledStaffConfirmation()
    {
        await PaymentCheckout(); await EnabledPaymentBundle();
        var deadline = (await PaymentRead().GetAsync(order.CustomerId, "12345678-1", default)).Order.Pricing.ValidUntil!.Value;
        var expired = await PaymentRead(new OffsetClock(deadline)).GetAsync(order.CustomerId, "12345678-1", default);
        Assert.That(expired.CanPay, Is.False); Assert.That(expired.PaymentInformation, Is.Null);
        Assert.That(expired.Order.Status, Is.EqualTo(OrderStatus.QuoteExpired));
        var staff = await service.GetForBackofficeAsync("12345678-1", Shift, default);
        Assert.That(staff.CanMarkOrderPaid, Is.True);
        var paid = await service.MarkOrderPaidAsync("12345678-1", new(staff.UpdatedAt), actorId, Shift, default);
        Assert.That(paid.Status, Is.EqualTo(OrderStatus.Paid));
    }

    [TestCase(0)]
    [TestCase(1)]
    public async Task PaymentReadDropsInformationWhenQuoteExpiresDuringBundleLoad(int ticksAfterDeadline)
    {
        await PaymentCheckout(); await EnabledPaymentBundle();
        var deadline = (await PaymentRead().GetAsync(order.CustomerId, "12345678-1", default)).Order.Pricing.ValidUntil!.Value;
        var clock = new PaymentBoundaryClock(deadline.AddTicks(-1));
        var logger = new PaymentReadCompletionLogger(() => clock.Now = deadline.AddTicks(ticksAfterDeadline));
        var orders = new OrderService(db, null!, null!, null!, null!, null!, clock, NullLogger<OrderService>.Instance);
        var information = new PaymentInformationService(db, clock, logger);
        var read = new OrderPaymentService(orders, information, clock, NullLogger<OrderPaymentService>.Instance);

        var result = await read.GetAsync(order.CustomerId, "12345678-1", default);

        Assert.That(logger.Completed, Is.True, "The enabled bundle must finish loading before the final expiry check.");
        Assert.That(result.CanPay, Is.False);
        Assert.That(result.PaymentInformation, Is.Null);
    }

    private sealed class PaymentBoundaryClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PaymentReadCompletionLogger(Action completed) : ILogger<PaymentInformationService>
    {
        public bool Completed { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 1601 && state is IEnumerable<KeyValuePair<string, object?>> values
                && values.Any(pair => pair.Key == "Operation"
                    && Equals(pair.Value, typeof(PaymentInformationService).FullName + "." + nameof(PaymentInformationService.CurrentAsync))))
            {
                Completed = true;
                completed();
            }
        }
    }

    [Test]
    public async Task PaymentRejectsMissingCheckoutForecastInvalidStateActorAndVersion()
    {
        Assert.That((await PaymentRead().GetAsync(order.CustomerId, "12345678-1", default)).MainPaymentRub, Is.Null);
        await CustomsQuote(120);
        Assert.That((await PaymentRead().GetAsync(order.CustomerId, "12345678-1", default)).CanPay, Is.False);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkOrderPaidAsync("12345678-1", new(order.UpdatedAt), actorId, Shift, default)))!.Code, Is.EqualTo("order_payment_unavailable"));
        order.SaveCheckout(JsonSerializer.Serialize(new OrderCheckoutDto(CustomerProfileDto.From(await db.Customers.SingleAsync()), new("courier", "Курьер", "Адрес")), WebJson), Now);
        await db.SaveChangesAsync();
        foreach (var expected in new DateTimeOffset?[] { null, order.UpdatedAt.AddSeconds(-1) })
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkOrderPaidAsync("12345678-1", new(expected), actorId, Shift, default)))!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkOrderPaidAsync("12345678-1", new(order.UpdatedAt), actorId, ["unknown"], default)))!.Code, Is.EqualTo("access_denied"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => service.MarkOrderPaidAsync("12345678-1", new(order.UpdatedAt), int.MaxValue, Shift, default)))!.Code, Is.EqualTo("backoffice_user_not_found"));
        foreach (var status in new[] { OrderStatus.Cancelled, OrderStatus.CannotDeliver, (OrderStatus)999 })
        {
            db.Entry(order).Property(row => row.Status).CurrentValue = status; await db.SaveChangesAsync();
            Assert.That((await service.GetForBackofficeAsync("12345678-1", Shift, default)).CanMarkOrderPaid, Is.False);
            Assert.That((await PaymentRead().GetAsync(order.CustomerId, "12345678-1", default)).CanPay, Is.False);
        }
    }

    [Test]
    public async Task PaymentSaveConflictDoesNotPersistStatusOrEvent()
    {
        await PaymentCheckout();
        await using var failing = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(reviewDatabaseName, reviewDatabaseRoot).AddInterceptors(new DutySaveFailure()).Options);
        var action = new OrderService(failing, null!, null!, null!, null!, null!, new Clock(), NullLogger<OrderService>.Instance);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => action.MarkOrderPaidAsync("12345678-1", new(order.UpdatedAt), actorId, Shift, default)))!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That((await db.Orders.AsNoTracking().SingleAsync()).Status, Is.EqualTo(OrderStatus.QuoteReady));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(row => row.Kind == OrderHistoryKind.OrderPaid), Is.Zero);
        Assert.That(failing.ChangeTracker.Entries(), Is.Empty);
    }
}
