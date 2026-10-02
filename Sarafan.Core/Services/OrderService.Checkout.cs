// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService
{
    public Task<OrderDto> SaveCheckoutAsync(int customerId, string number, OrderCheckoutRequest request, CancellationToken token)
        => PricingRun(nameof(SaveCheckoutAsync), () => "customer/order/checkout=[redacted]", async () =>
        {
            var (code, sequence) = ParsePublicOrderNumber(number);
            var owned = database.Orders.Where(row => row.CustomerId == customerId
                && row.Customer.OrderCode == code && row.CustomerOrderNumber == sequence);
            var id = await owned.AsNoTracking().Select(row => (long?)row.Id).SingleOrDefaultAsync(token)
                ?? throw new ServiceException(404, "resource_not_found");
            await ExpireQuotesCoreAsync(customerId, token, id);
            try
            {
                await consents.WithOrderConsentsAsync(customerId, async () =>
                {
                    await ConsentTransaction.LockCustomer(database, customerId, token);
                    var order = await owned.Include(row => row.Customer).SingleAsync(token);
                    var now = timeProvider.GetUtcNow();
                    var snapshot = await LatestPricingAsync(order.Id, token);
                    if (order.Status != OrderStatus.QuoteReady || snapshot?.ValidUntil is null || snapshot.ValidUntil <= now)
                        throw new ServiceException(409, "order_not_editable");
                    if (request.ExpectedUpdatedAt != order.UpdatedAt) throw new ServiceException(409, "order_update_conflict");
                    if (order.CheckoutData is not null && JsonSerializer.Deserialize<OrderCheckoutDto>(order.CheckoutData, PricingJson)!.Delivery.RouteAlias != request.Delivery)
                        throw new ServiceException(409, "order_not_editable");
                    var saved = ValidateCheckout(request, order.Customer, now);
                    var profile = order.Customer;
                    profile.LastName = saved.Profile.LastName;
                    profile.FirstName = saved.Profile.FirstName;
                    profile.Patronymic = saved.Profile.Patronymic;
                    profile.Email = saved.Profile.Email;
                    profile.PassportSeries = saved.Profile.PassportSeries;
                    profile.PassportNumber = saved.Profile.PassportNumber;
                    profile.PassportIssueDate = saved.Profile.PassportIssueDate;
                    profile.PassportIssuedBy = saved.Profile.PassportIssuedBy;
                    profile.Inn = saved.Profile.Inn;
                    order.Customer.State = CustomerProfileState.Evaluate(profile);
                    order.Customer.UpdatedAt = now;
                    var before = order.CheckoutData is null ? null : JsonSerializer.Deserialize<OrderCheckoutDto>(order.CheckoutData, PricingJson);
                    order.SaveCheckout(JsonSerializer.Serialize(saved, PricingJson), now);
                    AddHistory(order, order.UpdatedAt, OrderHistoryKind.CheckoutSaved, OrderHistoryArea.Checkout,
                        OrderHistoryActor.Customer, customerId, "Покупатель", null, null,
                        new(4, OrderStatus.QuoteReady, OrderStatus.QuoteReady, null, CheckoutBefore: before, CheckoutAfter: saved));
                    return true;
                }, token);
            }
            catch (ServiceException error) when (error.Code == "consent_conflict")
            {
                database.ChangeTracker.Clear();
                throw new ServiceException(409, "order_update_conflict");
            }
            return await GetCoreAsync(customerId, number, token);
        }, token);

    private static OrderCheckoutDto ValidateCheckout(OrderCheckoutRequest request, Customer customer, DateTimeOffset now)
    {
        var errors = new Dictionary<string, string[]>();
        void Invalid(string field, string message) => errors[field] = [message];
        string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var input = request.Profile ?? new CustomerProfileUpdateRequest();
        var normalized = new CustomerProfileUpdateRequest
        {
            LastName = Text(input.LastName),
            FirstName = Text(input.FirstName),
            Patronymic = Text(input.Patronymic),
            Email = Text(input.Email)?.ToLowerInvariant(),
            PassportSeries = Text(input.PassportSeries),
            PassportNumber = Text(input.PassportNumber),
            PassportIssueDate = input.PassportIssueDate,
            PassportIssuedBy = Text(input.PassportIssuedBy),
            Inn = Text(input.Inn)
        };
        var validation = new List<ValidationResult>();
        Validator.TryValidateObject(normalized, new ValidationContext(normalized), validation, true);
        foreach (var result in validation)
            foreach (var member in result.MemberNames) Invalid("profile." + char.ToLowerInvariant(member[0]) + member[1..], result.ErrorMessage!);
        if (normalized.LastName is null) Invalid("profile.lastName", "Укажите фамилию получателя.");
        if (normalized.FirstName is null) Invalid("profile.firstName", "Укажите имя получателя.");
        var today = ConsentCalendar.LocalDate(now);
        if (normalized.PassportIssueDate > today) Invalid("profile.passportIssueDate", "Дата выдачи не может быть в будущем.");
        var delivery = OrderCheckoutDeliveryDto.DemoOptions.SingleOrDefault(row => row.RouteAlias == request.Delivery);
        if (delivery is null) Invalid("delivery", "Выберите способ доставки.");
        if (errors.Count > 0) throw new ServiceException(400, "validation_failed") { Errors = errors };
        var profile = CustomerProfileDto.From(customer) with
        {
            LastName = normalized.LastName,
            FirstName = normalized.FirstName,
            Patronymic = normalized.Patronymic,
            Email = normalized.Email,
            PassportSeries = normalized.PassportSeries,
            PassportNumber = normalized.PassportNumber,
            PassportIssueDate = normalized.PassportIssueDate,
            PassportIssuedBy = normalized.PassportIssuedBy,
            Inn = normalized.Inn
        };
        return new(profile, delivery!);
    }
}
