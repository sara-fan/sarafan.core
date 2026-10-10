// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class PaymentInformationService
{
    public Task<PaymentBundleDto> CreateAsync(PaymentInformationWriteRequest request, int actorId, string[] roles, CancellationToken token)
        => Mutate(nameof(CreateAsync),
            () => LogValueSummary.Inputs((nameof(request), request), (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)),
            actorId, roles, async now =>
        {
            var fields = PaymentInformationRules.Prepare(request);
            var row = new PaymentInformationBundle(fields, actorId, now, await ReadQr(request.Qr, token));
            database.Add(row);
            return row;
        }, token);

    public Task<PaymentBundleDto> UpdateAsync(long id, PaymentInformationWriteRequest request, int actorId, string[] roles, CancellationToken token)
        => Mutate(nameof(UpdateAsync),
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(request), request), (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)),
            actorId, roles, async now =>
        {
            var row = await Find(id, request.Version, token);
            if (row.Published) throw new ServiceException(409, "payment_bundle_frozen");
            row.Update(PaymentInformationRules.Prepare(request), actorId, now, await ReadQr(request.Qr, token));
            return row;
        }, token);

    public Task<PaymentBundleDto> CopyAsync(long id, Guid? version, int actorId, string[] roles, CancellationToken token)
        => Mutate(nameof(CopyAsync),
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(version), version), (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)),
            actorId, roles, async now =>
        {
            var original = await Find(id, version, token);
            if (!original.Published) throw new ServiceException(409, "payment_bundle_copy_unavailable");
            var row = original.Copy(actorId, now);
            database.Add(row);
            return row;
        }, token);

    public Task<PaymentBundleDto> EnableAsync(long id, EnablePaymentBundleRequest request, int actorId, string[] roles, CancellationToken token)
        => Mutate(nameof(EnableAsync),
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(request), request), (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)),
            actorId, roles, async now =>
        {
            var row = await Find(id, request.Version, token);
            var active = await database.PaymentInformationBundles.SingleOrDefaultAsync(item => item.Enabled, token);
            if (request.ExpectedEnabled != (active is null ? null : new EnabledPaymentBundle(active.Id, active.Version)))
                throw new ServiceException(409, "payment_bundle_update_conflict");
            PaymentInformationRules.RequireValid(row.Fields(), true, row.QrContent is not null);
            if (active?.Id == row.Id) return row;
            if (active is not null)
            {
                active.SetEnabled(false, actorId, now);
                // Release the filtered unique key first; both writes share the same transaction.
                await database.SaveChangesAsync(token);
            }
            row.SetEnabled(true, actorId, now);
            return row;
        }, token);

    public Task<PaymentBundleDto> DisableAsync(long id, Guid? version, int actorId, string[] roles, CancellationToken token)
        => Mutate(nameof(DisableAsync),
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(version), version), (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)),
            actorId, roles, async now =>
        {
            var row = await Find(id, version, token);
            if (!row.Enabled) throw new ServiceException(409, "payment_bundle_update_conflict");
            row.SetEnabled(false, actorId, now);
            return row;
        }, token);

    public async Task DeleteAsync(long id, Guid? version, int actorId, string[] roles, CancellationToken token)
        => await Mutate(nameof(DeleteAsync),
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(version), version), (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)),
            actorId, roles, async _ =>
        {
            var row = await Find(id, version, token);
            if (row.Enabled) throw new ServiceException(409, "payment_bundle_enabled");
            database.Remove(row);
            return row;
        }, token, deleting: true);

    private Task<PaymentBundleDto> Mutate(string operation, Func<string> inputs, int actorId, string[] roles,
        Func<DateTimeOffset, Task<PaymentInformationBundle>> change, CancellationToken token, bool deleting = false)
        => Run(operation, inputs, async () =>
        {
            RequireManage(roles);
            var operations = AppDatabaseOperations.For(database);
            await using var transaction = await operations.BeginTransactionAsync(database, token);
            await operations.LockPaymentInformationMutationsAsync(database, token);
            if (!await database.BackofficeUsers.AnyAsync(row => row.Id == actorId, token))
                throw new ServiceException(404, "backoffice_user_not_found");
            try
            {
                var row = await change(clock.GetUtcNow());
                await database.SaveChangesAsync(token);
                var result = deleting ? null! : await GetAsync(row.Id, roles, token);
                await transaction.CommitAsync(token);
                return result;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(token);
                database.ChangeTracker.Clear();
                throw new ServiceException(409, "payment_bundle_update_conflict");
            }
        }, token);

    private async Task<PaymentInformationBundle> Find(long id, Guid? version, CancellationToken token)
    {
        var expected = PaymentInformationRules.RequireVersion(version);
        var row = await database.PaymentInformationBundles.SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new ServiceException(404, "resource_not_found");
        if (row.Version != expected) throw new ServiceException(409, "payment_bundle_update_conflict");
        return row;
    }

    private static Task<(string ContentType, byte[] Content)?> ReadQr(IFormFile? qr, CancellationToken token)
        => ImageUpload.ReadAsync(qr, ImageUpload.MaxBytes, false, PaymentInformationRules.InvalidQr, token);
}
