// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;

namespace Sarafan.Core.RestModels;

public sealed class ServiceCatalogueWriteRequest
{
    public ServiceKind? Service { get; set; }
    public PriceMethod? PriceMethod { get; set; }
    public decimal? Percentage { get; set; }
    public decimal? MinimumAmount { get; set; }
    public decimal? MaximumAmount { get; set; }
    public decimal? Amount { get; set; }
    public Currency? Currency { get; set; }
    public DateOnly? AvailableFrom { get; set; }
    public DateOnly? AvailableBy { get; set; }
    public Guid? Version { get; set; }
}

public sealed record DeleteServiceCatalogueEntryRequest(Guid? Version);

public sealed record ServiceCatalogueEntryDto(
    long Id,
    ServiceKind Service,
    PriceMethod PriceMethod,
    decimal? Percentage,
    decimal? MinimumAmount,
    decimal? MaximumAmount,
    decimal? Amount,
    Currency? Currency,
    DateOnly AvailableFrom,
    DateOnly? AvailableBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid Version);

public sealed record ServiceCatalogueListDto(ServiceCatalogueEntryDto[] Items);

public sealed record ServiceCatalogueActionsDto(bool View, bool Create, bool Edit, bool Delete, bool Audit);

public sealed record ServiceCatalogueLimitsDto(
    decimal MaximumAmount,
    int AmountDecimalPlaces,
    decimal MaximumPercentage,
    int PercentageDecimalPlaces,
    int AuditSearchMaxLength,
    int AuditPageSizeMaximum);

public sealed record ServiceCatalogueOpsDto(
    EnumOpsItemDto[] Services,
    EnumOpsItemDto[] PriceMethods,
    EnumOpsItemDto[] Currencies,
    EnumOpsItemDto[] AuditActions,
    ServiceCatalogueLimitsDto Limits,
    ServiceCatalogueActionsDto Actions,
    string AvailabilityTimeZone);

public sealed record ServiceCatalogueSnapshotDto(
    long Id,
    ServiceKind Service,
    PriceMethod PriceMethod,
    decimal? Percentage,
    decimal? MinimumAmount,
    decimal? MaximumAmount,
    decimal? Amount,
    Currency? Currency,
    DateOnly AvailableFrom,
    DateOnly? AvailableBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid Version);

public sealed record ServiceCatalogueAuditDto(
    long Id,
    long EntryId,
    ServiceKind Service,
    ServiceCatalogueAuditAction Action,
    int ActorId,
    string ActorName,
    DateTimeOffset At,
    ServiceCatalogueSnapshotDto? Before,
    ServiceCatalogueSnapshotDto? After);

public sealed class ServiceCatalogueAuditPageDto : PagedResult<ServiceCatalogueAuditDto>
{
    public ServiceKind? Service { get; init; }
    public ServiceCatalogueAuditAction? Action { get; init; }
    public long? EntryId { get; init; }
}
