// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService
{
    private static bool CanMarkCustomsPaid(Order order, OrderPricingSnapshot? snapshot, string[] roles)
        => BackofficeAuthorization.IsAllowed(roles, BackofficeAction.MarkCustomsPaid)
            && Enum.IsDefined(order.Status) && !order.CustomsPaid && snapshot is not null
            && ReadCalculation(snapshot).Components.Any(component => component.Service == ServiceKind.CustomsPayments
                && component.State == PriceComponentState.Calculated && component.AmountRub > 0);

    public Task<BackofficeOrderDetailsDto> MarkCustomsPaidAsync(string number, MarkCustomsPaidRequest request,
        int actorId, string[] roles, CancellationToken token)
        => PricingRun(nameof(MarkCustomsPaidAsync),
            () => LogValueSummary.Inputs((nameof(number), number), (nameof(request), request), (nameof(actorId), actorId),
                (nameof(roles), roles), (nameof(token), token)), async () =>
        {
            BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.MarkCustomsPaid);
            var order = await FindPublicOrder(number, token);
            await using var transaction = await AppDatabaseOperations.For(database).BeginTransactionAsync(database, token);
            if (request.ExpectedUpdatedAt != order.UpdatedAt) throw new ServiceException(409, "order_update_conflict");
            if (!CanMarkCustomsPaid(order, await LatestPricingAsync(order.Id, token), roles))
                throw new ServiceException(409, "customs_payment_unavailable");
            var actor = await database.BackofficeUsers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == actorId, token)
                ?? throw new ServiceException(404, "backoffice_user_not_found");
            var name = string.Join(' ', new[] { actor.LastName, actor.FirstName, actor.Patronymic }.Where(value => !string.IsNullOrWhiteSpace(value)));
            order.MarkCustomsPaid(timeProvider.GetUtcNow());
            AddHistory(order, order.UpdatedAt, OrderHistoryKind.CustomsPaid, OrderHistoryArea.Customs,
                OrderHistoryActor.Staff, actorId, name, null, null,
                new(5, order.Status, order.Status, null, CustomsPaidBefore: false, CustomsPaidAfter: true));
            try
            {
                await database.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(token);
                database.ChangeTracker.Clear();
                throw new ServiceException(409, "order_update_conflict");
            }
            return await GetForBackofficeAsync(number, roles, token);
        }, token);
}
