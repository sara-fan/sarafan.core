// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService
{
    public Task<BackofficeOrderDetailsDto> AdvanceFulfilmentAsync(string number, AdvanceOrderRequest request,
        int actorId, string[] roles, CancellationToken token)
        => StaffOrderMutation(nameof(AdvanceFulfilmentAsync),
            () => LogValueSummary.Inputs((nameof(number), number), (nameof(request), request),
                (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)),
            number, request.ExpectedUpdatedAt, actorId, roles, request.Status switch
            {
                OrderStatus.Delivering => OrderHistoryKind.DeliveryStarted,
                OrderStatus.Received => OrderHistoryKind.OrderReceived,
                _ => OrderHistoryKind.Created
            }, token);

    private Task<bool> MainPaymentPaidAsync(long orderId, CancellationToken token)
        => database.Set<OrderHistoryEvent>().AsNoTracking()
            .AnyAsync(item => item.OrderId == orderId && item.Kind == OrderHistoryKind.OrderPaid, token);

    private async Task<IReadOnlyList<OrderStageDto>> CustomerStagesAsync(long orderId, CancellationToken token)
    {
        var events = await database.Set<OrderHistoryEvent>().AsNoTracking()
            .Where(item => item.OrderId == orderId && (item.Kind == OrderHistoryKind.OrderPaid
                || item.Kind == OrderHistoryKind.DeliveryStarted || item.Kind == OrderHistoryKind.OrderReceived))
            .OrderBy(item => item.At).ThenBy(item => item.Id)
            .Select(item => new { item.Kind, item.At }).ToArrayAsync(token);
        return events.Select(item => new OrderStageDto(item.Kind switch
        {
            OrderHistoryKind.OrderPaid => OrderStatus.Paid,
            OrderHistoryKind.DeliveryStarted => OrderStatus.Delivering,
            _ => OrderStatus.Received
        }, item.At)).ToArray();
    }
}
