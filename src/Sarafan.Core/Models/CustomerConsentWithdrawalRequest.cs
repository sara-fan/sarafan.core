// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class CustomerConsentWithdrawalRequest
{
    public int CustomerId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public bool Processed { get; set; }
}
