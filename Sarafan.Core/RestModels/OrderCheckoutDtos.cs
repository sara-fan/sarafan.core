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
}

public sealed record OrderCheckoutDto(CustomerProfileDto Profile, OrderCheckoutDeliveryDto Delivery);

public sealed record OrderCheckoutDeliveryDto(string RouteAlias, string Name, string Destination)
{
    public static IReadOnlyList<OrderCheckoutDeliveryDto> DemoOptions { get; } = Array.AsReadOnly<OrderCheckoutDeliveryDto>(
    [new("courier", "Курьерская доставка", "Тестовый адрес: Москва, Тестовая улица, 1"),
     new("pickup", "Пункт выдачи", "Тестовый ПВЗ: Москва, Тестовая улица, 2")]);
}
