// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

internal sealed class LogibooksTestOptions(LogibooksOptions value) : IOptionsMonitor<LogibooksOptions>
{
    public LogibooksOptions CurrentValue { get; set; } = value;
    public LogibooksOptions Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<LogibooksOptions, string?> listener) => null;
}

public sealed partial class OrderPricingTests
{
    private readonly LogibooksTestOptions logibooksOptions = new(new()
    { Enabled = true, BaseUrl = "https://adapter.test.invalid", Identifier = "Sarafan", Password = "private" });
    private LogibooksRegisterService Registers(TimeProvider? clock = null)
        => new(db, logibooksOptions, clock ?? new Clock(), new(), NullLogger<LogibooksRegisterService>.Instance);

    private async Task<LogibooksCreateRequest> RegisterCommand()
    {
        logibooksOptions.CurrentValue = new() { Enabled = true, BaseUrl = "https://adapter.test.invalid" };
        await PaymentCheckout();
        await service.MarkOrderPaidAsync("12345678-1", new(order.UpdatedAt), actorId, Shift, default);
        db.ChangeTracker.Clear();
        order = await db.Orders.Include(x => x.Customer).SingleAsync();
        var preview = await Registers().PreviewAsync(new(Guid.NewGuid(), null, [new("12345678-1", order.UpdatedAt)]), Shift, default);
        return new(preview.OperationId, preview.FileName, [new("12345678-1", order.UpdatedAt)], preview.MappingVersion, preview.Fingerprint);
    }

    private sealed class RegisterClient(Func<Func<Task>, Task<LogibooksRemoteReceipt>> action) : ILogibooksClient
    {
        public int Calls { get; private set; }
        public Task<LogibooksRemoteReceipt> CreateAsync(string target, string payload, Func<Task> authenticationRetry, CancellationToken token)
        { Calls++; return action(authenticationRetry); }
    }
    private static LogibooksRemoteReceipt CreatedReceipt(int parcel = 101, int register = 10)
        => new(new(register, 1, 1, 0, [new(1, "12345678-1", "Created", parcel, 7, 16, [])]), true, null);
    private static RegisterClient Client(LogibooksRemoteReceipt receipt) => new(_ => Task.FromResult(receipt));

