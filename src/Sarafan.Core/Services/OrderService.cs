// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class OrderService(
    AppDbContext database,
    ConsentService consents,
    ICustomerOrderCodeGenerator codeGenerator,
    ICustomerOrderCodeCollisionDetector collisionDetector,
    IOptions<BackofficeBootstrapOptions> bootstrapOptions,
    ILogger<OrderService> logger)
{
    private const int CodeAllocationAttempts = 10;
    private readonly BackofficeBootstrapOptions _bootstrapOptions = bootstrapOptions.Value;

    public Task<OrderDto> GetAsync(
        int customerId,
        long orderId,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(OrderService).FullName}.{nameof(GetAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(customerId), customerId),
                (nameof(orderId), orderId),
                (nameof(cancellationToken), cancellationToken)),
            () => GetCoreAsync(customerId, orderId, cancellationToken),
            cancellationToken);

    public Task<OrderDto> CreateAsync(
        int customerId,
        string? sourceUrl,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(OrderService).FullName}.{nameof(CreateAsync)}",
            () => LogValueSummary.Inputs((nameof(customerId), customerId), (nameof(sourceUrl), sourceUrl), (nameof(idempotencyKey), idempotencyKey), (nameof(cancellationToken), cancellationToken)),
            () => CreateCoreAsync(customerId, sourceUrl, idempotencyKey, cancellationToken),
            cancellationToken);

    private async Task<OrderDto> CreateCoreAsync(
        int customerId,
        string? sourceUrl,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (!BackofficeUserService.RealOperationsEnabled(_bootstrapOptions))
        {
            throw new ServiceException(StatusCodes.Status404NotFound, "resource_not_found");
        }

        var normalizedSourceUrl = NormalizeSourceUrl(sourceUrl);
        if (idempotencyKey == Guid.Empty)
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_order_idempotency_key");
        }

        var operations = AppDatabaseOperations.For(database);
        for (var attempt = 0; attempt < CodeAllocationAttempts; attempt++)
        {
            var assignedNewCode = false;
            try
            {
                var allocation = await consents.WithPersonalDataAsync(customerId, async () =>
                {
                    var customer = await operations
                        .FindCustomerForUpdateAsync(database, customerId, cancellationToken)
                        ?? throw new ServiceException(StatusCodes.Status404NotFound, "customer_not_found");
                    var existing = await database.Orders
                        .SingleOrDefaultAsync(
                            order => order.CustomerId == customerId
                                && order.CreationIdempotencyKey == idempotencyKey,
                            cancellationToken);
                    if (existing is not null)
                    {
                        if (!string.Equals(existing.SourceUrl, normalizedSourceUrl, StringComparison.Ordinal))
                        {
                            throw new ServiceException(StatusCodes.Status409Conflict, "order_creation_conflict");
                        }

                        return new Allocation(existing, customer.OrderCode
                            ?? throw new InvalidOperationException("An existing order must have a customer order code."));
                    }

                    assignedNewCode = customer.OrderCode is null;
                    var customerOrderNumber = customer.AllocateOrderNumber(
                        customer.OrderCode ?? codeGenerator.Generate());
                    var order = new Order(customerId, customerOrderNumber, normalizedSourceUrl, idempotencyKey);
                    database.Orders.Add(order);
                    return new Allocation(order, customer.OrderCode!);
                }, cancellationToken);

                return ToDto(allocation);
            }
            catch (DbUpdateException exception) when (
                assignedNewCode && collisionDetector.IsCollision(exception))
            {
                database.ChangeTracker.Clear();
            }
        }

        throw new ServiceException(StatusCodes.Status503ServiceUnavailable, "order_number_allocation_failed");
    }

    private async Task<OrderDto> GetCoreAsync(
        int customerId,
        long orderId,
        CancellationToken cancellationToken)
    {
        if (!BackofficeUserService.RealOperationsEnabled(_bootstrapOptions))
        {
            throw new ServiceException(StatusCodes.Status404NotFound, "resource_not_found");
        }

        var order = await database.Orders
            .AsNoTracking()
            .Include(item => item.Customer)
            .SingleOrDefaultAsync(
                item => item.CustomerId == customerId && item.Id == orderId,
                cancellationToken);

        if (order is null || order.Customer is null || order.Customer.OrderCode is null)
        {
            throw new ServiceException(StatusCodes.Status404NotFound, "resource_not_found");
        }

        return ToDto(order);
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

    private static OrderDto ToDto(Allocation allocation) => ToDto(allocation.Order, allocation.CustomerOrderCode);

    private static OrderDto ToDto(Order order) => ToDto(order, order.Customer.OrderCode!);

    private static OrderDto ToDto(Order order, string customerOrderCode) => new(
        order.Id,
        $"{customerOrderCode}-{order.CustomerOrderNumber}",
        order.Status,
        order.SourceUrl);

    private sealed record Allocation(Order Order, string CustomerOrderCode);
}
