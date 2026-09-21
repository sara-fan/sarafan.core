// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class ServiceCatalogueService(
    AppDbContext database,
    TimeProvider clock,
    ILogger<ServiceCatalogueService> logger)
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<ServiceCatalogueOpsDto> OperationsAsync(string[] roles, CancellationToken token)
        => Run(nameof(OperationsAsync), () => Task.FromResult(ServiceCatalogueRules.Operations(roles)), token, roles);

    public Task<ServiceCatalogueListDto> ListAsync(string[] roles, CancellationToken token)
        => Run(nameof(ListAsync), async () =>
        {
            RequireView(roles);
            var rows = await database.ServiceCatalogueEntries.AsNoTracking()
                .OrderBy(item => item.Service)
                .ThenByDescending(item => item.AvailableFrom)
                .ThenBy(item => item.Id)
                .ToArrayAsync(token);
            return new ServiceCatalogueListDto(rows.Select(ToDto).ToArray());
        }, token, roles);

    public Task<ServiceCatalogueEntryDto> GetAsync(long id, string[] roles, CancellationToken token)
        => Run(nameof(GetAsync), async () =>
        {
            RequireView(roles);
            var row = await database.ServiceCatalogueEntries.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, token)
                ?? throw new ServiceException(404, "service_catalogue_entry_not_found");
            return ToDto(row);
        }, token, new { id, roles });

    public Task<ServiceCatalogueEntryDto> CreateAsync(
        ServiceCatalogueWriteRequest request,
        int actorId,
        string[] roles,
        CancellationToken token)
        => Run(nameof(CreateAsync), async () =>
        {
            RequireManage(roles);
            var prepared = ServiceCatalogueRules.Prepare(request, update: false);
            var operations = AppDatabaseOperations.For(database);
            await using var transaction = await operations.BeginTransactionAsync(database, token);
            await operations.LockServiceCatalogueMutationsAsync(database, token);
            var now = CurrentTimestamp();
            var actorName = await ActorNameAsync(actorId, token);
            await RequireNoOverlapAsync(prepared, null, token);
            var entry = new ServiceCatalogueEntry(prepared.Service, prepared.PriceMethod, prepared.Percentage,
                prepared.MinimumAmount, prepared.MaximumAmount, prepared.Amount, prepared.Currency,
                prepared.AvailableFrom, prepared.AvailableBy, now);
            database.ServiceCatalogueEntries.Add(entry);
            await SaveMutationAsync(operations, token);
            var after = Snapshot(entry);
            database.ServiceCatalogueAuditEvents.Add(Audit(entry.Id, entry.Service,
                ServiceCatalogueAuditAction.Created, actorId, actorName, now, null, after));
            await SaveMutationAsync(operations, token);
            await transaction.CommitAsync(token);
            return ToDto(entry);
        }, token, new { request, actorId, roles });

    public Task<ServiceCatalogueEntryDto> UpdateAsync(
        long id,
        ServiceCatalogueWriteRequest request,
        int actorId,
        string[] roles,
        CancellationToken token)
        => Run(nameof(UpdateAsync), async () =>
        {
            RequireManage(roles);
            var prepared = ServiceCatalogueRules.Prepare(request, update: true);
            var requestedVersion = ServiceCatalogueRules.RequireVersion(request.Version);
            var operations = AppDatabaseOperations.For(database);
            await using var transaction = await operations.BeginTransactionAsync(database, token);
            await operations.LockServiceCatalogueMutationsAsync(database, token);
            var now = CurrentTimestamp();
            var actorName = await ActorNameAsync(actorId, token);
            var entry = await FindForMutationAsync(id, requestedVersion, token);
            await RequireNoOverlapAsync(prepared, id, token);
            var before = Snapshot(entry);
            entry.Update(prepared.Service, prepared.PriceMethod, prepared.Percentage,
                prepared.MinimumAmount, prepared.MaximumAmount, prepared.Amount, prepared.Currency,
                prepared.AvailableFrom, prepared.AvailableBy, now);
            var after = Snapshot(entry);
            database.ServiceCatalogueAuditEvents.Add(Audit(entry.Id, entry.Service,
                ServiceCatalogueAuditAction.Updated, actorId, actorName, now, before, after));
            await SaveMutationAsync(operations, token);
            await transaction.CommitAsync(token);
            return ToDto(entry);
        }, token, new { id, request, actorId, roles });

    public Task DeleteAsync(long id, Guid? version, int actorId, string[] roles, CancellationToken token)
        => Run(nameof(DeleteAsync), async () =>
        {
            RequireManage(roles);
            var requestedVersion = ServiceCatalogueRules.RequireVersion(version);
            var operations = AppDatabaseOperations.For(database);
            await using var transaction = await operations.BeginTransactionAsync(database, token);
            await operations.LockServiceCatalogueMutationsAsync(database, token);
            var now = CurrentTimestamp();
            var actorName = await ActorNameAsync(actorId, token);
            var entry = await FindForMutationAsync(id, requestedVersion, token);
            var before = Snapshot(entry);
            database.ServiceCatalogueAuditEvents.Add(Audit(entry.Id, entry.Service,
                ServiceCatalogueAuditAction.Deleted, actorId, actorName, now, before, null));
            database.ServiceCatalogueEntries.Remove(entry);
            await SaveMutationAsync(operations, token);
            await transaction.CommitAsync(token);
        }, token, new { id, version, actorId, roles });

    public Task<ServiceCatalogueAuditPageDto> AuditAsync(
        ServiceKind? service,
        ServiceCatalogueAuditAction? action,
        long? entryId,
        string? search,
        int page,
        int pageSize,
        string sortBy,
        string sortOrder,
        string[] roles,
        CancellationToken token)
        => Run(nameof(AuditAsync), async () =>
        {
            RequireView(roles);
            search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            var sortByKey = sortBy?.Trim().ToLowerInvariant();
            var sortOrderKey = sortOrder?.Trim().ToLowerInvariant();
            if (service is { } serviceValue && !Enum.IsDefined(serviceValue)
                || action is { } actionValue && !Enum.IsDefined(actionValue)
                || entryId is <= 0
                || page < 1
                || pageSize is < 1 or > ServiceCatalogueRules.AuditPageSizeMaximum
                || search?.Length > ServiceCatalogueRules.AuditSearchMaxLength
                || sortByKey is not ("timestamp" or "service" or "action" or "actor")
                || sortOrderKey is not ("asc" or "desc"))
                throw new ServiceException(400, "invalid_service_catalogue_audit_filter");

            var searchedEntryId = long.TryParse(search, out var parsedEntryId) && parsedEntryId > 0
                ? parsedEntryId
                : (long?)null;
            var query = database.ServiceCatalogueAuditEvents.AsNoTracking().Where(item =>
                (service == null || item.Service == service)
                && (action == null || item.Action == action)
                && (entryId == null || item.EntryId == entryId));
            if (search is not null)
                query = AppDatabaseOperations.For(database)
                    .ApplyServiceCatalogueAuditSearch(query, search, searchedEntryId);

            var total = await query.CountAsync(token);
            var descending = sortOrderKey == "desc";
            var ordered = (sortByKey, descending) switch
            {
                ("timestamp", false) => query.OrderBy(item => item.At).ThenBy(item => item.Id),
                ("timestamp", true) => query.OrderByDescending(item => item.At).ThenByDescending(item => item.Id),
                ("service", false) => query.OrderBy(item => item.Service).ThenByDescending(item => item.At).ThenByDescending(item => item.Id),
                ("service", true) => query.OrderByDescending(item => item.Service).ThenByDescending(item => item.At).ThenByDescending(item => item.Id),
                ("action", false) => query.OrderBy(item => item.Action).ThenByDescending(item => item.At).ThenByDescending(item => item.Id),
                ("action", true) => query.OrderByDescending(item => item.Action).ThenByDescending(item => item.At).ThenByDescending(item => item.Id),
                ("actor", false) => query.OrderBy(item => item.ActorName).ThenByDescending(item => item.At).ThenByDescending(item => item.Id),
                _ => query.OrderByDescending(item => item.ActorName).ThenByDescending(item => item.At).ThenByDescending(item => item.Id)
            };
            var offset = (long)(page - 1) * pageSize;
            var rows = offset > int.MaxValue
                ? []
                : await ordered.Skip((int)offset).Take(pageSize).ToArrayAsync(token);
            var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
            return new ServiceCatalogueAuditPageDto
            {
                Items = rows.Select(ToAuditDto).ToArray(),
                Pagination = new PaginationInfo
                {
                    CurrentPage = page,
                    PageSize = pageSize,
                    TotalCount = total,
                    TotalPages = totalPages,
                    HasNextPage = page < totalPages,
                    HasPreviousPage = page > 1
                },
                Sorting = new SortingInfo { SortBy = sortByKey!, SortOrder = sortOrderKey! },
                Search = search,
                Service = service,
                Action = action,
                EntryId = entryId
            };
        }, token, new { service, action, entryId, search, page, pageSize, sortBy, sortOrder, roles });

    private Task<T> Run<T>(string method, Func<Task<T>> action, CancellationToken token, object? input)
        => OperationLogging.RunAsync(logger, $"{typeof(ServiceCatalogueService).FullName}.{method}",
            () => LogValueSummary.Inputs(("request", input)), action, token);

    private Task Run(string method, Func<Task> action, CancellationToken token, object? input)
        => OperationLogging.RunAsync(logger, $"{typeof(ServiceCatalogueService).FullName}.{method}",
            () => LogValueSummary.Inputs(("request", input)), action, token);

    private async Task<ServiceCatalogueEntry> FindForMutationAsync(long id, Guid version, CancellationToken token)
    {
        var entry = await database.ServiceCatalogueEntries.SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new ServiceException(404, "service_catalogue_entry_not_found");
        if (entry.Version != version)
            throw new ServiceException(409, "service_catalogue_update_conflict")
            {
                Errors = new Dictionary<string, string[]> { ["version"] = ["Тариф был изменён. Обновите данные и повторите действие."] }
            };
        return entry;
    }

    private async Task RequireNoOverlapAsync(PreparedServiceCatalogueEntry prepared, long? excludedId, CancellationToken token)
    {
        var overlaps = await database.ServiceCatalogueEntries.AnyAsync(item =>
            item.Service == prepared.Service
            && (excludedId == null || item.Id != excludedId)
            && (prepared.AvailableBy == null || item.AvailableFrom <= prepared.AvailableBy)
            && (item.AvailableBy == null || item.AvailableBy >= prepared.AvailableFrom), token);
        if (overlaps) throw Overlap();
    }

    private async Task SaveMutationAsync(IAppDatabaseOperations operations, CancellationToken token)
    {
        try
        {
            await database.SaveChangesAsync(token);
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            throw new ServiceException(409, "service_catalogue_update_conflict")
            {
                Errors = new Dictionary<string, string[]> { ["version"] = ["Тариф был изменён. Обновите данные и повторите действие."] }
            };
        }
        catch (DbUpdateException exception) when (operations.IsServiceCataloguePeriodCollision(exception))
        {
            database.ChangeTracker.Clear();
            throw Overlap();
        }
    }

    private async Task<string> ActorNameAsync(int actorId, CancellationToken token)
    {
        var actor = await database.BackofficeUsers.AsNoTracking().Where(item => item.Id == actorId)
            .Select(item => new { item.LastName, item.FirstName, item.Patronymic })
            .SingleOrDefaultAsync(token)
            ?? throw new ServiceException(404, "backoffice_user_not_found");
        return string.Join(' ', new[] { actor.LastName, actor.FirstName, actor.Patronymic }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private DateTimeOffset CurrentTimestamp()
    {
        var utc = clock.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private static void RequireView(string[] roles)
        => BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ViewServiceCatalogue);

    private static void RequireManage(string[] roles)
        => BackofficeAuthorization.RequireAllowed(roles, BackofficeAction.ManageServiceCatalogue);

    private static ServiceException Overlap()
        => new(409, "service_catalogue_period_overlap")
        {
            Errors = new Dictionary<string, string[]>
            {
                ["service"] = ["Для услуги уже существует тариф с пересекающимся периодом."],
                ["availableFrom"] = ["Выберите период, который не пересекается с другим тарифом этой услуги."],
                ["availableBy"] = ["Выберите период, который не пересекается с другим тарифом этой услуги."]
            }
        };

    private static ServiceCatalogueEntryDto ToDto(ServiceCatalogueEntry entry)
        => new(entry.Id, entry.Service, entry.PriceMethod, entry.Percentage, entry.MinimumAmount,
            entry.MaximumAmount, entry.Amount, entry.Currency, entry.AvailableFrom, entry.AvailableBy,
            entry.CreatedAt, entry.UpdatedAt, entry.Version);

    private static ServiceCatalogueSnapshotDto Snapshot(ServiceCatalogueEntry entry)
        => new(entry.Id, entry.Service, entry.PriceMethod, entry.Percentage, entry.MinimumAmount,
            entry.MaximumAmount, entry.Amount, entry.Currency, entry.AvailableFrom, entry.AvailableBy,
            entry.CreatedAt, entry.UpdatedAt, entry.Version);

    private static ServiceCatalogueAuditEvent Audit(
        long entryId,
        ServiceKind service,
        ServiceCatalogueAuditAction action,
        int actorId,
        string actorName,
        DateTimeOffset at,
        ServiceCatalogueSnapshotDto? before,
        ServiceCatalogueSnapshotDto? after)
        => new()
        {
            EntryId = entryId,
            Service = service,
            Action = action,
            ActorId = actorId,
            ActorName = actorName,
            At = at,
            Before = before is null ? null : JsonSerializer.Serialize(before, SnapshotJson),
            After = after is null ? null : JsonSerializer.Serialize(after, SnapshotJson)
        };

    private static ServiceCatalogueAuditDto ToAuditDto(ServiceCatalogueAuditEvent item)
        => new(item.Id, item.EntryId, item.Service, item.Action, item.ActorId, item.ActorName, item.At,
            DeserializeSnapshot(item.Before), DeserializeSnapshot(item.After));

    private static ServiceCatalogueSnapshotDto? DeserializeSnapshot(string? value)
        => value is null ? null : JsonSerializer.Deserialize<ServiceCatalogueSnapshotDto>(value, SnapshotJson)
            ?? throw new InvalidOperationException("Service catalogue audit snapshot is empty.");
}
