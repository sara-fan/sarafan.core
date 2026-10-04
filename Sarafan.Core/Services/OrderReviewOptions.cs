// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Services;

public sealed class OrderReviewOptions
{
    public int DeliveryMinimumDays { get; set; } = 14;
    public int DeliveryMaximumDays { get; set; } = 21;
}
