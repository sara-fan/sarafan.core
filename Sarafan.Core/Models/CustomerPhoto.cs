// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class CustomerPhoto
{
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Content { get; set; }
    public int Size { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
