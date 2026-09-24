// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class BackofficeRole
{
    public required string Code { get; set; }
    public required string DisplayName { get; set; }

    public ICollection<BackofficeUserRole> UserRoles { get; set; } = [];
}
