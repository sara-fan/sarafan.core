// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class Customer
{
    public int Id { get; set; }
    public required string Phone { get; set; }
    public CustomerState State { get; set; } = CustomerState.Preliminary;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public CustomerProfile Profile { get; set; } = null!;
    public CustomerPhoto? Photo { get; set; }
    public ICollection<ConsentEvent> ConsentEvents { get; set; } = [];
    public ICollection<RefreshSession> RefreshSessions { get; set; } = [];
}
