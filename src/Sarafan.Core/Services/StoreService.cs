// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class StoreService(AppDbContext database, TimeProvider clock, ILogger<StoreService> logger)
{
    private static readonly StringComparer NameComparer = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true);
    private static readonly Expression<Func<Store, StaffStoreDto>> StaffProjection = item => new(
        item.Id, item.Name, item.Description, item.OfficialUrl, item.Status, item.ShowOnHome, item.DisplayOrder,
        item.CreatedAt, item.UpdatedAt, item.Version,
        item.Logo == null ? null : "/api/v1/backoffice/stores/" + item.Id + "/logo?v=" + item.Logo.ContentSha256);

    public Task<StoreListDto<PublicStoreDto>> ListPublicAsync(string sort, bool featured, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(StoreService).FullName}.{nameof(ListPublicAsync)}",
            () => LogValueSummary.Inputs((nameof(sort), sort), (nameof(featured), featured), (nameof(token), token)), async () =>
            {
                if (sort is not ("recommended" or "name-asc" or "name-desc"))
                    throw new ServiceException(400, "invalid_store_sort");
                var query = database.Stores.AsNoTracking().Where(item => item.Status == StoreStatus.Active && item.Logo != null);
                if (featured) query = query.Where(item => item.ShowOnHome);
                query = query.OrderBy(item => item.DisplayOrder).ThenBy(item => item.Id);
                if (featured) query = query.Take(6);
                var items = await query.Select(item => new PublicStoreDto(item.Id, item.Name, item.Description,
                    item.OfficialUrl, "/api/v1/stores/" + item.Id + "/logo?v=" + item.Logo!.ContentSha256)).ToArrayAsync(token);
                if (!featured && sort != "recommended")
                    items = (sort == "name-asc" ? items.OrderBy(item => item.Name, NameComparer)
                        : items.OrderByDescending(item => item.Name, NameComparer)).ThenBy(item => item.Id).ToArray();
                return new StoreListDto<PublicStoreDto>(items);
            }, token);

    public Task<StoreListDto<StaffStoreDto>> ListStaffAsync(StoreStatus? status, string[] roles, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(StoreService).FullName}.{nameof(ListStaffAsync)}",
            () => LogValueSummary.Inputs((nameof(status), status), (nameof(roles), roles), (nameof(token), token)), async () =>
            {
                RequireStaff(roles, BackofficeAction.ViewStores);
                if (status.HasValue && !Enum.IsDefined(status.Value)) throw new ServiceException(400, "invalid_store_status");
                var query = database.Stores.AsNoTracking();
                if (status.HasValue) query = query.Where(item => item.Status == status);
                return new StoreListDto<StaffStoreDto>(await query.OrderBy(item => item.DisplayOrder).ThenBy(item => item.Id)
                    .Select(StaffProjection).ToArrayAsync(token));
            }, token);

    public Task<StaffStoreDto> GetStaffAsync(int id, string[] roles, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(StoreService).FullName}.{nameof(GetStaffAsync)}",
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(roles), roles), (nameof(token), token)), async () =>
            {
                RequireStaff(roles, BackofficeAction.ViewStores);
                return await StaffDetailsAsync(id, token);
            }, token);

    public Task<StoreLogoDto> GetLogoAsync(int id, string[]? staffRoles, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(StoreService).FullName}.{nameof(GetLogoAsync)}",
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(staffRoles), staffRoles), (nameof(token), token)), async () =>
            {
                if (staffRoles is not null) RequireStaff(staffRoles, BackofficeAction.ViewStores);
                return await database.StoreLogos.AsNoTracking()
                    .Where(item => item.StoreId == id && (staffRoles != null || item.Store.Status == StoreStatus.Active))
                    .Select(item => new StoreLogoDto(item.Content, item.ContentType, item.ContentSha256)).SingleOrDefaultAsync(token)
                    ?? throw new ServiceException(404, "resource_not_found");
            }, token);

    public Task<StaffStoreDto> CreateAsync(StoreWriteRequest request, string[] roles, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(StoreService).FullName}.{nameof(CreateAsync)}",
            () => LogValueSummary.Inputs((nameof(request), request), (nameof(roles), roles), (nameof(token), token)), async () =>
            {
                RequireStaff(roles, BackofficeAction.CreateStore);
                var fields = StoreRules.Normalize(request);
                var logo = await StoreRules.ReadLogoAsync(request.Logo, token);
                RequireLogo(request.Status, logo.HasValue);
                var now = clock.GetUtcNow();
                var store = new Store(fields.Name, fields.Description, fields.Url, now,
                    request.Status, request.ShowOnHome, request.DisplayOrder, logo);
                database.Stores.Add(store);
                await database.SaveChangesAsync(token);
                return await StaffDetailsAsync(store.Id, token);
            }, token);

    public Task<StaffStoreDto> UpdateAsync(int id, StoreWriteRequest request, string[] roles, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(StoreService).FullName}.{nameof(UpdateAsync)}",
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(request), request), (nameof(roles), roles), (nameof(token), token)), async () =>
            {
                RequireStaff(roles, BackofficeAction.EditStore);
                var version = StoreRules.RequireVersion(request.Version);
                var store = await FindForMutationAsync(id, version, token);
                var fields = StoreRules.Normalize(request);
                var logo = await StoreRules.ReadLogoAsync(request.Logo, token);
                RequireLogo(request.Status, logo.HasValue || store.Logo is not null);
                var now = clock.GetUtcNow();
                store.Update(fields.Name, fields.Description, fields.Url, request.Status, request.ShowOnHome, request.DisplayOrder, now);
                if (logo.HasValue) store.SetLogo(logo.Value.ContentType, logo.Value.Content, now);
                await SaveMutationAsync(token);
                return await StaffDetailsAsync(store.Id, token);
            }, token);

    public Task DeleteAsync(int id, Guid? version, string[] roles, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(StoreService).FullName}.{nameof(DeleteAsync)}",
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(version), version), (nameof(roles), roles), (nameof(token), token)), async () =>
            {
                RequireStaff(roles, BackofficeAction.DeleteStore);
                var store = await FindForMutationAsync(id, StoreRules.RequireVersion(version), token);
                database.Stores.Remove(store);
                await SaveMutationAsync(token);
            }, token);

    private async Task<StaffStoreDto> StaffDetailsAsync(int id, CancellationToken token)
        => await database.Stores.AsNoTracking().Where(item => item.Id == id).Select(StaffProjection).SingleOrDefaultAsync(token)
            ?? throw new ServiceException(404, "resource_not_found");

    private async Task<Store> FindForMutationAsync(int id, Guid version, CancellationToken token)
    {
        var store = await database.Stores.Include(item => item.Logo).SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new ServiceException(404, "resource_not_found");
        if (store.Version != version) throw new ServiceException(409, "store_update_conflict");
        return store;
    }

    private async Task SaveMutationAsync(CancellationToken token)
    {
        // EF's SaveChanges transaction commits the parent and dependent together, including on concurrency failure.
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            throw new ServiceException(409, "store_update_conflict");
        }
    }

    private static void RequireLogo(StoreStatus status, bool hasLogo)
    {
        if (status == StoreStatus.Active && !hasLogo) throw new ServiceException(400, "store_logo_required");
    }

    private static void RequireStaff(string[] roles, BackofficeAction action)
    {
        if (!BackofficeAuthorization.IsAllowed(roles, action)) throw new ServiceException(403, "access_denied");
    }
}
