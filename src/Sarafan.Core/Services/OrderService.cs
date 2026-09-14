// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

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
    TimeProvider timeProvider,
    ILogger<OrderService> logger)
{
    private const int CodeAllocationAttempts = 10;
    private const int MaximumSearchLength = 2048;

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
        int? quantity,
        string? comment,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(OrderService).FullName}.{nameof(CreateAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(customerId), customerId),
                (nameof(sourceUrl), sourceUrl),
                (nameof(quantity), quantity),
                (nameof(comment), comment),
                (nameof(idempotencyKey), idempotencyKey),
                (nameof(cancellationToken), cancellationToken)),
            () => CreateCoreAsync(customerId, sourceUrl, quantity, comment, idempotencyKey, cancellationToken),
            cancellationToken);

    public Task<BackofficeOrderPageDto> ListForBackofficeAsync(
        int page,
        int pageSize,
        string sortBy,
        string sortOrder,
        string? search,
        string? status,
        string? statusGroup,
        string? createdFrom,
        string? createdTo,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(OrderService).FullName}.{nameof(ListForBackofficeAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(page), page),
                (nameof(pageSize), pageSize),
                (nameof(sortBy), sortBy),
                (nameof(sortOrder), sortOrder),
                (nameof(search), search),
                (nameof(status), status),
                (nameof(statusGroup), statusGroup),
                (nameof(createdFrom), createdFrom),
                (nameof(createdTo), createdTo),
                (nameof(cancellationToken), cancellationToken)),
            () => ListForBackofficeCoreAsync(
                page,
                pageSize,
                sortBy,
                sortOrder,
                search,
                status,
                statusGroup,
                createdFrom,
                createdTo,
                cancellationToken),
            cancellationToken);

    private async Task<OrderDto> CreateCoreAsync(
        int customerId,
        string? sourceUrl,
        int? quantity,
        string? comment,
        Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        var normalizedSourceUrl = NormalizeSourceUrl(sourceUrl);
        var normalizedQuantity = NormalizeQuantity(quantity);
        var normalizedComment = NormalizeComment(comment);
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
                        .Include(order => order.AppliedExchangeRateHistory)
                        .SingleOrDefaultAsync(
                            order => order.CustomerId == customerId
                                && order.CreationIdempotencyKey == idempotencyKey,
                            cancellationToken);
                    if (existing is not null)
                    {
                        if (!string.Equals(existing.SourceUrl, normalizedSourceUrl, StringComparison.Ordinal)
                            || existing.Quantity != normalizedQuantity
                            || !string.Equals(existing.Comment, normalizedComment, StringComparison.Ordinal))
                        {
                            throw new ServiceException(StatusCodes.Status409Conflict, "order_creation_conflict");
                        }

                        return new Allocation(existing, customer.OrderCode
                            ?? throw new InvalidOperationException("An existing order must have a customer order code."));
                    }

                    assignedNewCode = customer.OrderCode is null;
                    var customerOrderNumber = customer.AllocateOrderNumber(
                        customer.OrderCode ?? codeGenerator.Generate());
                    var order = new Order(
                        customerId,
                        customerOrderNumber,
                        normalizedSourceUrl,
                        normalizedQuantity,
                        normalizedComment,
                        idempotencyKey,
                        timeProvider.GetUtcNow());
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

    private async Task<BackofficeOrderPageDto> ListForBackofficeCoreAsync(
        int page,
        int pageSize,
        string sortBy,
        string sortOrder,
        string? search,
        string? status,
        string? statusGroup,
        string? createdFrom,
        string? createdTo,
        CancellationToken cancellationToken)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var sortByKey = sortBy?.Trim().ToLowerInvariant() switch
        {
            "ordernumber" => "orderNumber",
            "status" => "status",
            "productname" => "productName",
            "storename" => "storeName",
            "sellerprice" => "sellerPrice",
            "quantity" => "quantity",
            "createdat" => "createdAt",
            "updatedat" => "updatedAt",
            _ => null
        };
        var sortOrderKey = sortOrder?.Trim().ToLowerInvariant();
        var normalizedStatusGroup = string.IsNullOrWhiteSpace(statusGroup)
            ? null
            : statusGroup.Trim().ToLowerInvariant();
        var validStatus = true;
        OrderStatus? statusValue = null;
        if (status is not null)
        {
            validStatus = int.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var statusNumber)
                && Enum.IsDefined((OrderStatus)statusNumber);
            if (validStatus)
            {
                statusValue = (OrderStatus)statusNumber;
            }
        }
        var validCreatedFrom = TryParseListDate(createdFrom, out var createdFromValue);
        var validCreatedTo = TryParseListDate(createdTo, out var createdToValue);
        OrderStatusFilterGroup? selectedGroup = null;
        var hasStatusGroup = normalizedStatusGroup is not null
            && OrderStatusFilterGroups.TryGet(normalizedStatusGroup, out selectedGroup);
        if (page < 1
            || pageSize is < 1 or > 100
            || sortByKey is null
            || sortOrderKey is not ("asc" or "desc")
            || normalizedSearch is { Length: > MaximumSearchLength }
            || !validStatus
            || status is not null && normalizedStatusGroup is not null
            || normalizedStatusGroup is not null && !hasStatusGroup
            || !validCreatedFrom
            || !validCreatedTo
            || createdFromValue > createdToValue
            || createdFromValue == DateOnly.MinValue
            || createdToValue == DateOnly.MaxValue)
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_order_list_filter");
        }

        IQueryable<Order> query = database.Orders
            .AsNoTracking()
            .Where(item => item.Customer.OrderCode != null);
        if (normalizedSearch is not null)
        {
            query = AppDatabaseOperations.For(database).ApplyOrderSearch(query, normalizedSearch);
        }

        if (statusValue.HasValue)
        {
            query = query.Where(item => item.Status == statusValue.Value);
        }
        else if (selectedGroup is not null)
        {
            var groupStatuses = selectedGroup.Statuses.ToArray();
            query = query.Where(item => groupStatuses.Contains(item.Status));
        }

        if (createdFromValue.HasValue)
        {
            var from = ConsentCalendar.Midnight(createdFromValue.Value);
            query = query.Where(item => item.CreatedAt >= from);
        }

        if (createdToValue.HasValue)
        {
            var toExclusive = ConsentCalendar.Midnight(createdToValue.Value.AddDays(1));
            query = query.Where(item => item.CreatedAt < toExclusive);
        }

        var total = await query.CountAsync(cancellationToken);
        var descending = sortOrderKey == "desc";
        var ordered = (sortByKey, descending) switch
        {
            ("orderNumber", false) => query.OrderBy(item => item.Customer.OrderCode)
                .ThenBy(item => item.CustomerOrderNumber).ThenBy(item => item.Id),
            ("orderNumber", true) => query.OrderByDescending(item => item.Customer.OrderCode)
                .ThenByDescending(item => item.CustomerOrderNumber).ThenByDescending(item => item.Id),
            ("status", false) => query.OrderBy(item => item.Status).ThenBy(item => item.Id),
            ("status", true) => query.OrderByDescending(item => item.Status).ThenByDescending(item => item.Id),
            ("productName", false) => query.OrderBy(item => item.ProductName == null)
                .ThenBy(item => item.ProductName).ThenBy(item => item.Id),
            ("productName", true) => query.OrderBy(item => item.ProductName == null)
                .ThenByDescending(item => item.ProductName).ThenByDescending(item => item.Id),
            ("storeName", false) => query.OrderBy(item => item.StoreName == null)
                .ThenBy(item => item.StoreName).ThenBy(item => item.Id),
            ("storeName", true) => query.OrderBy(item => item.StoreName == null)
                .ThenByDescending(item => item.StoreName).ThenByDescending(item => item.Id),
            ("sellerPrice", false) => query.OrderBy(item => item.SellerPrice == null)
                .ThenBy(item => item.SellerPriceCurrency).ThenBy(item => item.SellerPrice).ThenBy(item => item.Id),
            ("sellerPrice", true) => query.OrderBy(item => item.SellerPrice == null)
                .ThenByDescending(item => item.SellerPriceCurrency).ThenByDescending(item => item.SellerPrice)
                .ThenByDescending(item => item.Id),
            ("quantity", false) => query.OrderBy(item => item.Quantity).ThenBy(item => item.Id),
            ("quantity", true) => query.OrderByDescending(item => item.Quantity).ThenByDescending(item => item.Id),
            ("updatedAt", false) => query.OrderBy(item => item.UpdatedAt).ThenBy(item => item.Id),
            ("updatedAt", true) => query.OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id),
            ("createdAt", false) => query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id),
            _ => query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
        };
        var offset = (long)(page - 1) * pageSize;
        var rows = offset > int.MaxValue
            ? []
            : await ordered.Skip((int)offset).Take(pageSize)
                .Select(item => new BackofficeOrderProjection(
                    item.Customer.OrderCode!,
                    item.CustomerOrderNumber,
                    item.Status,
                    item.SourceUrl,
                    item.ProductName,
                    item.StoreName,
                    item.SellerPrice,
                    item.SellerPriceCurrency,
                    item.Quantity,
                    item.CreatedAt,
                    item.UpdatedAt))
                .ToArrayAsync(cancellationToken);
        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        return new BackofficeOrderPageDto
        {
            Items = rows.Select(ToBackofficeDto).ToArray(),
            Pagination = new PaginationInfo
            {
                CurrentPage = page,
                PageSize = pageSize,
                TotalCount = total,
                TotalPages = totalPages,
                HasNextPage = page < totalPages,
                HasPreviousPage = page > 1
            },
            Sorting = new SortingInfo { SortBy = sortByKey, SortOrder = sortOrderKey },
            Search = normalizedSearch,
            Status = statusValue,
            StatusGroup = normalizedStatusGroup,
            CreatedFrom = createdFromValue,
            CreatedTo = createdToValue
        };
    }

    private async Task<OrderDto> GetCoreAsync(
        int customerId,
        long orderId,
        CancellationToken cancellationToken)
    {
        var order = await database.Orders
            .AsNoTracking()
            .Include(item => item.Customer)
            .Include(item => item.AppliedExchangeRateHistory)
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

    private static int NormalizeQuantity(int? quantity)
    {
        if (quantity is null or <= 0)
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_order_quantity");
        }

        return quantity.Value;
    }

    private static bool TryParseListDate(string? value, out DateOnly? date)
    {
        date = null;
        if (value is null)
        {
            return true;
        }

        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        date = parsed;
        return true;
    }

    private static string? NormalizeComment(string? comment)
    {
        var normalized = comment?.Trim();
        if (normalized?.Length > 2000)
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_order_comment");
        }

        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static OrderDto ToDto(Allocation allocation) => ToDto(allocation.Order, allocation.CustomerOrderCode);

    private static OrderDto ToDto(Order order) => ToDto(order, order.Customer.OrderCode!);

    private static OrderDto ToDto(Order order, string customerOrderCode) => new(
        order.Id,
        $"{customerOrderCode}-{order.CustomerOrderNumber}",
        order.Status,
        order.SourceUrl,
        order.ProductName,
        order.StoreName,
        order.ImageUrl,
        order.SellerPrice.HasValue && order.SellerPriceCurrency.HasValue
            ? new OrderSellerPriceDto(order.SellerPrice.Value, order.SellerPriceCurrency.Value)
            : null,
        order.LengthCm.HasValue && order.WidthCm.HasValue && order.HeightCm.HasValue
            ? new OrderDimensionsDto(order.LengthCm.Value, order.WidthCm.Value, order.HeightCm.Value)
            : null,
        order.Characteristics,
        order.Quantity,
        order.Comment,
        order.AppliedExchangeRateHistory is { } rate
            ? new OrderAppliedExchangeRateDto(
                rate.Id,
                rate.Provider,
                rate.BaseCurrency,
                rate.QuoteCurrency,
                rate.Nominal,
                rate.OfficialRate,
                rate.SourceEffectiveDate)
            : null);

    private static BackofficeOrderListItemDto ToBackofficeDto(BackofficeOrderProjection order) => new(
        $"{order.CustomerOrderCode}-{order.CustomerOrderNumber}",
        order.Status,
        order.SourceUrl,
        order.ProductName,
        order.StoreName,
        order.SellerPrice.HasValue && order.SellerPriceCurrency.HasValue
            ? new OrderSellerPriceDto(order.SellerPrice.Value, order.SellerPriceCurrency.Value)
            : null,
        order.Quantity,
        order.CreatedAt,
        order.UpdatedAt);

    private sealed record Allocation(Order Order, string CustomerOrderCode);

    private sealed record BackofficeOrderProjection(
        string CustomerOrderCode,
        long CustomerOrderNumber,
        OrderStatus Status,
        string SourceUrl,
        string? ProductName,
        string? StoreName,
        decimal? SellerPrice,
        Currency? SellerPriceCurrency,
        int Quantity,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
