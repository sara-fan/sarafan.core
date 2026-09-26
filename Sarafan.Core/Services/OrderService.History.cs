// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService
{
    private void AddHistory(Order order, DateTimeOffset at, OrderHistoryKind kind, OrderHistoryArea areas,
        OrderHistoryActor actorType, int? actorId, string actorName, OrderProductAuditEvent? product,
        OrderPricingSnapshot? pricing, OrderHistoryEvidence evidence)
        => database.Set<OrderHistoryEvent>().Add(new()
        {
            Order = order,
            At = at,
            Kind = kind,
            Areas = areas,
            ActorType = actorType,
            ActorId = actorId,
            ActorName = actorName,
            ProductAudit = product,
            PricingSnapshot = pricing,
            Payload = JsonSerializer.Serialize(evidence, PricingJson)
        });

    public Task<OrderHistoryOpsDto> HistoryOperationsAsync(string number, string[] roles, CancellationToken token)
        => PricingRun(nameof(HistoryOperationsAsync), async () =>
        {
            BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManualQuotes);
            await FindPublicOrder(number, token);
            return new OrderHistoryOpsDto(
                [new(0, "Создание заказа", "created"), new(100, "Изменение товара", "product-changed"),
                 new(200, "Распознавание товара", "parsed"), new(300, "Расчёт стоимости", "price-calculated"), new(400, "Подтверждение расчёта", "quote-confirmed")],
                [new(1, "Создание", "creation"), new(2, "Товар", "product"), new(4, "Стоимость", "pricing"), new(8, "Статус", "status")],
                [new(0, "Покупатель", "customer"), new(100, "Сотрудник", "staff"), new(200, "Система", "system")]);
        }, token);

    // Scalar UNION ALL projection keeps filtering/counting/paging in the provider, before loading evidence.
    internal sealed class HistoryRow
    {
        public int Source { get; set; }
        public long Id { get; set; }
        public DateTimeOffset At { get; set; }
        public OrderHistoryKind Kind { get; set; }
        public OrderHistoryArea Areas { get; set; }
        public OrderHistoryActor ActorType { get; set; }
        public string ActorName { get; set; } = "";
        public bool ActorNameHistorical { get; set; }
        public long? ProductAuditId { get; set; }
        public long? PricingSnapshotId { get; set; }
    }

    internal IQueryable<HistoryRow> HistoryQuery(long orderId)
    {
        var events = database.Set<OrderHistoryEvent>().AsNoTracking().Where(item => item.OrderId == orderId);
        var products = database.Set<OrderProductAuditEvent>().AsNoTracking().Where(item => item.OrderId == orderId
            && !events.Any(history => history.ProductAuditId == item.Id));
        var prices = database.OrderPricingSnapshots.AsNoTracking().Where(item => item.OrderId == orderId
            && !events.Any(history => history.PricingSnapshotId == item.Id));
        // Old product writes use exactly the same timestamp and actor for their automatic recalculation.
        // Require a one-to-one match in both directions; ambiguous evidence remains separate.
        var pairs = from product in products
                    from price in prices
                    where product.OccurredAt == price.At && product.ActorId == price.ActorId && price.ValidUntil == null
                        && prices.Count(p => p.At == product.OccurredAt && p.ActorId == product.ActorId && p.ValidUntil == null) == 1
                        && products.Count(p => p.OccurredAt == price.At && p.ActorId == price.ActorId) == 1
                    select new { ProductId = product.Id, PriceId = price.Id };
        var current = events.Select(item => new HistoryRow
        {
            Source = 0,
            Id = item.Id,
            At = item.At,
            Kind = item.Kind,
            Areas = item.Areas,
            ActorType = item.ActorType,
            ActorName = item.ActorName,
            ActorNameHistorical = true,
            ProductAuditId = item.ProductAuditId,
            PricingSnapshotId = item.PricingSnapshotId
        });
        var legacyProducts = products.Select(item => new HistoryRow
        {
            Source = 1,
            Id = item.Id,
            At = item.OccurredAt,
            Kind = item.Kind == OrderProductAuditKind.Created ? OrderHistoryKind.Created
                : item.Kind == OrderProductAuditKind.Parsed ? OrderHistoryKind.Parsed : OrderHistoryKind.ProductChanged,
            Areas = OrderHistoryArea.Product | (item.Kind == OrderProductAuditKind.Created ? OrderHistoryArea.Creation : 0)
                | (pairs.Any(pair => pair.ProductId == item.Id) ? OrderHistoryArea.Pricing : 0),
            ActorType = item.Kind == OrderProductAuditKind.Created ? OrderHistoryActor.Customer
                : item.ActorId == null ? OrderHistoryActor.System : OrderHistoryActor.Staff,
            ActorName = item.Kind == OrderProductAuditKind.Created ? "Покупатель" : item.ActorId == null ? "Система"
                : database.BackofficeUsers.Where(actor => actor.Id == item.ActorId)
                    .Select(actor => (actor.LastName + " " + actor.FirstName + " " + (actor.Patronymic ?? "")).Trim()).FirstOrDefault() ?? "Сотрудник недоступен",
            ActorNameHistorical = item.ActorId == null,
            ProductAuditId = item.Id,
            PricingSnapshotId = pairs.Where(pair => pair.ProductId == item.Id).Select(pair => (long?)pair.PriceId).FirstOrDefault()
        });
        var legacyPrices = prices.Where(item => !pairs.Any(pair => pair.PriceId == item.Id)).Select(item => new HistoryRow
        {
            Source = 2,
            Id = item.Id,
            At = item.At,
            Kind = item.ValidUntil == null ? OrderHistoryKind.PriceCalculated : OrderHistoryKind.QuoteConfirmed,
            Areas = OrderHistoryArea.Pricing | (item.ValidUntil == null ? 0 : OrderHistoryArea.Status),
            ActorType = item.ActorId == null ? OrderHistoryActor.System : OrderHistoryActor.Staff,
            ActorName = item.ActorName ?? "Система",
            ActorNameHistorical = true,
            ProductAuditId = null,
            PricingSnapshotId = item.Id
        });
        var creation = database.Orders.Where(order => order.Id == orderId
            && !events.Any(item => item.Kind == OrderHistoryKind.Created)
            && !products.Any(item => item.Kind == OrderProductAuditKind.Created)).Select(order => new HistoryRow
            {
                Source = 3,
                Id = 1,
                At = order.CreatedAt,
                Kind = OrderHistoryKind.Created,
                Areas = OrderHistoryArea.Creation,
                ActorType = OrderHistoryActor.Customer,
                ActorName = "Покупатель",
                ActorNameHistorical = true,
                ProductAuditId = null,
                PricingSnapshotId = null
            });
        return current.Concat(legacyProducts).Concat(legacyPrices).Concat(creation);
    }

    private static OrderHistoryItemDto HistoryItem(HistoryRow row)
        => new($"{row.Source}-{row.Id}", row.At, row.Kind, row.Areas, row.ActorType, row.ActorName, row.ActorNameHistorical);

    public Task<OrderHistoryPageDto> HistoryAsync(string number, string[] roles, int page, int pageSize,
        string sortBy, string sortOrder, string? search, int? area, int? actorType, string? from, string? to, CancellationToken token)
        => PricingRun(nameof(HistoryAsync), async () =>
        {
            BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManualQuotes);
            var order = await FindPublicOrder(number, token);
            search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            sortBy = sortBy.Trim().ToLowerInvariant();
            sortOrder = sortOrder.Trim().ToLowerInvariant();
            if (page < 1 || pageSize is not (10 or 25 or 50 or 100) || search?.Length > 200
                || sortBy is not ("timestamp" or "event" or "actor") || sortOrder is not ("asc" or "desc")
                || area is not (null or 1 or 2 or 4 or 8) || actorType is not (null or 0 or 100 or 200)
                || !TryParseListDate(from, out var fromDate) || !TryParseListDate(to, out var toDate)
                || fromDate > toDate || fromDate == DateOnly.MinValue || toDate == DateOnly.MaxValue)
                throw new ServiceException(400, "invalid_order_list_filter");
            var query = HistoryQuery(order.Id);
            if (search is not null) { var term = search.ToLowerInvariant(); query = query.Where(row => row.ActorName.ToLower().Contains(term)); }
            if (area is not null) query = query.Where(row => ((int)row.Areas & area.Value) != 0);
            if (actorType is not null) query = query.Where(row => (int)row.ActorType == actorType.Value);
            if (fromDate is not null) { var start = ConsentCalendar.Midnight(fromDate.Value); query = query.Where(row => row.At >= start); }
            if (toDate is not null) { var end = ConsentCalendar.Midnight(toDate.Value.AddDays(1)); query = query.Where(row => row.At < end); }
            var count = await query.CountAsync(token);
            var ordered = (sortBy, sortOrder) switch
            {
                ("event", "asc") => query.OrderBy(row => row.Kind),
                ("event", "desc") => query.OrderByDescending(row => row.Kind),
                ("actor", "asc") => query.OrderBy(row => row.ActorName),
                ("actor", "desc") => query.OrderByDescending(row => row.ActorName),
                ("timestamp", "asc") => query.OrderBy(row => row.At),
                _ => query.OrderByDescending(row => row.At)
            };
            var offset = (long)(page - 1) * pageSize;
            var rows = offset > int.MaxValue ? [] : await ordered.ThenByDescending(row => row.At)
                .ThenBy(row => row.Source).ThenByDescending(row => row.Id).Skip((int)offset).Take(pageSize).ToArrayAsync(token);
            var pages = (int)Math.Ceiling(count / (double)pageSize);
            return new OrderHistoryPageDto
            {
                Items = rows.Select(HistoryItem).ToArray(),
                Search = search,
                Area = (OrderHistoryArea?)area,
                ActorType = (OrderHistoryActor?)actorType,
                From = fromDate,
                To = toDate,
                Sorting = new() { SortBy = sortBy, SortOrder = sortOrder },
                Pagination = new()
                {
                    CurrentPage = page,
                    PageSize = pageSize,
                    TotalCount = count,
                    TotalPages = pages,
                    HasNextPage = page < pages,
                    HasPreviousPage = page > 1
                }
            };
        }, token);

    public Task<OrderHistoryDetailDto> HistoryDetailAsync(string number, string eventKey, string[] roles, CancellationToken token)
        => PricingRun(nameof(HistoryDetailAsync), async () =>
        {
            BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManualQuotes);
            var order = await FindPublicOrder(number, token);
            var key = eventKey.Split('-');
            if (key.Length != 2 || !int.TryParse(key[0], NumberStyles.None, CultureInfo.InvariantCulture, out var source)
                || !long.TryParse(key[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || source is < 0 or > 3 || id <= 0 || eventKey != $"{source}-{id}")
                throw new ServiceException(404, "resource_not_found");
            var row = await HistoryQuery(order.Id).SingleOrDefaultAsync(item => item.Source == source && item.Id == id, token)
                ?? throw new ServiceException(404, "resource_not_found");
            var product = row.ProductAuditId is null ? null : await database.Set<OrderProductAuditEvent>().AsNoTracking()
                .SingleAsync(item => item.Id == row.ProductAuditId && item.OrderId == order.Id, token);
            var price = row.PricingSnapshotId is null ? null : await database.OrderPricingSnapshots.AsNoTracking()
                .SingleAsync(item => item.Id == row.PricingSnapshotId && item.OrderId == order.Id, token);
            var before = price is null ? null : await database.OrderPricingSnapshots.AsNoTracking()
                .Where(item => item.OrderId == order.Id && item.Id < price.Id).OrderByDescending(item => item.Id).FirstOrDefaultAsync(token);
            var evidence = source == 0 ? JsonSerializer.Deserialize<OrderHistoryEvidence>(
                await database.Set<OrderHistoryEvent>().Where(item => item.Id == id && item.OrderId == order.Id).Select(item => item.Payload).SingleAsync(token), PricingJson)!
                : new OrderHistoryEvidence(1, price?.ValidUntil is not null ? OrderStatus.UnderReview : null,
                    price?.ValidUntil is not null ? OrderStatus.QuoteReady : null, null);
            return new OrderHistoryDetailDto(HistoryItem(row), evidence.Version, source == 3,
                product?.Before is null ? null : JsonSerializer.Deserialize<OrderProductDto>(product.Before, PricingJson),
                product is null ? null : JsonSerializer.Deserialize<OrderProductDto>(product.After, PricingJson),
                evidence.StatusBefore, evidence.StatusAfter, evidence.SourceUrl,
                before is null ? null : ReadCalculation(before), price is null ? null : ReadCalculation(price),
                before?.ValidUntil, price?.ValidUntil);
        }, token);
}
