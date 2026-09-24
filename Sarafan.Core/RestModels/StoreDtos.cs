// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed class StoreWriteRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? OfficialUrl { get; set; }
    public StoreStatus Status { get; set; } = StoreStatus.Hidden;
    public int DisplayOrder { get; set; }
    public Guid? Version { get; set; }
    public IFormFile? Logo { get; set; }
}

public sealed record DeleteStoreRequest(Guid? Version);
public sealed record StoreListDto<T>(T[] Items);
public sealed record PublicStoreDto(int Id, string Name, string Description, string OfficialUrl, string LogoUrl);
public sealed record StaffStoreDto(int Id, string Name, string Description, string OfficialUrl,
    StoreStatus Status, int DisplayOrder, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    Guid Version, string? LogoUrl);
public sealed record StoreActionsDto(bool View, bool Create, bool Edit, bool Delete);
public sealed record StoreStatusDto(StoreStatus Value, string Name, string RouteAlias);
public sealed record StoreLimitsDto(int NameMaxLength, int DescriptionMaxLength, int DescriptionRecommendedLength,
    int OfficialUrlMaxLength, int LogoMaxBytes, string[] LogoContentTypes,
    int MaxPriorityStores, int LogoMaxDimension, int LogoMaxPixels, int LogoMaxFrames, int LogoMaxAnimationPixels, int LogoMaxMetadataBytes);
public sealed record StoreOpsDto(StoreStatusDto[] Statuses, StoreLimitsDto Limits, StoreActionsDto Actions,
    ProductSourceUrlOpsDto OfficialUrlRules);
public sealed record StoreLogoDto(byte[] Content, string ContentType, string ContentSha256);
