// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Services;

// Shared bounded read; domain callers retain their own validation problem identities.
internal static class ImageUpload
{
    internal const int MaxBytes = 2 * 1024 * 1024;
    internal static readonly string[] ContentTypes = ["image/png", "image/jpeg", "image/webp"];

    internal static async Task<(string ContentType, byte[] Content)?> ReadAsync(IFormFile? file, int maxBytes,
        bool allowAnimation, Func<string, ServiceException> invalid, CancellationToken token)
    {
        if (file is null) return null;
        if (file.Length is <= 0 || file.Length > maxBytes) throw invalid("size");
        var type = file.ContentType.ToLowerInvariant();
        if (!ContentTypes.Contains(type, StringComparer.Ordinal)) throw invalid("type");
        await using var stream = file.OpenReadStream();
        var bytes = new byte[(int)file.Length + 1];
        var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken: token);
        if (length != file.Length) throw invalid("size");
        var content = bytes.AsSpan(0, length).ToArray();
        if (!ImageContent.IsValid(type, content, allowAnimation)) throw invalid("content");
        return (type, content);
    }
}
