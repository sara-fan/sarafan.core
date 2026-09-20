// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

internal static class StoreRules
{
    internal const int NameMaxLength = 200;
    internal const int MaxPriorityStores = 6;
    internal const int LogoMaxBytes = 2 * 1024 * 1024;
    internal const int RequestMaxBytes = LogoMaxBytes + 64 * 1024;

    internal static StoreOpsDto Operations(string[] roles, IanaTldCatalogSnapshot tlds) => new(
        [new(StoreStatus.Hidden, "Скрыт", "hidden"), new(StoreStatus.Active, "Показывается в общем списке", "active"), new(StoreStatus.Priority, "Показывается в общем списке и на главной странице", "priority")],
        new(NameMaxLength, 160, 140, 2048, LogoMaxBytes, ["image/png", "image/jpeg", "image/webp"],
            MaxPriorityStores, StoreImageContent.MaxDimension, StoreImageContent.MaxPixels, StoreImageContent.MaxFrames, StoreImageContent.MaxAnimationPixels,
            StoreImageContent.MaxMetadataBytes),
        new(BackofficeAuthorization.IsAllowed(roles, BackofficeAction.ViewStores),
            BackofficeAuthorization.IsAllowed(roles, BackofficeAction.CreateStore),
            BackofficeAuthorization.IsAllowed(roles, BackofficeAction.EditStore),
            BackofficeAuthorization.IsAllowed(roles, BackofficeAction.DeleteStore)),
        new(ProductSourceUrl.MaximumLength, tlds.Version, tlds.TopLevelDomains));

    internal static (string Name, string Description, string Url) Normalize(StoreWriteRequest request, IanaTldCatalogSnapshot tlds)
    {
        var name = request.Name?.Trim() ?? "";
        var description = request.Description?.Trim() ?? "";
        var url = request.OfficialUrl?.Trim() ?? "";
        if (name.Length is < 1 or > NameMaxLength) throw new ServiceException(400, "invalid_store_name");
        if (description.Length is < 1 or > 160) throw new ServiceException(400, "invalid_store_description");
        if (url.Any(char.IsControl) || url.Contains('\\'))
            throw new ServiceException(400, "invalid_store_url");
        try { url = ProductSourceUrl.Normalize(url, tlds.Values); }
        catch (ServiceException exception) when (exception.Code == "invalid_order_url")
        {
            throw new ServiceException(400, "invalid_store_url");
        }
        if (!Enum.IsDefined(request.Status)) throw new ServiceException(400, "invalid_store_status");
        if (request.DisplayOrder < 0) throw new ServiceException(400, "invalid_store_display_order");
        return (name, description, url);
    }

    internal static Guid RequireVersion(Guid? version)
        => version is null || version == Guid.Empty
            ? throw new ServiceException(400, "invalid_store_version") : version.Value;

    internal static async Task<(string ContentType, byte[] Content)?> ReadLogoAsync(IFormFile? file, CancellationToken token)
    {
        if (file is null) return null;
        if (file.Length is <= 0 or > LogoMaxBytes) throw new ServiceException(400, "invalid_store_logo_size");
        var contentType = file.ContentType.ToLowerInvariant();
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new ServiceException(400, "invalid_store_logo_type");
        // Read at most the advertised length plus one byte; do not trust custom streams or metadata.
        await using var stream = file.OpenReadStream();
        var bytes = new byte[(int)file.Length + 1];
        var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken: token);
        if (length != file.Length) throw new ServiceException(400, "invalid_store_logo_size");
        var content = bytes.AsSpan(0, length).ToArray();
        if (!StoreImageContent.IsValid(contentType, content)) throw new ServiceException(400, "invalid_store_logo_content");
        return (contentType, content);
    }
}
