// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    [Test]
    public async Task RegisterSupports1000RowsAndAppliesPartialOutcomesIndependently()
    {
        await RegisterCommand();
        for (var number = 2; number <= 1000; number++)
        {
            var item = new Order(order.CustomerId, number, "https://shop.test/item", 1, null, Guid.NewGuid(), Now);
            item.SetProduct(new("Товар " + number, new(1.23m, Currency.Usd), 1, null, null, null));
            item.UpdatePricing(Now, true); item.MarkPaid(Now);
            db.Add(item);
        }
        await db.SaveChangesAsync();
        var selection = await db.Orders.OrderBy(x => x.CustomerOrderNumber)
            .Select(x => new LogibooksOrderSelection("12345678-" + x.CustomerOrderNumber, x.UpdatedAt)).ToArrayAsync();
        var preview = await Registers().PreviewAsync(new(Guid.NewGuid(), null, selection), Shift, default);
        Assert.That(preview.Parcels, Has.Length.EqualTo(1000)); Assert.That(preview.Register.RealWeightKg, Is.EqualTo(1001));
        var admitted = await Registers().CreateAsync(new(preview.OperationId, preview.FileName, selection, 1, preview.Fingerprint), actorId, Shift, default);
        var results = selection.Select((item, index) => index == 0 ? CreatedReceipt().Result!.Rows[0] :
            new LogibooksRemoteRow(index + 1, item.OrderNumber, "Invalid", null, null, null, [new("Demo", "productName", "Демо-отказ")])).ToArray();
        await Registers().ProcessAsync(Client(new(new(10, 1000, 1, 999, results), true, null)), default);
        var result = await Registers().GetAsync(admitted.Id, Shift, default);
        Assert.That(result.State, Is.EqualTo("PartiallySucceeded")); Assert.That(result.Created, Is.EqualTo(1)); Assert.That(result.Rejected, Is.EqualTo(999));
        var rejected = await Registers().RowsAsync(admitted.Id, 2, 100, "Демо-отказ", Shift, default);
        Assert.That(rejected.Pagination.TotalCount, Is.EqualTo(999)); Assert.That(rejected.Items[0].RowNumber, Is.EqualTo(102));
        Assert.That((await Registers().RowsAsync(admitted.Id, 1, 25, "Создана", Shift, default)).Pagination.TotalCount, Is.EqualTo(1));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(x => x.Kind == OrderHistoryKind.LogibooksRegistered), Is.EqualTo(1));
    }

    [Test]
    public async Task DeadAttemptBecomesUnknownWithoutRedispatchAndDifferentTargetRemainsQueued()
    {
        var command = await RegisterCommand(); var admitted = await Registers().CreateAsync(command, actorId, Shift, default);
        var saved = await db.Set<LogibooksRegister>().SingleAsync();
        saved.Phase = LogibooksPhase.Dispatching; saved.AttemptId = Guid.NewGuid(); saved.Deadline = Now.AddSeconds(-1);
        await db.SaveChangesAsync();
        var client = Client(CreatedReceipt());
        await new LogibooksRegisterJob(Registers(), client).Execute(null!, default);
        Assert.That(client.Calls, Is.Zero); Assert.That((await Registers().GetAsync(admitted.Id, Shift, default)).State, Is.EqualTo("Unknown"));
        db.ChangeTracker.Clear(); saved = await db.Set<LogibooksRegister>().SingleAsync();
        saved.Phase = LogibooksPhase.Queued; saved.Target = "https://different.invalid";
        await db.SaveChangesAsync(); await Registers().ProcessAsync(client, default); Assert.That(client.Calls, Is.Zero);
    }

    private sealed class AcceptanceFault : SaveChangesInterceptor
    {
        public bool Fail { get; set; } = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (Fail && data.Context!.ChangeTracker.Entries<OrderHistoryEvent>().Any(x => x.State == EntityState.Added && x.Entity.Kind == OrderHistoryKind.LogibooksRegistered))
                throw new DbUpdateException("Local audit write failed");
            return ValueTask.FromResult(result);
        }
    }

    [Test]
    public async Task LocalAuditFailureRetriesOnlyRetainedReceiptAndDoesNotLoseAcceptance()
    {
        var request = await RegisterCommand(); var admitted = await Registers().CreateAsync(request, actorId, Shift, default);
        var fault = new AcceptanceFault();
        await using var failingDb = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(reviewDatabaseName, reviewDatabaseRoot).AddInterceptors(fault).Options);
        var worker = new LogibooksRegisterService(failingDb, logibooksOptions, new Clock(), new(), NullLogger<LogibooksRegisterService>.Instance);
        var client = Client(CreatedReceipt());
        await Assert.ThrowsAsync<DbUpdateException>(() => worker.ProcessAsync(client, default));
        Assert.That((await Registers().GetAsync(admitted.Id, Shift, default)).Phase, Is.EqualTo("ReceiptRetained"));
        fault.Fail = false; logibooksOptions.CurrentValue.Enabled = false;
        await worker.ProcessAsync(client, default); await worker.ProcessAsync(client, default);
        Assert.That(client.Calls, Is.EqualTo(1));
        Assert.That((await Registers().GetAsync(admitted.Id, Shift, default)).State, Is.EqualTo("Succeeded"));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(x => x.Kind == OrderHistoryKind.LogibooksRegistered), Is.EqualTo(1));
    }

    [Test]
    public async Task SavedCourierFieldsAreUsedOnlyWhenTheyReproduceAuthoritativeDestination()
    {
        await RegisterCommand();
        var profile = CustomerProfileDto.From(order.Customer) with { LastName = "Снимок", Phone = "+79999999999", PostalCode = "123456", City = "Москва", Address = "Сохранённый адрес" };
        var checkout = new OrderCheckoutDto(profile, new("courier", "Курьер", "123456, Москва, Сохранённый адрес"));
        db.Entry(order).Property(x => x.CheckoutData).CurrentValue = LogibooksMapping.Serialize(checkout);
        var goods = LogibooksMapping.Goods(order);
        Assert.That(goods.City, Is.EqualTo("Москва")); Assert.That(goods.PostalCode, Is.EqualTo("123456")); Assert.That(goods.Address, Is.EqualTo("Сохранённый адрес"));
        Assert.That(goods.Phone, Is.EqualTo(order.Customer.Phone)); Assert.That(goods.LastName, Is.EqualTo("Снимок"));
        db.Entry(order).Property(x => x.CheckoutData).CurrentValue = LogibooksMapping.Serialize(checkout with { Delivery = checkout.Delivery with { Destination = "Другая сохранённая строка" } });
        goods = LogibooksMapping.Goods(order); Assert.That(goods.City, Is.Null); Assert.That(goods.Address, Is.EqualTo("Другая сохранённая строка"));
        db.Entry(order).Property(x => x.CheckoutData).CurrentValue = null;
        goods = LogibooksMapping.Goods(order); Assert.That(goods.Address, Is.Null);
    }

    [Test]
    public async Task LookupRejectsMissingOrderAndActorAndBalancedNumberSelectionHasNoCrossProduct()
    {
        var command = await RegisterCommand();
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(command, int.MaxValue, Shift, default)))!.Code, Is.EqualTo("backoffice_user_not_found"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().PreviewAsync(new(Guid.NewGuid(), null,
            [new("12345678-9999", Now)]), Shift, default)))!.StatusCode, Is.EqualTo(404));
        Assert.That(PublicOrderNumbers.Selection([]).Compile()(order), Is.False);
        var predicate = PublicOrderNumbers.Selection(["12345678-1", "87654321-2", "11111111-3"]).Compile();
        Assert.That(predicate(order), Is.True);
        Assert.That(PublicOrderNumbers.Selection(["12345678-2", "87654321-1"]).Compile()(order), Is.False);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task LateEvidenceBeforeLocalApplyCannotOverrideDefinitiveReceipt(bool contradiction)
    {
        var command = await RegisterCommand(); var admitted = await Registers().CreateAsync(command, actorId, Shift, default);
        await Registers().ProcessAsync(Client(new(null, false, "logibooks_unknown")), default);
        var unknown = await Registers().GetAsync(admitted.Id, Shift, default);
        await Registers().RetainAsync(admitted.Id, unknown.AttemptId!.Value, CreatedReceipt(), default);
        if (contradiction)
            await Registers().RetainAsync(admitted.Id, unknown.AttemptId.Value, CreatedReceipt(999, 20), default);
        else
            await Registers().RetainAsync(admitted.Id, unknown.AttemptId.Value, new(null, false, null), default);
        await Registers().ApplyAsync(admitted.Id, default);
        var current = await Registers().GetAsync(admitted.Id, Shift, default);
        Assert.That(current.InvestigationRequired, Is.EqualTo(contradiction));
        Assert.That(current.Created, Is.EqualTo(contradiction ? 0 : 1));
        if (!contradiction) Assert.That(current.Failure, Is.Null);
        Assert.That(await db.Set<LogibooksReceipt>().CountAsync(), Is.EqualTo(3));
    }

    [Test]
    public async Task AuthenticationRefreshMarkerCannotBeRecordedTwice()
    {
        var command = await RegisterCommand(); var admitted = await Registers().CreateAsync(command, actorId, Shift, default);
        var twice = new RegisterClient(async retry => { await retry(); await retry(); return CreatedReceipt(); });
        var conflict = await Assert.ThrowsAsync<ServiceException>(() => Registers().ProcessAsync(twice, default));
        Assert.That(conflict!.Code, Is.EqualTo("logibooks_update_conflict"));
        Assert.That((await Registers().GetAsync(admitted.Id, Shift, default)).Phase, Is.EqualTo("Dispatching"));
    }
}
