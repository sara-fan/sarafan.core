// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum PriceComponentState { Calculated = 0, NotCalculated = 100, NotApplicable = 200 }

// Append-only calculation evidence. The payload contains typed components and tariff snapshots,
// not references to mutable catalogue rows. History survives subsequent tariff deletion.
public sealed class OrderPricingSnapshot
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public int? ActorId { get; set; }
    public BackofficeUser? Actor { get; set; }
    public string? ActorName { get; set; }
    public string Payload { get; set; } = string.Empty;
}
