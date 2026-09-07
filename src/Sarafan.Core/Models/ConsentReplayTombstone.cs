// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class ConsentReplayTombstone
{
    public string KeyHash { get; set; } = "";
    public Guid DocumentId { get; set; }
}