    [Test]
    public async Task RegisterAdmissionFreezesDemoMappingAndReplaysBeforeEligibilityAndDisablement()
    {
        var request = await RegisterCommand();
        var result = await Registers().CreateAsync(request, actorId, Shift, default);
        Assert.That(result.State, Is.EqualTo("Submitting")); Assert.That(result.Phase, Is.EqualTo("Queued"));
        var saved = await db.Set<LogibooksRegister>().AsNoTracking().SingleAsync();
        var payload = LogibooksMapping.Read<LogibooksPayload>(saved.Request);
        Assert.Multiple(() =>
        {
            Assert.That(payload.Register, Is.EqualTo(new LogibooksMetadata(request.FileName, 2)));
            Assert.That(payload.Parcels[0].UnitPrice, Is.EqualTo(50));
            Assert.That(payload.Parcels[0].Currency, Is.EqualTo("USD"));
            Assert.That(payload.Parcels[0].Quantity, Is.EqualTo(2));
            Assert.That(payload.Parcels[0].CountryCode, Is.EqualTo(643));
            Assert.That(payload.Parcels[0].SenderCountryCode, Is.EqualTo(643));
            Assert.That(payload.Parcels[0].TradingCountryCode, Is.EqualTo(643));
            Assert.That(payload.Parcels[0].Address, Is.EqualTo("Адрес"));
            Assert.That(payload.Parcels[0].City, Is.Null);
            Assert.That(payload.Parcels[0].Phone, Is.EqualTo("+79990001234"));
            Assert.That(saved.Request, Does.Not.Contain("private"));
            Assert.That(request.FileName, Does.Match("^Sarafan-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$"));
        });
        logibooksOptions.CurrentValue.Enabled = false;
        var replay = await Registers().CreateAsync(request, actorId, Shift, default);
        Assert.That(replay.Id, Is.EqualTo(result.Id));
        var equivalent = request with { Orders = request.Orders.Select(x => x with { ExpectedUpdatedAt = x.ExpectedUpdatedAt.ToOffset(TimeSpan.FromHours(3)) }).ToArray() };
        Assert.That((await Registers().CreateAsync(equivalent, actorId, Shift, default)).Id, Is.EqualTo(result.Id));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(request with { FileName = "changed" }, actorId, Shift, default)))!.Code,
            Is.EqualTo("logibooks_update_conflict"));
        Assert.That(await db.Set<LogibooksRegister>().CountAsync(), Is.EqualTo(1));
        Assert.That((await Registers().OperationsAsync(Shift, default)).Enabled, Is.False);
        Assert.That((await Registers().GetAsync(result.Id, Shift, default)).Id, Is.EqualTo(result.Id));
    }

    [Test]
    public async Task RegisterAdmissionRejectsStalePreviewOverlapAndChangedFingerprint()
    {
        var request = await RegisterCommand();
        foreach (var changed in new[] {
            request with { MappingVersion = 2 }, request with { Fingerprint = "changed" },
            request with { Orders = [new("12345678-1", Now.AddDays(-1))] } })
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(changed, actorId, Shift, default)))!.Code, Is.EqualTo("logibooks_preview_changed"));
        await Registers().CreateAsync(request, actorId, Shift, default);
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(request with { OperationId = Guid.NewGuid() }, actorId, Shift, default)))!.Code, Is.EqualTo("logibooks_preview_changed"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().PreviewAsync(new(Guid.NewGuid(), null, request.Orders), Shift, default)))!.Code, Is.EqualTo("logibooks_preview_changed"));
    }

    [Test]
    public async Task RegisterValidationBoundsAndAuthorizationAreAppliedAtServiceBoundary()
    {
        var request = await RegisterCommand();
        var invalid = new[] {
            request with { OperationId = Guid.Empty }, request with { FileName = " " }, request with { FileName = new string('a', 201) },
            request with { FileName = "unsafe\nlabel" }, request with { Orders = [] }, request with { Orders = null! },
            request with { Orders = [null!] }, request with { Orders = [new("12345678-1", default)] },
            request with { Orders = [new("", Now)] }, request with { Orders = [request.Orders[0], request.Orders[0]] },
            request with { Orders = Enumerable.Repeat(request.Orders[0], 1001).ToArray() } };
        foreach (var item in invalid)
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(item, actorId, Shift, default)))!.Code, Is.EqualTo("invalid_logibooks_register"));
        foreach (var role in new[] { BackofficeRoles.Operator, BackofficeRoles.SeniorOperator, "unknown" })
        {
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().OperationsAsync([role], default)))!.StatusCode, Is.EqualTo(403));
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().PreviewAsync(new(request.OperationId, null, request.Orders), [role], default)))!.StatusCode, Is.EqualTo(403));
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(request, actorId, [role], default)))!.StatusCode, Is.EqualTo(403));
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().GetAsync(request.OperationId, [role], default)))!.StatusCode, Is.EqualTo(403));
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().RowsAsync(request.OperationId, 1, 25, null, [role], default)))!.StatusCode, Is.EqualTo(403));
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().ListAsync(1, 25, null, null, "state", "asc", [role], default)))!.StatusCode, Is.EqualTo(403));
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().ReconcileAsync(request.OperationId, null!, actorId, [role], default)))!.StatusCode, Is.EqualTo(403));
        }
        logibooksOptions.CurrentValue.OutboundBytes = 1;
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(request, actorId, Shift, default)))!.StatusCode, Is.EqualTo(413));
        logibooksOptions.CurrentValue.Enabled = false;
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().CreateAsync(request, actorId, Shift, default)))!.Code, Is.EqualTo("logibooks_disabled"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().PreviewAsync(new(request.OperationId, null, request.Orders), Shift, default)))!.Code, Is.EqualTo("logibooks_disabled"));
    }

    [Test]
    public async Task RegisterSuccessAppliesOnceWithUnchangedOrderStatusAndVersionSixHistory()
    {
        var request = await RegisterCommand();
        var original = order.UpdatedAt;
        var admitted = await Registers().CreateAsync(request, actorId, Shift, default);
        var client = Client(CreatedReceipt());
        await Registers().ProcessAsync(client, default);
        await Registers().ProcessAsync(client, default);
        await Registers().ApplyAsync(admitted.Id, default);
        Assert.That(client.Calls, Is.EqualTo(1));
        var result = await Registers().GetAsync(admitted.Id, Shift, default);
        Assert.That(result.State, Is.EqualTo("Succeeded")); Assert.That(result.Created, Is.EqualTo(1));
        var row = (await Registers().RowsAsync(admitted.Id, 1, 25, null, Shift, default)).Items.Single();
        Assert.That(row.ParcelId, Is.EqualTo(101)); Assert.That(row.CheckStatus, Is.EqualTo(16));
        var current = await db.Orders.AsNoTracking().SingleAsync();
        Assert.That(current.Status, Is.EqualTo(OrderStatus.Paid)); Assert.That(current.UpdatedAt, Is.GreaterThan(original));
        var history = await db.Set<OrderHistoryEvent>().SingleAsync(x => x.Kind == OrderHistoryKind.LogibooksRegistered);
        Assert.That(history.ActorId, Is.EqualTo(actorId)); Assert.That(history.Areas, Is.EqualTo(OrderHistoryArea.Logibooks));
        var evidence = await service.HistoryDetailAsync("12345678-1", "0-" + history.Id, Shift, default);
        Assert.That(evidence.Version, Is.EqualTo(6)); Assert.That(evidence.Logibooks!.OperationId, Is.EqualTo(admitted.Id));
        Assert.That(evidence.StatusBefore, Is.EqualTo(OrderStatus.Paid)); Assert.That(evidence.StatusAfter, Is.EqualTo(OrderStatus.Paid));
        Assert.That(await db.Set<LogibooksReceipt>().CountAsync(), Is.EqualTo(1));
        await Registers().RetainAsync(result.Id, result.AttemptId!.Value, CreatedReceipt(), default);
        Assert.That(await db.Set<LogibooksReceipt>().CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task UnknownRetainsReservationAndReconciliationRequiresExternalProofAndCurrentVersion()
    {
        var request = await RegisterCommand();
        var admitted = await Registers().CreateAsync(request, actorId, Shift, default);
        var client = Client(new(null, false, "logibooks_unknown"));
        await Registers().ProcessAsync(client, default);
        await Registers().ProcessAsync(client, default);
        var unknown = await Registers().GetAsync(admitted.Id, Shift, default);
        Assert.That(client.Calls, Is.EqualTo(1)); Assert.That(unknown.State, Is.EqualTo("Unknown"));
        Assert.That(unknown.Failure!.Code, Is.EqualTo("logibooks_unknown"));
        var reconcile = new LogibooksReconcileRequest(unknown.Version, unknown.AttemptId!.Value, null, true, "Оператор проверил завершённую попытку", [new(1, false, null)]);
        foreach (var invalid in new[] { reconcile with { Note = " " }, reconcile with { Note = new string('x', 2001) },
            reconcile with { OriginalAttemptFinished = false }, reconcile with { Rows = [] }, reconcile with { Rows = null! },
            reconcile with { Rows = [null!] }, reconcile with { Rows = [new(2, false, null)] },
            reconcile with { Rows = [new(1, false, 1)] }, reconcile with { Rows = [new(1, true, null)] },
            reconcile with { Rows = [new(1, true, 101)] } })
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().ReconcileAsync(admitted.Id, invalid, actorId, Shift, default)))!.StatusCode, Is.EqualTo(400));
        foreach (var stale in new[] { reconcile with { Version = Guid.NewGuid() }, reconcile with { AttemptId = Guid.NewGuid() } })
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().ReconcileAsync(admitted.Id, stale, actorId, Shift, default)))!.StatusCode, Is.EqualTo(409));
        logibooksOptions.CurrentValue.Enabled = false;
        var rejected = await Registers().ReconcileAsync(admitted.Id, reconcile, actorId, Shift, default);
        Assert.That(rejected.State, Is.EqualTo("Rejected"));
        Assert.That((await db.Set<LogibooksRegisterRow>().SingleAsync()).Blocking, Is.False);
        Assert.That(await db.Set<LogibooksReconciliation>().CountAsync(), Is.EqualTo(1));
        Assert.That(await db.Set<OrderHistoryEvent>().CountAsync(x => x.Kind == OrderHistoryKind.LogibooksRegistered), Is.Zero);
        // A contradictory late original response is retained and places an investigation hold.
        await Registers().RetainAsync(admitted.Id, unknown.AttemptId!.Value, CreatedReceipt(), default);
        var conflict = await Registers().GetAsync(admitted.Id, Shift, default);
        Assert.That(conflict.InvestigationRequired, Is.True);
        Assert.That(conflict.Failure!.Code, Is.EqualTo("logibooks_conflicting_evidence"));
        await Registers().ApplyAsync(admitted.Id, default);
        logibooksOptions.CurrentValue.Enabled = true;
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().PreviewAsync(new(Guid.NewGuid(), null, request.Orders), Shift, default)))!.StatusCode, Is.EqualTo(409));
    }

    [Test]
    public async Task RetainedReceiptCanApplyAfterRestartWhileDisabledWithoutAnotherRemoteCall()
    {
        var request = await RegisterCommand();
        var admitted = await Registers().CreateAsync(request, actorId, Shift, default);
        var client = new RegisterClient(async retry => { await retry(); return new(null, false, "logibooks_unknown"); });
        await Registers().ProcessAsync(client, default);
        var unknown = await Registers().GetAsync(admitted.Id, Shift, default);
        Assert.That((await db.Set<LogibooksRegister>().AsNoTracking().SingleAsync()).AuthenticationRetried, Is.True);
        await Registers().RetainAsync(admitted.Id, unknown.AttemptId!.Value, CreatedReceipt(), default);
        Assert.That((await Registers().GetAsync(admitted.Id, Shift, default)).Phase, Is.EqualTo("ReceiptRetained"));
        logibooksOptions.CurrentValue.Enabled = false;
        var never = Client(CreatedReceipt(999));
        await Registers().ProcessAsync(never, default);
        Assert.That(never.Calls, Is.Zero);
        Assert.That((await Registers().GetAsync(admitted.Id, Shift, default)).State, Is.EqualTo("Succeeded"));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().RetainAsync(admitted.Id, Guid.NewGuid(), CreatedReceipt(), default)))!.StatusCode, Is.EqualTo(409));
        await Registers().RetainAsync(admitted.Id, unknown.AttemptId.Value, CreatedReceipt(999), default);
        Assert.That((await Registers().GetAsync(admitted.Id, Shift, default)).InvestigationRequired, Is.True);
    }

    [Test]
    public async Task CreatedReconciliationRecordsOriginalActorAndReconciler()
    {
        var request = await RegisterCommand();
        var admitted = await Registers().CreateAsync(request, actorId, Shift, default);
        await Registers().ProcessAsync(Client(new(null, false, "logibooks_unknown")), default);
        var unknown = await Registers().GetAsync(admitted.Id, Shift, default);
        var result = await Registers().ReconcileAsync(admitted.Id, new(unknown.Version, unknown.AttemptId!.Value, 10, true,
            "Проверено в Logibooks", [new(1, true, 101)]), actorId, Shift, default);
        Assert.That(result.State, Is.EqualTo("Succeeded"));
        var history = await db.Set<OrderHistoryEvent>().SingleAsync(x => x.Kind == OrderHistoryKind.LogibooksRegistered);
        var evidence = await service.HistoryDetailAsync("12345678-1", "0-" + history.Id, Shift, default);
        Assert.That(evidence.Logibooks!.ReconcilerId, Is.EqualTo(actorId));
        Assert.That(evidence.Logibooks.Source, Is.EqualTo("reconciliation"));
    }

    [TestCase("logibooks_unknown")]
    [TestCase("logibooks_auth_failed")]
    [TestCase("logibooks_rejected")]
    public async Task RetainedFailureReadsUseCurrentWordingWithoutRewritingEvidence(string code)
    {
        var request = await RegisterCommand();
        var admitted = await Registers().CreateAsync(request, actorId, Shift, default);
        var expected = new SarafanProblemDetailsFactory().Create(new DefaultHttpContext(), 502, code);
        var retained = new SarafanProblemDetailsFactory().Create(new DefaultHttpContext(), 502, code,
            new Dictionary<string, string[]> { ["orders"] = ["Retained field evidence"] });
        retained.Title = "Ошибка Logibooks";
        retained.Detail = "Проверьте результат в Logibooks.";
        retained.Extensions["source"] = "retained";
        var evidence = LogibooksMapping.Serialize(retained);
        var stored = await db.Set<LogibooksRegister>().SingleAsync(x => x.Id == admitted.Id);
        stored.Failure = evidence;
        await db.SaveChangesAsync();
        var detail = await Registers().GetAsync(admitted.Id, Shift, default);
        var page = await Registers().ListAsync(1, 25, null, null, "createdAt", "asc", Shift, default);
        foreach (var value in new[] { detail, page.Items.Single() })
        {
            Assert.That(value.Failure!.Title, Is.EqualTo(expected.Title).And.Not.Contain("Logibooks"));
            Assert.That(value.Failure.Detail, Is.EqualTo(expected.Detail).And.Not.Contain("Logibooks"));
            Assert.That(value.Failure.Type, Is.EqualTo(retained.Type));
            Assert.That(value.Failure.Status, Is.EqualTo(retained.Status));
            Assert.That(value.Failure.Code, Is.EqualTo(retained.Code));
            Assert.That(value.Failure.Instance, Is.EqualTo(retained.Instance));
            Assert.That(value.Failure.TraceId, Is.EqualTo(retained.TraceId));
            Assert.That(value.Failure.Errors!["orders"], Is.EqualTo(retained.Errors!["orders"]));
            Assert.That(value.Failure.Extensions["source"]?.ToString(), Is.EqualTo("retained"));
        }
        Assert.That(await db.Set<LogibooksRegister>().AsNoTracking().Where(x => x.Id == admitted.Id)
            .Select(x => x.Failure).SingleAsync(), Is.EqualTo(evidence));
    }

    [TestCase("internal_error", 500, "https://sarafan.sw.consulting/problems/internal-error")]
    [TestCase("logibooks_future", 502, "https://sarafan.sw.consulting/problems/logibooks-future")]
    [TestCase("logibooks_unknown", 500, "https://sarafan.sw.consulting/problems/logibooks-unknown")]
    [TestCase("logibooks_unknown", 502, "https://other.invalid/logibooks-unknown")]
    public void RetainedFailureReadsPreserveUnrecognizedOrMismatchedEvidence(string code, int status, string type)
    {
        var retained = new SarafanProblemDetails
        { Code = code, Status = status, Type = type, Title = "Retained title", Detail = "Retained detail", Instance = "urn:retained" };
        var result = SarafanProblemDetailsFactory.ReadRetainedLogibooksFailure(LogibooksMapping.Serialize(retained));
        Assert.That(result.Title, Is.EqualTo(retained.Title));
        Assert.That(result.Detail, Is.EqualTo(retained.Detail));
        Assert.That(result.Instance, Is.EqualTo(retained.Instance));
    }

    [Test]
    public async Task DefinitiveRejectionReleasesOrdersAndListSearchUsesDisplayedFields()
    {
        var request = await RegisterCommand();
        var admitted = await Registers().CreateAsync(request, actorId, Shift, default);
        await Registers().ProcessAsync(Client(new(null, true, "logibooks_auth_failed")), default);
        var rejected = await Registers().GetAsync(admitted.Id, Shift, default);
        Assert.That(rejected.State, Is.EqualTo("Rejected"));
        Assert.That((await Registers().ListAsync(1, 25, "отклон", "Rejected", "state", "asc", Shift, default)).Pagination.TotalCount, Is.EqualTo(1));
        foreach (var term in new[] { "12345678", "Товар", "50,00" + Currency.Usd.GetSymbol(), Currency.Usd.GetSymbol(), "2", "1,000", "Отклонена", "—" })
            Assert.That((await Registers().RowsAsync(admitted.Id, 1, 10, term, Shift, default)).Pagination.TotalCount, Is.EqualTo(1), term);
        foreach (var term in new[] { "%", "_", "not-found", "private", "USD", "50,00 USD", "50,00 " + Currency.Usd.GetSymbol() })
            Assert.That((await Registers().RowsAsync(admitted.Id, 1, 10, term, Shift, default)).Pagination.TotalCount, Is.Zero, term);
        var preview = await Registers().PreviewAsync(new(Guid.NewGuid(), null, request.Orders), Shift, default);
        Assert.That(preview.Parcels, Has.Length.EqualTo(1));
        foreach (var sort in new[] { "createdAt", "fileName", "state" })
            foreach (var direction in new[] { "asc", "desc" })
                Assert.That((await Registers().ListAsync(1, 10, "1", null, sort, direction, Shift, default)).Items, Has.Length.EqualTo(1));
        Assert.That((await Registers().ListAsync(int.MaxValue, 100, null, null, "fileName", "asc", Shift, default)).Items, Is.Empty);
        Assert.That((await Registers().RowsAsync(admitted.Id, int.MaxValue, 100, null, Shift, default)).Items, Is.Empty);
        foreach (var bad in new[] { (0, 25, "", "", "state", "asc"), (1, 11, "", "", "state", "asc"), (1, 25, new string('x', 201), "", "state", "asc"),
            (1, 25, "", "invalid", "state", "asc"), (1, 25, "", "", "invalid", "asc"), (1, 25, "", "", "state", "invalid") })
            Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().ListAsync(bad.Item1, bad.Item2, bad.Item3, bad.Item4, bad.Item5, bad.Item6, Shift, default)))!.StatusCode, Is.EqualTo(400));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().RowsAsync(admitted.Id, 1, 25, new string('x', 201), Shift, default)))!.StatusCode, Is.EqualTo(400));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().RowsAsync(Guid.NewGuid(), 1, 25, null, Shift, default)))!.StatusCode, Is.EqualTo(404));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(() => Registers().GetAsync(Guid.NewGuid(), Shift, default)))!.StatusCode, Is.EqualTo(404));
    }
}
