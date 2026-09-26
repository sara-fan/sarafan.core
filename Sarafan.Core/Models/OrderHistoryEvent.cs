// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum OrderHistoryKind { Created = 0, ProductChanged = 100, Parsed = 200, PriceCalculated = 300, QuoteConfirmed = 400 }
public enum OrderHistoryActor { Customer = 0, Staff = 100, System = 200 }
[Flags]
public enum OrderHistoryArea { Creation = 1, Product = 2, Pricing = 4, Status = 8 }

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
