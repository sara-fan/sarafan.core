// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;
namespace Sarafan.Core.RestModels;

public sealed class OrderCheckoutRequest
{
    public DateTimeOffset? ExpectedUpdatedAt { get; set; }
    [Required(ErrorMessage = "Укажите данные получателя.")] public CustomerProfileUpdateRequest? Profile { get; set; }
    public string? Delivery { get; set; }
    public CustomerDeliveryAddress? ExpectedDeliveryAddress { get; set; }
    public CustomerDeliveryAddress? DeliveryAddress { get; set; }
}
public sealed record OrderCheckoutDto(CustomerProfileDto Profile, OrderCheckoutDeliveryDto Delivery);
public sealed record OrderCheckoutDeliveryDto(string RouteAlias, string Name, string Destination);
public sealed record OrderCheckoutDeliveryOptionDto(string RouteAlias, string Name, string DestinationSource, string? Destination)
{
    public static IReadOnlyList<OrderCheckoutDeliveryOptionDto> PilotOptions { get; } = Array.AsReadOnly<OrderCheckoutDeliveryOptionDto>(
    [new("courier", "Курьерская доставка", "customer-profile", null)]);

}
