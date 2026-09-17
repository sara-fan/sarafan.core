// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class Store
{
    private Store() { }

    // The application service supplies validated fields and its TimeProvider snapshot.
    internal Store(string name, string description, string officialUrl, DateTimeOffset now,
        StoreStatus status = StoreStatus.Hidden, bool showOnHome = false, int displayOrder = 0,
        (string ContentType, byte[] Content)? logo = null)
    {
        Name = name;
        Description = description;
        OfficialUrl = officialUrl;
        CreatedAt = NormalizeTimestamp(now);
        UpdatedAt = CreatedAt;
        Status = status;
        ShowOnHome = showOnHome;
        DisplayOrder = displayOrder;
        if (logo.HasValue) Logo = new StoreLogo(this, logo.Value.ContentType, logo.Value.Content);
    }

    public int Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string OfficialUrl { get; private set; } = string.Empty;
    public StoreStatus Status { get; private set; } = StoreStatus.Hidden;
    public bool ShowOnHome { get; private set; }
    public int DisplayOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid Version { get; private set; } = Guid.NewGuid();
    public StoreLogo? Logo { get; private set; }

    internal void Update(
        string name, string description, string officialUrl, StoreStatus status,
        bool showOnHome, int displayOrder, DateTimeOffset now)
    {
        AdvanceVersion(now);
        Name = name;
        Description = description;
        OfficialUrl = officialUrl;
        Status = status;
        ShowOnHome = showOnHome;
        DisplayOrder = displayOrder;
    }

    // Load Logo before replacing it on a persisted store, retaining the tracked dependent.
    internal void SetLogo(string contentType, byte[] content, DateTimeOffset now)
    {
        AdvanceVersion(now);
        if (Logo is null)
            Logo = new StoreLogo(this, contentType, content);
        else
            Logo.Replace(contentType, content);
    }

    private void AdvanceVersion(DateTimeOffset now)
    {
        var timestamp = NormalizeTimestamp(now);
        UpdatedAt = timestamp > UpdatedAt ? timestamp : UpdatedAt.AddMicroseconds(1);
        Version = Guid.NewGuid();
    }

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
