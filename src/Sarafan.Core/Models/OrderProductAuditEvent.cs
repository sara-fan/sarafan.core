// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class OrderProductAuditEvent
{
    public long Id { get; set; }
    public long OrderId { get; set; }
    public int ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string Before { get; set; }
    public required string After { get; set; }
    public long UsdRateId { get; set; }
    public long EurRateId { get; set; }
}
