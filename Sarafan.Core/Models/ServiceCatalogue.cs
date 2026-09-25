// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum PriceMethod
{
    Percent = 0,
    Fixed = 100,
    Manual = 200,
    Auto = 300,
    Stepped = 400
}

public static class PriceMethodExtensions
{
    public static string GetDisplayName(this PriceMethod method) => method switch
    {
        PriceMethod.Percent => "Процент от цены товара",
        PriceMethod.Fixed => "Фиксированная стоимость",
        PriceMethod.Manual => "Ввод вручную",
        PriceMethod.Auto => "Автоматическое определение",
        PriceMethod.Stepped => "Стоимость по диапазонам",

        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null)
    };

    public static string GetRouteAlias(this PriceMethod method) => method switch
    {
        PriceMethod.Percent => "percent",
        PriceMethod.Fixed => "fixed",
        PriceMethod.Manual => "manual",
        PriceMethod.Auto => "auto",
        PriceMethod.Stepped => "stepped",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null)
    };
}

public enum ServiceKind
{
    Product = 0,
    UsWarehouseExpenses = 100,
    InternationalDelivery = 200,
    DomesticDelivery = 300,
    ServiceCommission = 400,
    WarehousePhoto = 500,
    ProductInspection = 600,
    ShipmentInsurance = 700
}

public static class ServiceKindExtensions
{
    public static string GetDisplayName(this ServiceKind service) => service switch
    {
        ServiceKind.Product => "Выкуп товара",
        ServiceKind.UsWarehouseExpenses => "Доставка до склада в США",
        ServiceKind.InternationalDelivery => "Доставка из США в Россию",
        ServiceKind.DomesticDelivery => "Доставка по России",
        ServiceKind.ServiceCommission => "Комиссия сервиса",
        ServiceKind.WarehousePhoto => "Фото товара на складе в США",
        ServiceKind.ProductInspection => "Проверка товара",
        ServiceKind.ShipmentInsurance => "Страхование отправления",
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, null)
    };

    public static string GetRouteAlias(this ServiceKind service) => service switch
    {
        ServiceKind.Product => "product",
        ServiceKind.UsWarehouseExpenses => "us-warehouse-delivery",
        ServiceKind.InternationalDelivery => "international-delivery",
        ServiceKind.DomesticDelivery => "domestic-delivery",
        ServiceKind.ServiceCommission => "service-commission",
        ServiceKind.WarehousePhoto => "warehouse-photo",
        ServiceKind.ProductInspection => "product-inspection",
        ServiceKind.ShipmentInsurance => "shipment-insurance",
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, null)
    };
}

public enum ServiceCatalogueAuditAction
{
    Created = 0,
    Updated = 100,
    Deleted = 200
}

public static class ServiceCatalogueAuditActionExtensions
{
    public static string GetDisplayName(this ServiceCatalogueAuditAction action) => action switch
    {
        ServiceCatalogueAuditAction.Created => "Создано",
        ServiceCatalogueAuditAction.Updated => "Изменено",
        ServiceCatalogueAuditAction.Deleted => "Удалено",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    public static string GetRouteAlias(this ServiceCatalogueAuditAction action) => action switch
    {
        ServiceCatalogueAuditAction.Created => "created",
        ServiceCatalogueAuditAction.Updated => "updated",
        ServiceCatalogueAuditAction.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };
}

public sealed class ServiceCatalogueEntry
{
    private ServiceCatalogueEntry()
    {
    }

    internal ServiceCatalogueEntry(
        ServiceKind service,
        PriceMethod priceMethod,
        decimal? percentage,
        decimal? minimumAmount,
        decimal? maximumAmount,
        decimal? amount,
        Currency? currency,
        DateOnly? availableFrom,
        DateOnly? availableBy,
        DateTimeOffset now,
        Currency? intervalCurrency = null,
        PriceBand[]? bands = null)
    {
        Service = service;
        PriceMethod = priceMethod;
        Percentage = percentage;
        MinimumAmount = minimumAmount;
        MaximumAmount = maximumAmount;
        Amount = amount;
        Currency = currency;
        IntervalCurrency = intervalCurrency;
        Bands = bands?.ToArray() ?? [];
        AvailableFrom = availableFrom;
        AvailableBy = availableBy;
        CreatedAt = NormalizeTimestamp(now);
        UpdatedAt = CreatedAt;
    }

    public long Id { get; private set; }
    public ServiceKind Service { get; private set; }
    public PriceMethod PriceMethod { get; private set; }
    public decimal? Percentage { get; private set; }
    public decimal? MinimumAmount { get; private set; }
    public decimal? MaximumAmount { get; private set; }
    public decimal? Amount { get; private set; }
    public Currency? Currency { get; private set; }
    public Currency? IntervalCurrency { get; private set; }
    public PriceBand[] Bands { get; private set; } = [];
    public DateOnly? AvailableFrom { get; private set; }
    public DateOnly? AvailableBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid Version { get; private set; } = Guid.NewGuid();

    internal void Update(
        ServiceKind service,
        PriceMethod priceMethod,
        decimal? percentage,
        decimal? minimumAmount,
        decimal? maximumAmount,
        decimal? amount,
        Currency? currency,
        DateOnly? availableFrom,
        DateOnly? availableBy,
        DateTimeOffset now,
        Currency? intervalCurrency = null,
        PriceBand[]? bands = null)
    {
        Service = service;
        PriceMethod = priceMethod;
        Percentage = percentage;
        MinimumAmount = minimumAmount;
        MaximumAmount = maximumAmount;
        Amount = amount;
        Currency = currency;
        IntervalCurrency = intervalCurrency;
        Bands = bands?.ToArray() ?? [];
        AvailableFrom = availableFrom;
        AvailableBy = availableBy;
        var timestamp = NormalizeTimestamp(now);
        UpdatedAt = timestamp > UpdatedAt ? timestamp : UpdatedAt.AddMicroseconds(1);
        Version = Guid.NewGuid();
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}

public sealed record PriceBand(
    [property: System.Text.Json.Serialization.JsonRequired] decimal? From,
    [property: System.Text.Json.Serialization.JsonRequired] decimal? By,
    [property: System.Text.Json.Serialization.JsonRequired] decimal Amount);

public sealed class ServiceCatalogueAuditEvent
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public ServiceKind Service { get; set; }
    public ServiceCatalogueAuditAction Action { get; set; }
    public int ActorId { get; set; }
    public BackofficeUser Actor { get; set; } = null!;
    public string ActorName { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
}
