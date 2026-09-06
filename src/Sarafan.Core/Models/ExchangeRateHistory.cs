// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class ExchangeRateHistory
{
    public long Id { get; set; }
    public required string Provider { get; set; }
    public required string Source { get; set; }
    public required string BaseCurrency { get; set; }
    public required string QuoteCurrency { get; set; }
    public int Nominal { get; set; }
    public decimal OfficialRate { get; set; }
    public DateOnly SourceEffectiveDate { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
}
