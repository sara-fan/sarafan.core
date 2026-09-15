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
                RequireStaff(roles, BackofficeAction.ManualQuotes);
                var order = await FindPublicOrder(orderNumber, cancellationToken);
                var details = StaffDetails(order, roles, await limits.GetPairAsync(cancellationToken));
                var savedRateId = order.UpdatedLimitEurRateId ?? order.CreatedLimitEurRateId;
                return details with
                {
                    SavedLimitSourceEffectiveDate = await database.ExchangeRateHistory.AsNoTracking()
                        .Where(rate => rate.Id == savedRateId).Select(rate => (DateOnly?)rate.SourceEffectiveDate)
                        .SingleOrDefaultAsync(cancellationToken)
                };
            }, cancellationToken);

    public Task<BackofficeOrderDetailsDto> UpdateProductAsync(string orderNumber, UpdateOrderProductRequest request,
        int actorId, string[] roles, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(OrderService).FullName}.{nameof(UpdateProductAsync)}",
            () => LogValueSummary.Inputs((nameof(orderNumber), orderNumber), (nameof(request), request),
                (nameof(actorId), actorId), (nameof(cancellationToken), cancellationToken)), async () =>
            {
                RequireStaff(roles, BackofficeAction.EditOrderProduct);
                await using var transaction = await AppDatabaseOperations.For(database).BeginTransactionAsync(database, cancellationToken);
                var order = await FindPublicOrder(orderNumber, cancellationToken);
                if (order.Status != OrderStatus.UnderReview) throw new ServiceException(409, "order_not_editable");
                if (request.ExpectedUpdatedAt != order.UpdatedAt) throw new ServiceException(409, "order_update_conflict");
                var product = OrderProductRules.Normalize(new SubmittedProductRequest
                {
                    ProductName = request.ProductName,
                    SellerPrice = request.SellerPrice,
                    Color = request.Color,
                    Size = request.Size
                }, request.Quantity ?? 0, request.Comment);
                OrderProductRules.Validate(product);
                var pair = OrderLimitService.Validate(product, await limits.GetPairAsync(cancellationToken));
                var before = Effective(order);
                order.CorrectProduct(product, pair.Usd.Id, pair.Eur.Id, timeProvider.GetUtcNow());
                database.Set<OrderProductAuditEvent>().Add(new()
                {
                    OrderId = order.Id,
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

    private static void RequireStaff(string[] roles, BackofficeAction action)
    {
        if (!BackofficeAuthorization.IsAllowed(roles, action)) throw new ServiceException(403, "access_denied");
    }

    private async Task<Order> FindPublicOrder(string number, CancellationToken cancellationToken)
    {
        var parts = number.Split('-');
        if (parts.Length != 2 || parts[0].Length != 8 || parts[0].Any(c => c is < '0' or > '9')
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || sequence <= 0 || parts[1] != sequence.ToString(CultureInfo.InvariantCulture))
            throw new ServiceException(404, "resource_not_found");
        var code = parts[0];
        return await database.Orders.Include(order => order.Customer).ThenInclude(customer => customer.Profile)
            .SingleOrDefaultAsync(order => order.Customer.OrderCode == code && order.CustomerOrderNumber == sequence, cancellationToken)
            ?? throw new ServiceException(404, "resource_not_found");
    }

    private static BackofficeOrderDetailsDto StaffDetails(Order order, string[] roles, OrderLimitRatePair? pair)
    {
        var profile = order.Customer.Profile;
        return new($"{order.Customer.OrderCode}-{order.CustomerOrderNumber}", order.Status,
            ProductSourceUrl.NormalizeStored(order.SourceUrl), order.CreatedAt, order.UpdatedAt,
            Effective(order), Submitted(order), new(profile?.LastName, profile?.FirstName, profile?.Patronymic,
                order.Customer.Phone, profile?.Email, profile?.PassportSeries, profile?.PassportNumber,
                profile?.PassportIssueDate, profile?.PassportIssuedBy, profile?.Inn, profile?.PostalCode, profile?.City, profile?.Address),
            OrderLimitService.ToDto(pair), order.Status == OrderStatus.UnderReview
                && BackofficeAuthorization.IsAllowed(roles, BackofficeAction.EditOrderProduct))
        {
            StoreName = order.StoreName,
            ImageUrl = order.ImageUrl,
            Dimensions = order.LengthCm.HasValue && order.WidthCm.HasValue && order.HeightCm.HasValue
                ? new(order.LengthCm.Value, order.WidthCm.Value, order.HeightCm.Value) : null,
            Characteristics = order.Characteristics
        };
    }

    internal static OrderProductDto Submitted(Order order, bool legacySnapshot = true)
        => new(order.SubmittedProductName ?? (legacySnapshot ? order.ProductName : null),
            Price(order.SubmittedSellerPrice ?? (legacySnapshot ? order.SellerPrice : null),
                order.SubmittedSellerPriceCurrency ?? (legacySnapshot ? order.SellerPriceCurrency : null)),
            order.Quantity, order.SubmittedColor, order.SubmittedSize, order.Comment);

    internal static OrderProductDto Effective(Order order)
        => order.OverrideQuantity.HasValue
            ? new(order.OverrideProductName, Price(order.OverrideSellerPrice, order.OverrideSellerPriceCurrency),
                order.OverrideQuantity.Value, order.OverrideColor, order.OverrideSize, order.OverrideComment)
            : Submitted(order);

    private static OrderSellerPriceDto? Price(decimal? amount, Currency? currency)
        => amount.HasValue && currency.HasValue ? new(amount.Value, currency.Value) : null;
}
