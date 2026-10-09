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
    public Task<OrderDto> GetCheckoutAsync(int customerId, string number, CancellationToken token)
        => PricingRun(nameof(GetCheckoutAsync), () => "customer/order=[redacted]",
            () => GetCoreAsync(customerId, number, token, checkoutPricing: true), token);

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
                        throw new ServiceException(409, "order_checkout_unavailable");
                    if (request.ExpectedUpdatedAt != order.UpdatedAt) throw new ServiceException(409, "order_update_conflict");
                    var before = ReadCheckout(order);
                    var saved = ValidateCheckout(request, order.Customer, now, before);
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
                    if (request.Delivery == "courier" && request.DeliveryAddress is not null)
                    {
                        profile.PostalCode = saved.Profile.PostalCode;
                        profile.City = saved.Profile.City;
                        profile.Address = saved.Profile.Address;
                    }
                    order.Customer.State = CustomerProfileState.Evaluate(profile);
                    order.Customer.UpdatedAt = now;

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

    private static OrderCheckoutDto ValidateCheckout(OrderCheckoutRequest request, Customer customer, DateTimeOffset now, OrderCheckoutDto? before)
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
        if (normalized.Email is null) Invalid("profile.email", "Укажите email.");
        if (normalized.PassportSeries is null) Invalid("profile.passportSeries", "Укажите серию паспорта.");
        if (normalized.PassportNumber is null) Invalid("profile.passportNumber", "Укажите номер паспорта.");
        if (normalized.PassportIssuedBy is null) Invalid("profile.passportIssuedBy", "Укажите, кем выдан паспорт.");
        if (normalized.PassportIssueDate is null) Invalid("profile.passportIssueDate", "Укажите дату выдачи паспорта.");
        if (normalized.Inn is null) Invalid("profile.inn", "Укажите ИНН.");
        var today = ConsentCalendar.LocalDate(now);
        if (normalized.PassportIssueDate > today) Invalid("profile.passportIssueDate", "Дата выдачи не может быть в будущем.");
        var option = OrderCheckoutDeliveryOptionDto.PilotOptions.SingleOrDefault(row => row.RouteAlias == request.Delivery);
        var keepDelivery = option is not null && before?.Delivery.RouteAlias == request.Delivery
            && (request.Delivery != "courier" || (request.ExpectedDeliveryAddress is null && request.DeliveryAddress is null
                && new CustomerDeliveryAddress(before!.Profile.PostalCode, before.Profile.City, before.Profile.Address).Validate().Count == 0));
        var delivery = keepDelivery ? before!.Delivery : null;
        var profileAddress = CustomerDeliveryAddress.From(customer).Normalize();
        var address = request.Delivery == "courier" ? request.DeliveryAddress?.Normalize() ?? profileAddress : profileAddress;
        if (option is not null && !keepDelivery)
        {
            if (option.DestinationSource == "customer-profile")
            {
                var addressErrors = address.Validate();
                if (request.DeliveryAddress is not null)
                    foreach (var (field, messages) in addressErrors) errors["deliveryAddress." + field] = messages;
                else if (addressErrors.Count > 0) Invalid("delivery", "Заполните индекс, город и адрес доставки в профиле.");
                if (addressErrors.Count == 0 && profileAddress != request.ExpectedDeliveryAddress?.Normalize())
                    Invalid("delivery", "Адрес профиля изменился. Обновите адрес доставки и повторите действие.");
            }
            delivery = new(option.RouteAlias, option.Name, option.Destination ?? address.Destination);
        }
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
            Inn = normalized.Inn,
            PostalCode = keepDelivery ? before!.Profile.PostalCode : address.PostalCode,
            City = keepDelivery ? before!.Profile.City : address.City,
            Address = keepDelivery ? before!.Profile.Address : address.Address
        };
        return new(profile, delivery!);
    }
}
