// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

internal static class LogibooksMapping
{
    internal const int Version = 1;
    internal const int MaximumOrders = 1000;
    internal const int MaximumLabel = 200;
    internal const int MaximumCommand = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal static readonly string[] DemoDefaults = ["Импорт (40), автотранспорт (1), CPT (2)",
        "Вес единицы — 1,000 кг", "ТН ВЭД — 1000000000", "Все страны — 643", "Отправитель — Sarafan Demo"];
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Number(Order order) => $"{order.Customer.OrderCode}-{order.CustomerOrderNumber}";
    internal static LogibooksRegisterInput? NormalizeRegister(LogibooksRegisterInput? value)
    {
        static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (value is null) return null;
        var normalized = new LogibooksRegisterInput(Optional(value.DealNumber), Optional(value.CustomerOrderNumber),
            Optional(value.InvoiceNumber), value.InvoiceDate);
        // Empty extensions retain the original command and payload bytes for version-one replay.
        return normalized == new LogibooksRegisterInput() ? null : normalized;
    }
    internal static bool HasPrice(decimal? price, Currency? currency)
        => currency == Currency.Usd && price is > 0 and <= 99999999.99m && decimal.Round(price.Value, 2) == price;
    internal static string? Ineligible(OrderStatus status, decimal? price, Currency? currency, bool blocking)
        => blocking ? "Заказ уже включён в таможенный реестр или ожидает результата его создания."
            : status != OrderStatus.Paid ? "Выберите оплаченный заказ."
            : !HasPrice(price, currency) ? "Требуется положительная цена единицы товара в USD." : null;
    internal static string Label(Guid id, DateTimeOffset now) => $"Sarafan-{TimeZoneInfo.ConvertTime(now,
        ScheduledJobTimeZones.Resolve(ConsentCalendar.TimeZoneId)).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{id.ToString("N")[..8]}";

    internal static LogibooksGoods Goods(Order order)
    {
        var checkout = order.CheckoutData is null ? null : Read<OrderCheckoutDto>(order.CheckoutData);
        var profile = checkout?.Profile;
        var structured = checkout?.Delivery.RouteAlias == "courier" && profile is not null
            && string.Join(", ", profile.PostalCode, profile.City, profile.Address) == checkout.Delivery.Destination;
        return new(Number(order), string.IsNullOrWhiteSpace(order.ProductName) ? $"Товар по заказу {Number(order)}" : order.ProductName,
            order.SellerPrice!.Value, order.Quantity, order.SourceUrl, order.Customer.Phone,
            profile?.LastName, profile?.FirstName, profile?.Patronymic, profile?.Email,
            profile?.PassportSeries, profile?.PassportNumber, profile?.PassportIssueDate, profile?.PassportIssuedBy, profile?.Inn,
            structured ? profile?.PostalCode : null, structured ? profile?.City : null,
            structured ? profile?.Address : checkout?.Delivery.Destination);
    }
}
