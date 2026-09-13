// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class OrderService(
    AppDbContext database,
    ConsentService consents,
    ICustomerOrderCodeGenerator codeGenerator,
    ILogger<OrderService> logger)
{
    private const int CodeAllocationAttempts = 10;
    private const string CustomerOrderCodeIndex = "ux_customers_order_code";

    public Task<OrderDto> CreateAsync(
        int customerId,
        string? sourceUrl,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(OrderService).FullName}.{nameof(CreateAsync)}",
            () => "customer=[redacted]; sourceUrl=[redacted]; idempotencyKey=[redacted]",
            () => CreateCoreAsync(customerId, NormalizeSourceUrl(sourceUrl), idempotencyKey, cancellationToken),
            cancellationToken);

    private async Task<OrderDto> CreateCoreAsync(
        int customerId,
        string sourceUrl,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty)
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_order_idempotency_key");
        }

        for (var attempt = 0; attempt < CodeAllocationAttempts; attempt++)
        {
            var assignedNewCode = false;
            try
            {
                var allocation = await consents.WithPersonalDataAsync(customerId, async () =>
                {
                    var customer = await database.Customers
                        .FromSqlInterpolated($"SELECT * FROM customers WHERE id = {customerId} FOR UPDATE")
                        .SingleOrDefaultAsync(cancellationToken)
                        ?? throw new ServiceException(StatusCodes.Status404NotFound, "customer_not_found");
                    var existing = await database.Orders
                        .SingleOrDefaultAsync(
                            order => order.CustomerId == customerId
                                && order.CreationIdempotencyKey == idempotencyKey,
                            cancellationToken);
                    if (existing is not null)
                    {
                        if (!string.Equals(existing.SourceUrl, sourceUrl, StringComparison.Ordinal))
                        {
                            throw new ServiceException(StatusCodes.Status409Conflict, "order_creation_conflict");
                        }

                        return new Allocation(existing, customer.OrderCode
                            ?? throw new InvalidOperationException("An existing order must have a customer order code."));
                    }

                    assignedNewCode = customer.OrderCode is null;
                    var customerOrderNumber = customer.AllocateOrderNumber(
                        customer.OrderCode ?? codeGenerator.Generate());
                    var order = new Order(customerId, customerOrderNumber, sourceUrl, idempotencyKey);
                    database.Orders.Add(order);
                    return new Allocation(order, customer.OrderCode!);
                }, cancellationToken);

                return ToDto(allocation);
            }
            catch (DbUpdateException exception) when (
                assignedNewCode && IsCustomerOrderCodeCollision(exception))
            {
                database.ChangeTracker.Clear();
            }
        }

        throw new ServiceException(StatusCodes.Status503ServiceUnavailable, "order_number_allocation_failed");
    }

    private static string NormalizeSourceUrl(string? sourceUrl)
    {
        var normalized = sourceUrl?.Trim();
        if (string.IsNullOrEmpty(normalized)
            || normalized.Length > 2048
            || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_order_url");
        }

        return normalized;
    }

    private static bool IsCustomerOrderCodeCollision(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CustomerOrderCodeIndex
        };

    private static OrderDto ToDto(Allocation allocation) => new(
        allocation.Order.Id,
        $"{allocation.CustomerOrderCode}-{allocation.Order.CustomerOrderNumber}",
        allocation.Order.Status,
        allocation.Order.SourceUrl);

    private sealed record Allocation(Order Order, string CustomerOrderCode);
}
