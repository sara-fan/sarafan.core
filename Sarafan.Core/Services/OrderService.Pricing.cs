// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

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
    private static readonly JsonSerializerOptions PricingJson = new(JsonSerializerDefaults.Web);
    internal const int QuoteValidityHours = 24;

    public Task<OrderPricingOpsDto> PricingOperationsAsync(string[] roles, CancellationToken token)
        => PricingRun(nameof(PricingOperationsAsync), () => Task.FromResult(new OrderPricingOpsDto(
            ServiceCatalogueRules.Operations(roles),
            [new(0, "Рассчитана", "calculated"), new(100, "Не рассчитана", "not-calculated"), new(200, "Не применяется", "not-applicable")],
            QuoteValidityHours, BackofficeAuthorization.IsAllowed(roles, BackofficeAction.ManageOrderPricing))), token);

    public Task<OrderPricingDto> GetPricingAsync(string orderNumber, string[] roles, CancellationToken token)
        => PricingRun(nameof(GetPricingAsync), async () =>
        {
            BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManualQuotes);
            var order = await FindPublicOrder(orderNumber, token);
            return await PricingDetailsAsync(order, roles, timeProvider.GetUtcNow(), token);
        }, token);

    public Task<OrderPricingDto> UpdatePricingAsync(string orderNumber, OrderPricingWriteRequest request,
        int actorId, string[] roles, CancellationToken token)
        => PricingRun(nameof(UpdatePricingAsync), () => MutatePricingAsync(orderNumber, request.ExpectedUpdatedAt,
            request.Inputs, false, actorId, roles, token), token);

    public Task<OrderPricingDto> ConfirmPricingAsync(string orderNumber, ConfirmOrderPricingRequest request,
        int actorId, string[] roles, CancellationToken token)
        => PricingRun(nameof(ConfirmPricingAsync), () => MutatePricingAsync(orderNumber, request.ExpectedUpdatedAt,
            null, true, actorId, roles, token), token);

    private Task<T> PricingRun<T>(string method, Func<Task<T>> action, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(OrderService).FullName}.{method}",
            () => LogValueSummary.Inputs(("pricing", "[redacted]")), action, token);

    private async Task<OrderPricingDto> MutatePricingAsync(string number, DateTimeOffset? expectedUpdatedAt,
        OrderPricingInputs? inputs, bool confirm, int actorId, string[] roles, CancellationToken token)
    {
        BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManageOrderPricing);
        var operations = AppDatabaseOperations.For(database);
        await using var transaction = await operations.BeginTransactionAsync(database, token);
        await operations.LockServiceCatalogueMutationsAsync(database, token);
        var order = await FindPublicOrder(number, token);
        if (order.Status != OrderStatus.UnderReview) throw new ServiceException(409, "order_not_editable");
        if (expectedUpdatedAt != order.UpdatedAt) throw new ServiceException(409, "order_update_conflict");
        var utc = timeProvider.GetUtcNow().ToUniversalTime();
        var now = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
        var actor = await database.BackofficeUsers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == actorId, token)
            ?? throw new ServiceException(404, "backoffice_user_not_found");
        var name = string.Join(' ', new[] { actor.LastName, actor.FirstName, actor.Patronymic }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var latest = await LatestPricingAsync(order.Id, token);
        OrderPriceCalculationDto calculation;
        if (confirm)
        {
            // Confirmation freezes the displayed saved calculation, not today's possibly changed tariffs/rates.
            if (latest is null) throw new ServiceException(409, "order_pricing_unavailable");
            calculation = ReadCalculation(latest);
            if (calculation.TotalRub is null) throw new ServiceException(409, "order_pricing_unavailable");
        }
        else
        {
            if (inputs is null) throw new ServiceException(400, "invalid_order_pricing");
            OrderPriceCalculator.ValidateInputs(inputs, await OrderPriceCalculator.TariffsAsync(database, now, token));
            var selectedServices = latest is null ? OrderPricingInputs.Empty.SelectedServices : ReadCalculation(latest).Inputs.SelectedServices;
            if (!inputs.SelectedServices.ToHashSet().SetEquals(selectedServices))
                throw new ServiceException(400, "invalid_order_pricing");
            calculation = await OrderPriceCalculator.CalculateAsync(database, order, now, inputs, automaticPrices, token);
        }
        order.UpdatePricing(now, confirm);
        database.OrderPricingSnapshots.Add(new()
        {
            Order = order,
            At = now,
            ValidUntil = confirm ? now.AddHours(QuoteValidityHours) : null,
            ActorId = actorId,
            ActorName = name,
            Payload = JsonSerializer.Serialize(calculation, PricingJson)
        });
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
        return await PricingDetailsAsync(order, roles, now, token);
    }

    private Task<OrderPricingSnapshot?> LatestPricingAsync(long orderId, CancellationToken token)
        => database.OrderPricingSnapshots.AsNoTracking().Where(row => row.OrderId == orderId)
            .OrderByDescending(row => row.Id).FirstOrDefaultAsync(token);

    private async Task<OrderPricingDto> PricingDetailsAsync(Order order, string[] roles, DateTimeOffset now, CancellationToken token)
    {
        var rows = await database.OrderPricingSnapshots.AsNoTracking().Where(row => row.OrderId == order.Id)
            .OrderByDescending(row => row.Id).Take(100).ToArrayAsync(token);
        var latest = rows.FirstOrDefault();
        var calculation = latest is null
            ? await OrderPriceCalculator.CalculateAsync(database, order, now, OrderPricingInputs.Empty, automaticPrices, token)
            : ReadCalculation(latest);
        var editable = order.Status == OrderStatus.UnderReview && BackofficeAuthorization.IsAllowed(roles, BackofficeAction.ManageOrderPricing);
        return new($"{order.Customer.OrderCode}-{order.CustomerOrderNumber}", order.UpdatedAt, editable,
            editable && latest is not null && calculation.TotalRub is not null,
            latest?.ValidUntil is not null, latest?.ValidUntil is { } until && now >= until, latest?.ValidUntil,
            calculation, rows.Select(row => new OrderPricingHistoryDto(row.Id, row.At, row.ValidUntil, row.ActorId, row.ActorName, ReadCalculation(row))).ToArray(),
            (await OrderPriceCalculator.TariffsAsync(database, now, token)).Select(ServiceCatalogueService.ToDto).ToArray());
    }

    private async Task AddPricingSnapshotAsync(Order order, DateTimeOffset now, OrderPricingInputs inputs,
        int? actorId, string? actorName, CancellationToken token)
    {
        var calculation = await OrderPriceCalculator.CalculateAsync(database, order, now, inputs, automaticPrices, token);
        database.OrderPricingSnapshots.Add(new()
        {
            Order = order,
            At = now,
            ActorId = actorId,
            ActorName = actorName,
            Payload = JsonSerializer.Serialize(calculation, PricingJson)
        });
    }

    private static OrderPriceCalculationDto ReadCalculation(OrderPricingSnapshot snapshot)
        => JsonSerializer.Deserialize<OrderPriceCalculationDto>(snapshot.Payload, PricingJson)
            ?? throw new InvalidOperationException("Pricing snapshot is invalid.");
}
