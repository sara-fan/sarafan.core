// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService(
    AppDbContext database,
    ConsentService consents,
    ICustomerOrderCodeGenerator codeGenerator,
    ICustomerOrderCodeCollisionDetector collisionDetector,
    IanaTldCatalogService tlds,
    OrderLimitService limits,
    TimeProvider timeProvider,
    ILogger<OrderService> logger,
    IAutomaticPriceSource? automaticPrices = null)
{
    private const int CodeAllocationAttempts = 10;
    private const int MaximumSearchLength = 2048;

    public Task<IReadOnlyList<CustomerOrderListItemDto>> ListAsync(
        int customerId,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(OrderService).FullName}.{nameof(ListAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(customerId), customerId),
                (nameof(cancellationToken), cancellationToken)),
            () => ListCoreAsync(customerId, cancellationToken),
            cancellationToken);

    public Task<OrderDto> GetAsync(
        int customerId,
        string orderNumber,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(OrderService).FullName}.{nameof(GetAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(customerId), customerId),
                (nameof(orderNumber), orderNumber),
                (nameof(cancellationToken), cancellationToken)),
            () => GetCoreAsync(customerId, orderNumber, cancellationToken),
            cancellationToken);

    public Task<OrderDto> CreateAsync(
        int customerId,
        string? sourceUrl,
        int? quantity,
        string? comment,
        Guid idempotencyKey,
        CancellationToken cancellationToken,
        OrderProductRequest? product = null)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(OrderService).FullName}.{nameof(CreateAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(customerId), customerId),
                (nameof(sourceUrl), sourceUrl),
                (nameof(quantity), quantity),
                (nameof(comment), comment),
                (nameof(product), product),
                (nameof(idempotencyKey), idempotencyKey),
                (nameof(cancellationToken), cancellationToken)),
            () => CreateCoreAsync(customerId, sourceUrl, quantity, comment, idempotencyKey, cancellationToken, product),
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
        CancellationToken cancellationToken,
        OrderProductRequest? productRequest)
    {
        string? normalizedSourceUrl = null;
        ServiceException? sourceUrlError = null;
        try
        {
            var catalog = await tlds.GetRequiredAsync(cancellationToken);
            normalizedSourceUrl = ProductSourceUrl.Normalize(sourceUrl, catalog.Values);
        }
        catch (ServiceException exception) when (exception.Code is "invalid_order_url" or "tld_catalog_unavailable")
        {
            sourceUrlError = exception;
        }
        var normalizedQuantity = NormalizeQuantity(quantity);
        var normalizedComment = NormalizeComment(comment);
        var product = OrderProductRules.Normalize(productRequest, normalizedQuantity, normalizedComment);
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
                        if (!ProductSourceUrl.MatchesStored(existing.SourceUrl, sourceUrl)
                            || await ProductAtCreation(existing, cancellationToken) != product)
                        {
                            throw new ServiceException(StatusCodes.Status409Conflict, "order_creation_conflict");
                        }

                        return new Allocation(existing, customer.OrderCode
                            ?? throw new InvalidOperationException("An existing order must have a customer order code."));
                    }

                    if (sourceUrlError is not null)
                    {
                        throw sourceUrlError;
                    }

                    OrderProductRules.Validate(product);
                    var pair = OrderLimitService.Validate(product, await limits.GetPairAsync(cancellationToken));

                    assignedNewCode = customer.OrderCode is null;
                    var customerOrderNumber = customer.AllocateOrderNumber(
                        customer.OrderCode ?? codeGenerator.Generate());
                    var order = new Order(
                        customerId,
                        customerOrderNumber,
                        normalizedSourceUrl!,
                        normalizedQuantity,
                        normalizedComment,
                        idempotencyKey,
                        timeProvider.GetUtcNow());
                    database.Orders.Add(order);
                    order.SetProduct(product);
                    await AppDatabaseOperations.For(database).LockServiceCatalogueMutationsAsync(database, cancellationToken);
                    var pricingSnapshot = await AddPricingSnapshotAsync(order, order.CreatedAt, OrderPricingInputs.Empty, null, null, cancellationToken);
                    var productAudit = new OrderProductAuditEvent
                    {
                        Order = order,
                        Kind = OrderProductAuditKind.Created,
                        OccurredAt = order.CreatedAt,
                        After = JsonSerializer.Serialize(product),
                        UsdRateId = pair.Usd.Id,
                        EurRateId = pair.Eur.Id
                    };
                    database.Set<OrderProductAuditEvent>().Add(productAudit);
                    var profile = await database.CustomerProfiles.AsNoTracking()
                        .SingleOrDefaultAsync(item => item.CustomerId == customerId, cancellationToken);
                    var customerName = string.Join(" ", new[] { profile?.LastName, profile?.FirstName, profile?.Patronymic }
                        .Where(part => !string.IsNullOrWhiteSpace(part)));
                    AddHistory(order, order.CreatedAt, OrderHistoryKind.Created,
                        OrderHistoryArea.Creation | OrderHistoryArea.Product | OrderHistoryArea.Pricing | OrderHistoryArea.Status,
                        OrderHistoryActor.Customer, customerId, customerName.Length == 0 ? "Покупатель" : customerName, productAudit, pricingSnapshot,
                        new(1, null, order.Status, order.SourceUrl));
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
        var normalizedStatusGroup = statusGroup is null ? null : statusGroup.Trim().ToLowerInvariant();
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
        string orderNumber,
        CancellationToken cancellationToken)
    {
        var (code, sequence) = ParsePublicOrderNumber(orderNumber);
        var order = await database.Orders
            .AsNoTracking()
            .Include(item => item.Customer)
            .Include(item => item.AppliedExchangeRateHistory)
            .SingleOrDefaultAsync(
                item => item.CustomerId == customerId && item.Customer.OrderCode == code && item.CustomerOrderNumber == sequence,
                cancellationToken);

        if (order is null || order.Customer is null || order.Customer.OrderCode is null)
        {
            throw new ServiceException(StatusCodes.Status404NotFound, "resource_not_found");
        }

        return ToDto(order);
    }

    private async Task<IReadOnlyList<CustomerOrderListItemDto>> ListCoreAsync(
        int customerId,
        CancellationToken cancellationToken)
    {
        var rows = await database.Orders
            .AsNoTracking()
            .Where(item => item.CustomerId == customerId && item.Customer.OrderCode != null)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new CustomerOrderProjection(
                item.Customer.OrderCode!,
                item.CustomerOrderNumber,
                item.Status,
                item.SourceUrl,
                item.ProductName,
                item.StoreName,
                item.ImageUrl,
                item.SellerPrice,
                item.SellerPriceCurrency,
                item.Quantity,
                item.CreatedAt))
            .ToArrayAsync(cancellationToken);

        return rows.Select(ToCustomerDto).ToArray();
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
        $"{customerOrderCode}-{order.CustomerOrderNumber}",
        order.Status,
        ProductSourceUrl.NormalizeStored(order.SourceUrl),
        order.ProductName,
        order.StoreName,
        order.ImageUrl,
        Price(order.SellerPrice, order.SellerPriceCurrency),
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
            : null)
    {
        Product = CurrentProduct(order),
        CreatedAt = order.CreatedAt,
        ShowReviewFields = order.Status == OrderStatus.UnderReview
    };

    private static BackofficeOrderListItemDto ToBackofficeDto(BackofficeOrderProjection order) => new(
        $"{order.CustomerOrderCode}-{order.CustomerOrderNumber}",
        order.Status,
        ProductSourceUrl.NormalizeStored(order.SourceUrl),
        order.ProductName,
        order.StoreName,
        order.SellerPrice.HasValue && order.SellerPriceCurrency.HasValue
            ? new OrderSellerPriceDto(order.SellerPrice.Value, order.SellerPriceCurrency.Value)
            : null,
        order.Quantity,
        order.CreatedAt,
        order.UpdatedAt);

    private static CustomerOrderListItemDto ToCustomerDto(CustomerOrderProjection order) => new(
        $"{order.CustomerOrderCode}-{order.CustomerOrderNumber}",
        order.Status,
        ProductSourceUrl.NormalizeStored(order.SourceUrl),
        order.ProductName,
        order.StoreName,
        order.ImageUrl,
        order.SellerPrice.HasValue && order.SellerPriceCurrency.HasValue
            ? new OrderSellerPriceDto(order.SellerPrice.Value, order.SellerPriceCurrency.Value)
            : null,
        order.Quantity,
        order.CreatedAt);

    private sealed record Allocation(Order Order, string CustomerOrderCode);

    private sealed record CustomerOrderProjection(
        string CustomerOrderCode,
        long CustomerOrderNumber,
        OrderStatus Status,
        string SourceUrl,
        string? ProductName,
        string? StoreName,
        string? ImageUrl,
        decimal? SellerPrice,
        Currency? SellerPriceCurrency,
        int Quantity,
        DateTimeOffset CreatedAt);

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
