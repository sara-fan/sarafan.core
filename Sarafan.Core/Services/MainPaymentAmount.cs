// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

internal static class MainPaymentAmount
{
    internal static decimal? From(CustomerPricingDto pricing)
        => pricing.ValidUntil.HasValue && pricing.TotalRub.HasValue
            ? pricing.TotalRub.Value + (pricing.DomesticDeliveryRub ?? 0m) : null;
}
