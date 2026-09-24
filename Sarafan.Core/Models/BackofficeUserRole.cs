// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class BackofficeUserRole
{
    public int BackofficeUserId { get; set; }
    public BackofficeUser BackofficeUser { get; set; } = null!;
    public required string RoleCode { get; set; }
    public BackofficeRole Role { get; set; } = null!;
}
