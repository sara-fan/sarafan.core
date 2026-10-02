// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService
{
    public Task<BackofficeOrderDetailsDto> RejectReviewAsync(string number, RejectOrderReviewRequest request,
        int actorId, string[] roles, CancellationToken token)
        => PricingRun(nameof(RejectReviewAsync), () => "number/request/actor=[redacted]", async () =>
        {
            BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManageOrderPricing);
            await using var transaction = await AppDatabaseOperations.For(database).BeginTransactionAsync(database, token);
            var order = await FindPublicOrder(number, token);
            if (order.Status != OrderStatus.UnderReview) throw new ServiceException(409, "order_not_editable");
            if (request.ExpectedUpdatedAt != order.UpdatedAt) throw new ServiceException(409, "order_update_conflict");
            var reason = request.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
                throw new ServiceException(400, "invalid_review_reason");
            var actor = await database.BackofficeUsers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == actorId, token)
                ?? throw new ServiceException(404, "backoffice_user_not_found");
            var name = string.Join(' ', new[] { actor.LastName, actor.FirstName, actor.Patronymic }.Where(value => !string.IsNullOrWhiteSpace(value)));
            order.RejectReview(timeProvider.GetUtcNow());
            AddHistory(order, order.UpdatedAt, OrderHistoryKind.ReviewRejected, OrderHistoryArea.Status,
                OrderHistoryActor.Staff, actorId, name, null, null, new(3, OrderStatus.UnderReview, OrderStatus.CannotDeliver, null, null, reason));
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

    private async Task<(DateTimeOffset? At, string? Reason)> ReviewResultAsync(long orderId, CancellationToken token)
    {
        var result = await database.Set<OrderHistoryEvent>().AsNoTracking()
            .Where(row => row.OrderId == orderId && (row.Kind == OrderHistoryKind.QuoteConfirmed || row.Kind == OrderHistoryKind.ReviewRejected))
            .OrderByDescending(row => row.Id).Select(row => new { row.At, row.Payload }).FirstOrDefaultAsync(token);
        return result is null ? (null, null) : (result.At, JsonSerializer.Deserialize<OrderHistoryEvidence>(result.Payload, PricingJson)?.ReviewReason);
    }

    private OrderDeliveryEstimateDto? DeliveryEstimate(OrderStatus status)
        => status is OrderStatus.QuoteReady or OrderStatus.QuoteExpired
            ? new(reviewOptions?.Value.DeliveryMinimumDays ?? 14, reviewOptions?.Value.DeliveryMaximumDays ?? 21) : null;

    public Task ExpireQuotesAsync(CancellationToken token)
        => PricingRun(nameof(ExpireQuotesAsync), () => "token=[redacted]", async () =>
        {
            await ExpireQuotesCoreAsync(null, token);
            return true;
        }, token);

    private async Task ExpireQuotesCoreAsync(int? customerId, CancellationToken token, long? orderId = null)
    {
        var now = timeProvider.GetUtcNow();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var expired = await (from order in database.Orders
                                 where order.Status == OrderStatus.QuoteReady && (customerId == null || order.CustomerId == customerId)
                                     && (orderId == null || order.Id == orderId)
                                 from snapshot in database.OrderPricingSnapshots
                                 where snapshot.OrderId == order.Id && snapshot.ValidUntil != null && snapshot.ValidUntil <= now
                                     && snapshot.Id == database.OrderPricingSnapshots.Where(other => other.OrderId == order.Id).Max(other => other.Id)
                                 select new { Order = order, Deadline = snapshot.ValidUntil }).Take(500).ToArrayAsync(token);
            if (expired.Length == 0) return;
            foreach (var item in expired)
            {
                item.Order.ExpireQuote(now);
                AddHistory(item.Order, item.Deadline!.Value, OrderHistoryKind.QuoteExpired, OrderHistoryArea.Status,
                    OrderHistoryActor.System, null, "Система", null, null, new(3, OrderStatus.QuoteReady, OrderStatus.QuoteExpired, null));
            }
            try
            {
                await database.SaveChangesAsync(token);
                if (expired.Length < 500) return;
                attempt--;
            }
            catch (DbUpdateConcurrencyException)
            {
                database.ChangeTracker.Clear();
            }
        }
        throw new ServiceException(409, "order_update_conflict");
    }
}
