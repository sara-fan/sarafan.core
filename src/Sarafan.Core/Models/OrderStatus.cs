// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum OrderStatus
{
    UnderReview = 0,
    QuoteReady = 100,
    QuoteExpired = 200,

    Paid = 300,                                         // aka InProgress
    PurchasingItem = 310,
    DeliveringToUsWarehouse = 320,
    DeliveredToUsWarehouse = 330,
    DeliveringToRussia = 340,
    DeliveredToRussianWarehouse = 360,
    DeliveringInRussia = 380,

    Received = 400,                                     // aka Completed

    Cancelled = 500
}

public static class OrderStatusExtensions
{
    public static string GetDisplayName(this OrderStatus status) => status switch
    {
        OrderStatus.UnderReview => "На проверке",
        OrderStatus.QuoteReady => "Расчёт готов",
        OrderStatus.QuoteExpired => "Расчёт истёк",
        OrderStatus.Paid => "Оплачен",
        OrderStatus.PurchasingItem => "Выкупаем товар",
        OrderStatus.DeliveringToUsWarehouse => "Доставляем на склад в США",
        OrderStatus.DeliveredToUsWarehouse => "Получен на складе в США",
        OrderStatus.DeliveringToRussia => "Доставляем в Россию",
        OrderStatus.DeliveredToRussianWarehouse => "Получен на складе в России",
        OrderStatus.DeliveringInRussia => "Доставка по России",
        OrderStatus.Received => "Получен",
        OrderStatus.Cancelled => "Отменён",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string GetRouteAlias(this OrderStatus status) => status switch
    {
        OrderStatus.UnderReview => "under_review",
        OrderStatus.QuoteReady => "quote_ready",
        OrderStatus.QuoteExpired => "quote_expired",
        OrderStatus.Paid => "paid",
        OrderStatus.PurchasingItem => "purchasing_item",
        OrderStatus.DeliveringToUsWarehouse => "delivering_to_us_warehouse",
        OrderStatus.DeliveredToUsWarehouse => "delivered_to_us_warehouse",
        OrderStatus.DeliveringToRussia => "delivering_to_russia",
        OrderStatus.DeliveredToRussianWarehouse => "delivered_to_russian_warehouse",
        OrderStatus.DeliveringInRussia => "delivering_in_russia",
        OrderStatus.Received => "received",
        OrderStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static int GetUpperStatusValue(this OrderStatus status) => status switch
    {
        OrderStatus.Paid or
        OrderStatus.PurchasingItem or
        OrderStatus.DeliveringToUsWarehouse or
        OrderStatus.DeliveredToUsWarehouse or
        OrderStatus.DeliveringToRussia or
        OrderStatus.DeliveredToRussianWarehouse or
        OrderStatus.DeliveringInRussia => (int)OrderStatus.Paid,
        OrderStatus.UnderReview or
        OrderStatus.QuoteReady or
        OrderStatus.QuoteExpired or
        OrderStatus.Received or
        OrderStatus.Cancelled => (int)status,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string GetUpperStatusDisplayName(this OrderStatus status) => status switch
    {
        OrderStatus.Paid or
        OrderStatus.PurchasingItem or
        OrderStatus.DeliveringToUsWarehouse or
        OrderStatus.DeliveredToUsWarehouse or
        OrderStatus.DeliveringToRussia or
        OrderStatus.DeliveredToRussianWarehouse or
        OrderStatus.DeliveringInRussia => "Выполняется",
        OrderStatus.Received => "Завершён",
        OrderStatus.UnderReview or
        OrderStatus.QuoteReady or
        OrderStatus.QuoteExpired or
        OrderStatus.Cancelled => status.GetDisplayName(),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string GetUpperStatusRouteAlias(this OrderStatus status) => status switch
    {
        OrderStatus.Paid or
        OrderStatus.PurchasingItem or
        OrderStatus.DeliveringToUsWarehouse or
        OrderStatus.DeliveredToUsWarehouse or
        OrderStatus.DeliveringToRussia or
        OrderStatus.DeliveredToRussianWarehouse or
        OrderStatus.DeliveringInRussia => "in_progress",
        OrderStatus.Received => "completed",
        OrderStatus.UnderReview or
        OrderStatus.QuoteReady or
        OrderStatus.QuoteExpired or
        OrderStatus.Cancelled => status.GetRouteAlias(),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}
