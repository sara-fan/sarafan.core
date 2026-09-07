// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class LegalDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "";
    public string Locale { get; set; } = "ru";
    public string Title { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public byte[] Source { get; set; } = [];
    public string Html { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string RendererVersion { get; set; } = "";
    public string[] CookieCategories { get; set; } = [];
    public string State { get; set; } = "draft";
    public int CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? EffectiveAt { get; set; }
    public DateTimeOffset? DisposedAt { get; set; }
    public int Revision { get; set; } = 1;
}

public sealed class LegalAuditEvent
{
    public long Id { get; set; }
    public Guid? DocumentId { get; set; }
    public Guid? RightsCaseId { get; set; }
    public int ActorId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset At { get; set; }
}
