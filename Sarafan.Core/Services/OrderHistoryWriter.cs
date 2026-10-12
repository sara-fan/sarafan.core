// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
namespace Sarafan.Core.Services;

internal static class OrderHistoryWriter
{
    internal static OrderHistoryEvent Create(Order order, DateTimeOffset at, OrderHistoryKind kind, OrderHistoryArea areas,
        OrderHistoryActor actorType, int? actorId, string actorName, OrderHistoryEvidence evidence,
        OrderProductAuditEvent? product = null, OrderPricingSnapshot? pricing = null) => new()
        {
            Order = order,
            At = at,
            Kind = kind,
            Areas = areas,
            ActorType = actorType,
            ActorId = actorId,
            ActorName = actorName,
            ProductAudit = product,
            PricingSnapshot = pricing,
            Payload = JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
}
