// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json.Serialization;
using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdvanceOrderRequest(DateTimeOffset? ExpectedUpdatedAt, OrderStatus Status);
