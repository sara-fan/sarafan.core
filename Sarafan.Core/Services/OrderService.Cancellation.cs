// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService
{
    public Task<OrderDto> CancelAsync(int customerId, string orderNumber, CancelOrderRequest request,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(OrderService).FullName}.{nameof(CancelAsync)}",
            () => LogValueSummary.Inputs((nameof(customerId), customerId), (nameof(orderNumber), orderNumber),
                (nameof(cancellationToken), cancellationToken)),
            () => CancelCoreAsync(customerId, orderNumber, request, cancellationToken), cancellationToken);

    private async Task<OrderDto> CancelCoreAsync(int customerId, string orderNumber, CancelOrderRequest request,
        CancellationToken token)
    {
        var (code, sequence) = ParsePublicOrderNumber(orderNumber);
        var operations = AppDatabaseOperations.For(database);
        await using var transaction = await operations.BeginTransactionAsync(database, token);
        var order = await database.Orders.Include(item => item.Customer).SingleOrDefaultAsync(item =>
            item.CustomerId == customerId && item.Customer.OrderCode == code && item.CustomerOrderNumber == sequence, token)
            ?? throw new ServiceException(404, "resource_not_found");

        if (order.Status == OrderStatus.Cancelled)
            return await GetCoreAsync(customerId, orderNumber, token);
        if (!order.Status.CanCustomerCancel()) throw new ServiceException(409, "order_not_cancellable");
        if (request.ExpectedUpdatedAt is null)
            throw InvalidCancelField("expectedUpdatedAt", "Обновите заказ перед отменой.");
        if (request.ExpectedUpdatedAt != order.UpdatedAt)
            throw new ServiceException(409, "order_update_conflict");
        var reason = request.Reason?.Trim();
        if (reason?.Length > 2000)
            throw InvalidCancelField("reason", "Причина отмены не должна превышать 2000 символов.");
        if (reason?.Length == 0) reason = null;

        var profile = await database.CustomerProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CustomerId == customerId, token);
        var actorName = string.Join(" ", new[] { profile?.LastName, profile?.FirstName, profile?.Patronymic }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        var previousStatus = order.CancelByCustomer(timeProvider.GetUtcNow());
        AddHistory(order, order.UpdatedAt, OrderHistoryKind.CustomerCancelled, OrderHistoryArea.Status,
            OrderHistoryActor.Customer, customerId, actorName.Length == 0 ? "Покупатель" : actorName,
            null, null, new(2, previousStatus, OrderStatus.Cancelled, null, reason));

        try
        {
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(token);
            database.ChangeTracker.Clear();
            var current = await database.Orders.AsNoTracking().Where(item => item.Id == order.Id)
                .Select(item => item.Status).SingleAsync(token);
            if (current == OrderStatus.Cancelled)
                return await GetCoreAsync(customerId, orderNumber, token);
            throw new ServiceException(409, "order_update_conflict");
        }

        return await GetCoreAsync(customerId, orderNumber, token);
    }

    private static ServiceException InvalidCancelField(string field, string detail)
        => new(400, "validation_failed")
        {
            Errors = new Dictionary<string, string[]> { [field] = [detail] }
        };
}
