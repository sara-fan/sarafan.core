// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Security.Cryptography;

namespace Sarafan.Core.Models;

public sealed class StoreLogo
{
    private byte[] content = [];

    private StoreLogo() { }

    internal StoreLogo(Store store, string contentType, byte[] content)
    {
        Store = store;
        StoreId = store.Id;
        Replace(contentType, content);
    }

    public int StoreId { get; private set; }
    public Store Store { get; private set; } = null!;
    public string ContentType { get; private set; } = string.Empty;
    public byte[] Content => content.ToArray();
    public string ContentSha256 { get; private set; } = string.Empty;

    internal void Replace(string contentType, byte[] bytes)
    {
        content = bytes.ToArray();
        ContentType = contentType;
        ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
