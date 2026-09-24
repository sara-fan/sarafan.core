// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService
{
    public Task<BackofficeOrderDetailsDto> GetForBackofficeAsync(string orderNumber, string[] roles, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(OrderService).FullName}.{nameof(GetForBackofficeAsync)}",
            () => LogValueSummary.Inputs((nameof(orderNumber), orderNumber), (nameof(cancellationToken), cancellationToken)), async () =>
            {
                BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManualQuotes);
                var order = await FindPublicOrder(orderNumber, cancellationToken);
                var details = StaffDetails(order, roles, await limits.GetPairAsync(cancellationToken));
                return details with
                {
                    SavedLimitSourceEffectiveDate = await SavedLimitSourceEffectiveDate(order.Id, cancellationToken)
                };
            }, cancellationToken);

    public Task<BackofficeOrderDetailsDto> UpdateProductAsync(string orderNumber, UpdateOrderProductRequest request,
        int actorId, string[] roles, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(OrderService).FullName}.{nameof(UpdateProductAsync)}",
            () => LogValueSummary.Inputs((nameof(orderNumber), orderNumber), (nameof(request), request),
                (nameof(actorId), actorId), (nameof(cancellationToken), cancellationToken)), async () =>
            {
                BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.EditOrderProduct);
                await using var transaction = await AppDatabaseOperations.For(database).BeginTransactionAsync(database, cancellationToken);
                await AppDatabaseOperations.For(database).LockServiceCatalogueMutationsAsync(database, cancellationToken);
                var order = await FindPublicOrder(orderNumber, cancellationToken);
                if (order.Status != OrderStatus.UnderReview) throw new ServiceException(409, "order_not_editable");
                if (request.ExpectedUpdatedAt != order.UpdatedAt) throw new ServiceException(409, "order_update_conflict");
                var product = OrderProductRules.Normalize(new OrderProductRequest
                {
                    ProductName = request.ProductName,
                    StoreName = request.StoreName,
                    SellerPrice = request.SellerPrice,
                    Color = request.Color,
                    Size = request.Size
                }, request.Quantity ?? 0, request.Comment);
                OrderProductRules.Validate(product);
                var pair = OrderLimitService.Validate(product, await limits.GetPairAsync(cancellationToken));
                var before = CurrentProduct(order);
                order.CorrectProduct(product.StoreName, product, timeProvider.GetUtcNow());
                if (before.SellerPrice != product.SellerPrice || before.Quantity != product.Quantity)
                {
                    var latest = await LatestPricingAsync(order.Id, cancellationToken);
                    var inputs = latest is null ? OrderPricingInputs.Empty : ReadCalculation(latest).Inputs;
                    var actor = await database.BackofficeUsers.AsNoTracking().SingleAsync(row => row.Id == actorId, cancellationToken);
                    var actorName = string.Join(' ', new[] { actor.LastName, actor.FirstName, actor.Patronymic }.Where(value => !string.IsNullOrWhiteSpace(value)));
                    await AddPricingSnapshotAsync(order, order.UpdatedAt, inputs, actorId, actorName, cancellationToken);
                }
                database.Set<OrderProductAuditEvent>().Add(new()
                {
                    OrderId = order.Id,
                    Kind = OrderProductAuditKind.StaffCorrected,
                    ActorId = actorId,
                    OccurredAt = order.UpdatedAt,
                    Before = JsonSerializer.Serialize(before),
                    After = JsonSerializer.Serialize(product),
                    UsdRateId = pair.Usd.Id,
                    EurRateId = pair.Eur.Id
                });
                try
                {
                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    database.ChangeTracker.Clear();
                    throw new ServiceException(409, "order_update_conflict");
                }
                return StaffDetails(order, roles, pair) with { SavedLimitSourceEffectiveDate = pair.Usd.SourceEffectiveDate };
            }, cancellationToken);

    private async Task<Order> FindPublicOrder(string number, CancellationToken cancellationToken)
    {
        var (code, sequence) = ParsePublicOrderNumber(number);
        return await database.Orders.Include(order => order.Customer).ThenInclude(customer => customer.Profile)
            .SingleOrDefaultAsync(order => order.Customer.OrderCode == code && order.CustomerOrderNumber == sequence, cancellationToken)
            ?? throw new ServiceException(404, "resource_not_found");
    }

    private static (string Code, long Sequence) ParsePublicOrderNumber(string number)
    {
        var parts = number.Split('-');
        if (parts.Length != 2 || parts[0].Length != 8 || parts[0].Any(c => c is < '0' or > '9')
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence <= 0 || parts[1] != sequence.ToString(CultureInfo.InvariantCulture))
            throw new ServiceException(404, "resource_not_found");
        return (parts[0], sequence);
    }

    private static BackofficeOrderDetailsDto StaffDetails(Order order, string[] roles, OrderLimitRatePair? pair)
    {
        var profile = order.Customer.Profile;
        return new($"{order.Customer.OrderCode}-{order.CustomerOrderNumber}", order.Status,
            ProductSourceUrl.NormalizeStored(order.SourceUrl), order.CreatedAt, order.UpdatedAt,
            CurrentProduct(order), new(profile?.LastName, profile?.FirstName, profile?.Patronymic,
                order.Customer.Phone, profile?.Email, profile?.PassportSeries, profile?.PassportNumber,
                profile?.PassportIssueDate, profile?.PassportIssuedBy, profile?.Inn, profile?.PostalCode, profile?.City, profile?.Address),
            OrderLimitService.ToDto(pair), order.Status == OrderStatus.UnderReview
                && BackofficeAuthorization.IsAllowed(roles, BackofficeAction.EditOrderProduct))
        {
            ImageUrl = order.ImageUrl,
            Dimensions = order.LengthCm.HasValue && order.WidthCm.HasValue && order.HeightCm.HasValue
                ? new(order.LengthCm.Value, order.WidthCm.Value, order.HeightCm.Value) : null,
            Characteristics = order.Characteristics
        };
    }

    internal static OrderProductDto CurrentProduct(Order order)
        => new(order.ProductName, Price(order.SellerPrice, order.SellerPriceCurrency),
            order.Quantity, order.Color, order.Size, order.Comment)
        {
            StoreName = order.StoreName
        };

    private async Task<OrderProductDto> ProductAtCreation(Order order, CancellationToken cancellationToken)
    {
        var snapshot = await database.Set<OrderProductAuditEvent>().AsNoTracking()
            .Where(item => item.OrderId == order.Id && item.Kind == OrderProductAuditKind.Created)
            .Select(item => item.After)
            .SingleOrDefaultAsync(cancellationToken);
        return snapshot is null
            ? CurrentProduct(order)
            : JsonSerializer.Deserialize<OrderProductDto>(snapshot)
                ?? throw new InvalidOperationException("The order creation audit snapshot is invalid.");
    }

    private Task<DateOnly?> SavedLimitSourceEffectiveDate(long orderId, CancellationToken cancellationToken)
        => (from audit in database.Set<OrderProductAuditEvent>().AsNoTracking()
            where audit.OrderId == orderId && audit.EurRateId != null
            join rate in database.ExchangeRateHistory.AsNoTracking()
                on audit.EurRateId equals rate.Id
            orderby audit.OccurredAt descending, audit.Id descending
            select (DateOnly?)rate.SourceEffectiveDate).FirstOrDefaultAsync(cancellationToken);

    private static OrderSellerPriceDto? Price(decimal? amount, Currency? currency)
        => amount.HasValue && currency.HasValue ? new(amount.Value, currency.Value) : null;
}
