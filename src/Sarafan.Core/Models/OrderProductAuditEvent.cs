// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum OrderProductAuditKind
{
    Created = 0,
    Parsed = 100,
    StaffCorrected = 200
}

public sealed class OrderProductAuditEvent
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public OrderProductAuditKind Kind { get; set; }
    public int? ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Before { get; set; }
    public required string After { get; set; }
    public long? UsdRateId { get; set; }
    public long? EurRateId { get; set; }
    public Order Order { get; set; } = null!;
}
