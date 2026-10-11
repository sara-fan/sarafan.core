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
    private static decimal? MainPayment(Order order, OrderPricingSnapshot? snapshot)
        => order.CheckoutData is not null && snapshot?.ValidUntil is not null
            ? MainPaymentAmount.From(CustomerPricing(snapshot, snapshot.At, true)) : null;

    private static bool CanMarkOrderPaid(Order order, OrderPricingSnapshot? snapshot, string[] roles)
        => BackofficeAuthorization.IsAllowed(roles, BackofficeAction.MarkOrderPaid)
            && order.Status is OrderStatus.QuoteReady or OrderStatus.QuoteExpired
            && MainPayment(order, snapshot).HasValue;

    public Task<BackofficeOrderDetailsDto> MarkOrderPaidAsync(string number, MarkOrderPaidRequest request,
        int actorId, string[] roles, CancellationToken token)
        => StaffOrderMutation(nameof(MarkOrderPaidAsync),
            () => LogValueSummary.Inputs((nameof(number), number), (nameof(request), request),
                (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)),
            number, request.ExpectedUpdatedAt, actorId, roles, OrderHistoryKind.OrderPaid, token);

    private Task<BackofficeOrderDetailsDto> StaffOrderMutation(string operation, Func<string> inputs, string number,
        DateTimeOffset? expectedUpdatedAt, int actorId, string[] roles, OrderHistoryKind kind, CancellationToken token)
        => PricingRun(operation, inputs, async () =>
        {
            var mainPayment = kind == OrderHistoryKind.OrderPaid;
            var customsPayment = kind == OrderHistoryKind.CustomsPaid;
            var target = kind == OrderHistoryKind.DeliveryStarted ? OrderStatus.Delivering : OrderStatus.Received;
            BackofficeAuthorization.RequireAllowed(roles, mainPayment ? BackofficeAction.MarkOrderPaid
                : customsPayment ? BackofficeAction.MarkCustomsPaid : BackofficeAction.ManualQuotes);
            if (kind is not (OrderHistoryKind.OrderPaid or OrderHistoryKind.CustomsPaid or OrderHistoryKind.DeliveryStarted or OrderHistoryKind.OrderReceived))
                throw new ServiceException(409, "order_fulfilment_unavailable");
            var order = await FindPublicOrder(number, token);
            await using var transaction = await AppDatabaseOperations.For(database).BeginTransactionAsync(database, token);
            if (expectedUpdatedAt != order.UpdatedAt) throw new ServiceException(409, "order_update_conflict");
            var snapshot = await LatestPricingAsync(order.Id, token);
            if (!(mainPayment ? CanMarkOrderPaid(order, snapshot, roles)
                : customsPayment ? CanMarkCustomsPaid(order, snapshot, roles) : order.NextStage == target))
                throw new ServiceException(409, mainPayment ? "order_payment_unavailable"
                    : customsPayment ? "customs_payment_unavailable" : "order_fulfilment_unavailable");
            var actor = await database.BackofficeUsers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == actorId, token)
                ?? throw new ServiceException(404, "backoffice_user_not_found");
            var name = string.Join(' ', new[] { actor.LastName, actor.FirstName, actor.Patronymic }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var previous = order.Status;
            if (mainPayment) order.MarkPaid(timeProvider.GetUtcNow());
            else if (customsPayment) order.MarkCustomsPaid(timeProvider.GetUtcNow());
            else order.AdvanceFulfilment(target, timeProvider.GetUtcNow());
            AddHistory(order, order.UpdatedAt, kind,
                customsPayment ? OrderHistoryArea.Customs : OrderHistoryArea.Status, OrderHistoryActor.Staff,
                actorId, name, null, null, !customsPayment ? new(3, previous, order.Status, null)
                    : new(5, order.Status, order.Status, null, CustomsPaidBefore: false, CustomsPaidAfter: true));
            try
            {
                await database.SaveChangesAsync(token);
                var result = await GetForBackofficeAsync(number, roles, token);
                await transaction.CommitAsync(token);
                return result;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(token);
                database.ChangeTracker.Clear();
                throw new ServiceException(409, "order_update_conflict");
            }
        }, token);
}
