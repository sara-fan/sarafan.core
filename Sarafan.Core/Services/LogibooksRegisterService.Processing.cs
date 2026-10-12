// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class LogibooksRegisterService
{
    public Task ProcessAsync(ILogibooksClient client, CancellationToken token)
        => Run(nameof(ProcessAsync), () => LogValueSummary.Inputs((nameof(client), client), (nameof(token), token)), async () =>
        {
            var pending = await database.Set<LogibooksRegister>().AsNoTracking()
                .Where(x => x.Phase == LogibooksPhase.ReceiptRetained && !x.InvestigationRequired)
                .OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(20).ToArrayAsync(token);
            foreach (var id in pending) await ApplyAsync(id, token);
            var claim = await ClaimAsync(token);
            if (claim is null) return false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.CurrentValue.DeadlineSeconds));
            var receipt = await client.CreateAsync(claim.Target, claim.Request,
                () => RecordAuthenticationRetryAsync(claim.Id, claim.AttemptId!.Value, deadline.Token), deadline.Token);
            await RetainAsync(claim.Id, claim.AttemptId!.Value, receipt, token);
            await ApplyAsync(claim.Id, token);
            return true;
        }, token);

    private async Task<LogibooksRegister?> ClaimAsync(CancellationToken token)
    {
        database.ChangeTracker.Clear();
        var operations = AppDatabaseOperations.For(database);
        await using var transaction = await operations.BeginTransactionAsync(database, token);
        await operations.LockLogibooksAsync(database, token);
        var now = clock.GetUtcNow();
        var expired = await database.Set<LogibooksRegister>().Where(x => x.Phase == LogibooksPhase.Dispatching
            && x.State == LogibooksState.Submitting && x.Deadline <= now).ToArrayAsync(token);
        foreach (var item in expired)
        {
            item.State = LogibooksState.Unknown;
            item.Failure = Failure("logibooks_unknown");
            item.Touch(now);
        }
        LogibooksRegister? claim = null;
        if (options.CurrentValue.Enabled)
        {
            var target = options.CurrentValue.Target;
            claim = await database.Set<LogibooksRegister>().Where(x => x.Phase == LogibooksPhase.Queued && x.Target == target
                && !x.InvestigationRequired && !x.Rows.Any(row => database.Set<LogibooksRegisterRow>()
                    .Any(hold => hold.OrderId == row.OrderId && hold.Register.InvestigationRequired)))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(token);
            if (claim is not null)
            {
                claim.AttemptId = Guid.NewGuid();
                claim.Phase = LogibooksPhase.Dispatching;
                claim.Deadline = now.AddSeconds(options.CurrentValue.DeadlineSeconds);
                claim.Touch(now);
            }
        }
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return claim;
    }

    private async Task<LogibooksRegister> LockedAsync(Guid id, CancellationToken token)
    {
        await AppDatabaseOperations.For(database).LockLogibooksAsync(database, token);
        database.ChangeTracker.Clear();
        return await database.Set<LogibooksRegister>().Include(x => x.Rows).SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ServiceException(404, "resource_not_found");
    }

    private async Task RecordAuthenticationRetryAsync(Guid id, Guid attemptId, CancellationToken token)
    {
        await using var transaction = await AppDatabaseOperations.For(database).BeginTransactionAsync(database, token);
        var operation = await LockedAsync(id, token);
        if (operation.AttemptId != attemptId || operation.AuthenticationRetried || operation.Phase != LogibooksPhase.Dispatching)
            throw new ServiceException(409, "logibooks_update_conflict");
        operation.AuthenticationRetried = true;
        operation.Touch(clock.GetUtcNow());
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    internal async Task RetainAsync(Guid id, Guid attemptId, LogibooksRemoteReceipt receipt, CancellationToken token)
    {
        await using var transaction = await AppDatabaseOperations.For(database).BeginTransactionAsync(database, token);
        var operation = await LockedAsync(id, token);
        if (operation.AttemptId != attemptId) throw new ServiceException(409, "logibooks_update_conflict");
        await RetainCoreAsync(operation, receipt, null, null, token);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    private async Task RetainCoreAsync(LogibooksRegister operation, LogibooksRemoteReceipt receipt, int? reconciler,
        string? reconcilerName, CancellationToken token)
    {
        var payload = LogibooksMapping.Serialize(receipt);
        var hash = LogibooksMapping.Hash(payload);
        if (await database.Set<LogibooksReceipt>().AnyAsync(x => x.RegisterId == operation.Id
            && x.AttemptId == operation.AttemptId && x.Hash == hash, token)) return;
        database.Add(new LogibooksReceipt
        {
            RegisterId = operation.Id,
            AttemptId = operation.AttemptId!.Value,
            At = clock.GetUtcNow(),
            Hash = hash,
            Payload = payload,
            ReconcilerId = reconciler,
            ReconcilerName = reconcilerName
        });
        if (!receipt.Definitive)
        {
            if (operation.Rows.Any(x => x.Outcome == LogibooksRowOutcome.Unresolved))
            {
                operation.State = LogibooksState.Unknown;
                operation.Failure = Failure("logibooks_unknown");
            }
            operation.Touch(clock.GetUtcNow());
            return;
        }
        var results = ReceiptRows(operation, receipt);
        var contradicts = receipt.Result?.RegisterId is int registerId && operation.RemoteRegisterId is int previousId && registerId != previousId
            || results.Any(result => operation.Rows.Any(row => row.RowNumber == result.RowNumber
                && row.Outcome != LogibooksRowOutcome.Unresolved
                && (row.Outcome == LogibooksRowOutcome.Created
                    ? result.Outcome != "Created" || row.ParcelId != result.ParcelId : result.Outcome == "Created")));
        // Compare durable evidence too: local application may not yet have resolved any rows.
        var retained = await database.Set<LogibooksReceipt>().AsNoTracking()
            .Where(x => x.RegisterId == operation.Id && x.AttemptId == operation.AttemptId)
            .Select(x => x.Payload).ToArrayAsync(token);
        contradicts |= retained.Select(LogibooksMapping.Read<LogibooksRemoteReceipt>).Where(x => x.Definitive).Any(previous =>
            receipt.Result?.RegisterId is int currentRegister && previous.Result?.RegisterId is int priorRegister && currentRegister != priorRegister
            || ReceiptRows(operation, previous).Any(prior => results.Any(current => current.RowNumber == prior.RowNumber
                && (current.Outcome == "Created" != (prior.Outcome == "Created")
                    || current.Outcome == "Created" && current.ParcelId != prior.ParcelId))));
        if (contradicts)
        {
            await AppDatabaseOperations.For(database).LockOrdersAsync(database, operation.Rows.Select(x => x.OrderId).Order().ToArray(), token);
            operation.InvestigationRequired = true;
            operation.Failure = Failure("logibooks_conflicting_evidence");
        }
        else if (!operation.InvestigationRequired && operation.Rows.Any(x => x.Outcome == LogibooksRowOutcome.Unresolved))
        {
            operation.Phase = LogibooksPhase.ReceiptRetained;
            operation.Failure = receipt.FailureCode is null ? null : Failure(receipt.FailureCode);
        }
        operation.Touch(clock.GetUtcNow());
    }

    private static LogibooksRemoteRow[] ReceiptRows(LogibooksRegister operation, LogibooksRemoteReceipt receipt)
        => receipt.Result?.Rows ?? operation.Rows.Select(x =>
            new LogibooksRemoteRow(x.RowNumber, x.OrderNumber, "NotCreated", null, null, null, [])).ToArray();

    internal async Task ApplyAsync(Guid id, CancellationToken token)
    {
        await using var transaction = await AppDatabaseOperations.For(database).BeginTransactionAsync(database, token);
        var operation = await LockedAsync(id, token);
        if (operation.Phase != LogibooksPhase.ReceiptRetained || operation.InvestigationRequired)
        {
            await transaction.CommitAsync(token);
            return;
        }
        var retained = await database.Set<LogibooksReceipt>().AsNoTracking().Where(x => x.RegisterId == id && x.AttemptId == operation.AttemptId)
            .OrderByDescending(x => x.Id).ToArrayAsync(token);
        // A later uncertain transport observation cannot displace definitive retained evidence.
        var saved = retained.First(x => LogibooksMapping.Read<LogibooksRemoteReceipt>(x.Payload).Definitive);
        var receipt = LogibooksMapping.Read<LogibooksRemoteReceipt>(saved.Payload);

        operation.Failure = receipt.FailureCode is null ? null : Failure(receipt.FailureCode);
        var ids = operation.Rows.Select(x => x.OrderId).Order().ToArray();
        await AppDatabaseOperations.For(database).LockOrdersAsync(database, ids, token);
        var orders = await database.Orders.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, token);
        var results = ReceiptRows(operation, receipt);
        if (receipt.Result?.RegisterId is int remoteId) operation.RemoteRegisterId = remoteId;
        foreach (var result in results)
        {
            var row = operation.Rows.Single(x => x.RowNumber == result.RowNumber);
            if (row.Outcome != LogibooksRowOutcome.Unresolved) continue;
            row.Outcome = result.Outcome == "Created" ? LogibooksRowOutcome.Created : LogibooksRowOutcome.Rejected;
            row.Blocking = row.Outcome == LogibooksRowOutcome.Created;
            row.ParcelId = result.ParcelId;
            row.StatusId = result.StatusId;
            row.CheckStatus = result.CheckStatus;
            row.Issues = LogibooksMapping.Serialize(result.Issues);
            if (row.Outcome != LogibooksRowOutcome.Created) continue;
            var order = orders[row.OrderId];
            order.RecordLogibooksRegistration(clock.GetUtcNow());
            row.AcceptanceHistoryEvent = OrderHistoryWriter.Create(order, order.UpdatedAt, OrderHistoryKind.LogibooksRegistered,
                OrderHistoryArea.Logibooks, OrderHistoryActor.Staff, operation.ActorId, operation.ActorName,
                new(6, order.Status, order.Status, null, Logibooks: new(operation.Id, operation.RemoteRegisterId!.Value,
                    row.ParcelId!.Value, saved.ReconcilerId, saved.ReconcilerName, saved.ReconcilerId is null ? null : saved.At,
                    saved.ReconcilerId is null ? "adapter" : "reconciliation")));
        }
        var accepted = operation.Rows.Count(x => x.Outcome == LogibooksRowOutcome.Created);
        operation.State = operation.Rows.Any(x => x.Outcome == LogibooksRowOutcome.Unresolved) ? LogibooksState.Unknown
            : accepted == operation.Rows.Count ? LogibooksState.Succeeded : accepted == 0 ? LogibooksState.Rejected : LogibooksState.PartiallySucceeded;
        operation.Phase = LogibooksPhase.Applied;
        operation.Touch(clock.GetUtcNow());
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }

    public Task<LogibooksOperationDto> ReconcileAsync(Guid id, LogibooksReconcileRequest request, int actorId,
        string[] roles, CancellationToken token)
        => Run(nameof(ReconcileAsync), () => LogValueSummary.Inputs((nameof(id), id), (nameof(request), request),
            (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)), async () =>
        {
            Authorize(roles);
            await using (var transaction = await AppDatabaseOperations.For(database).BeginTransactionAsync(database, token))
            {
                var operation = await LockedAsync(id, token);
                if (operation.Version != request.Version || operation.AttemptId != request.AttemptId || operation.State != LogibooksState.Unknown
                    || operation.InvestigationRequired || operation.Phase == LogibooksPhase.ReceiptRetained)
                    throw new ServiceException(409, "logibooks_update_conflict");
                if (request.Rows is null || request.Rows.Length == 0 || request.Rows.Length > operation.Rows.Count
                    || request.Rows.Any(x => x is null) || request.Rows.Select(x => x.RowNumber).Distinct().Count() != request.Rows.Length
                    || string.IsNullOrWhiteSpace(request.Note) || request.Note.Trim().Length > 2000 || !request.OriginalAttemptFinished)
                    throw Invalid("note", "Подтвердите завершение исходной попытки и укажите основание проверки (до 2000 символов).");
                var existingParcels = operation.Rows.Where(x => x.ParcelId.HasValue).Select(x => x.ParcelId!.Value).ToHashSet();
                foreach (var result in request.Rows)
                {
                    if (!operation.Rows.Any(x => x.RowNumber == result.RowNumber && x.Outcome == LogibooksRowOutcome.Unresolved)
                        || (result.Created ? result.ParcelId is not > 0 || !existingParcels.Add(result.ParcelId.Value) : result.ParcelId is not null))
                        throw Invalid("rows", "Проверьте номера неразрешённых строк и уникальные идентификаторы посылок.");
                }
                if (request.Rows.Any(x => x.Created) && (request.RegisterId is not > 0
                    || operation.RemoteRegisterId is not null && request.RegisterId != operation.RemoteRegisterId))
                    throw Invalid("registerId", "Укажите единый идентификатор созданного реестра.");
                var actor = await ActorAsync(actorId, token);
                database.Add(new LogibooksReconciliation
                {
                    RegisterId = id,
                    AttemptId = request.AttemptId,
                    ActorId = actorId,
                    ActorName = actor,
                    At = clock.GetUtcNow(),
                    Note = request.Note.Trim(),
                    Payload = LogibooksMapping.Serialize(request)
                });
                var results = request.Rows.Select(x => new LogibooksRemoteRow(x.RowNumber, null,
                    x.Created ? "Created" : "NotCreated", x.ParcelId, null, null, [])).ToArray();
                var receipt = new LogibooksRemoteReceipt(new(request.Rows.Any(x => x.Created) ? request.RegisterId : operation.RemoteRegisterId,
                    results.Length, results.Count(x => x.Outcome == "Created"), results.Count(x => x.Outcome != "Created"), results), true, null);
                await RetainCoreAsync(operation, receipt, actorId, actor, token);
                await database.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            await ApplyAsync(id, token);
            return await GetCoreAsync(id, token);
        }, token);
}
