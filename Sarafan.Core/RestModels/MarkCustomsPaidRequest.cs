// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json.Serialization;
namespace Sarafan.Core.RestModels;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MarkCustomsPaidRequest(DateTimeOffset? ExpectedUpdatedAt);
