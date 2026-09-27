// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum OrderHistoryKind { Created = 0, ProductChanged = 100, Parsed = 200, PriceCalculated = 300, QuoteConfirmed = 400 }
public enum OrderHistoryActor { Customer = 0, Staff = 100, System = 200 }
[Flags]
public enum OrderHistoryArea { Creation = 1, Product = 2, Pricing = 4, Status = 8 }

public static class OrderHistoryKindExtensions
{
    public static string GetDisplayName(this OrderHistoryKind kind) => kind switch
    {
        OrderHistoryKind.Created => "Создание заказа",
        OrderHistoryKind.ProductChanged => "Изменение товара",
        OrderHistoryKind.Parsed => "Распознавание товара",
        OrderHistoryKind.PriceCalculated => "Расчёт стоимости",
        OrderHistoryKind.QuoteConfirmed => "Подтверждение расчёта",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}

public static class OrderHistoryAreaExtensions
{
    public static string GetDisplayName(this OrderHistoryArea area) => area switch
    {
        OrderHistoryArea.Creation => "Создание",
        OrderHistoryArea.Product => "Товар",
        OrderHistoryArea.Pricing => "Стоимость",
        OrderHistoryArea.Status => "Статус",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null)
    };
}

public sealed class OrderHistoryEvent
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public DateTimeOffset At { get; set; }
    public OrderHistoryKind Kind { get; set; }
    public OrderHistoryArea Areas { get; set; }
    public OrderHistoryActor ActorType { get; set; }
    public int? ActorId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public long? ProductAuditId { get; set; }
    public OrderProductAuditEvent? ProductAudit { get; set; }
    public long? PricingSnapshotId { get; set; }
    public OrderPricingSnapshot? PricingSnapshot { get; set; }
    public string Payload { get; set; } = string.Empty;
}
