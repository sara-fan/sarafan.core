// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class LegalDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public LegalDocumentKind Kind { get; set; }
    public string Locale { get; set; } = "ru";
    public string Title { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public byte[] Source { get; set; } = [];
    public string Html { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string RendererVersion { get; set; } = "";
    public CookieCategory[] CookieCategories { get; set; } = [];
    public int CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
}

public sealed class LegalDocumentAuditEvent
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public int ActorId { get; set; }
    public BackofficeUser BackofficeUser { get; set; } = null!;
    public string Action { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public LegalDocumentKind Kind { get; set; }
    public string Locale { get; set; } = "ru";
    public string Title { get; set; } = "";
    public string DisplayVersion { get; set; } = "";
    public DateTimeOffset EffectiveAt { get; set; }
    public string SourceHash { get; set; } = "";
    public string ContentHash { get; set; } = "";
}
