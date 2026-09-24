// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class IanaTldCatalog
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;
    public required string Version { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset SourceUpdatedAt { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
    public required string ContentSha256 { get; set; }
    public required string[] TopLevelDomains { get; set; }
}
