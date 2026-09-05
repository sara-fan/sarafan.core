// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class BackofficeUserService(
    AppDbContext database,
    IBackofficePasswordHasher passwordHasher,
    IOptions<BackofficeBootstrapOptions> bootstrapOptions,
    TimeProvider timeProvider,
    ILogger<BackofficeUserService> logger)
{
    private const string AdministratorMutationLockSql = "SELECT pg_advisory_xact_lock(1397301386)";
    private readonly BackofficeBootstrapOptions _bootstrapOptions = bootstrapOptions.Value;

    public Task<IReadOnlyList<BackofficeUserDto>> ListAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeUserService).FullName}.{nameof(ListAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)),
            () => ListCoreAsync(cancellationToken),
            cancellationToken);

    public Task<BackofficeUserDto> GetAsync(int id, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeUserService).FullName}.{nameof(GetAsync)}",
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(cancellationToken), cancellationToken)),
            () => GetCoreAsync(id, cancellationToken),
            cancellationToken);

    public Task<BackofficeIdentityDto> GetIdentityAsync(int id, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeUserService).FullName}.{nameof(GetIdentityAsync)}",
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(cancellationToken), cancellationToken)),
            () => GetIdentityCoreAsync(id, cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<BackofficeRoleDto>> ListRolesAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeUserService).FullName}.{nameof(ListRolesAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)),
            () => ListRolesCoreAsync(cancellationToken),
            cancellationToken);

    public Task<BackofficeUserDto> CreateAsync(
        BackofficeUserCreateRequest request,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeUserService).FullName}.{nameof(CreateAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(request), request),
                (nameof(cancellationToken), cancellationToken)),
            () => CreateCoreAsync(request, cancellationToken),
            cancellationToken);

    public Task<BackofficeUserDto> UpdateAsync(
        int id,
        BackofficeUserUpdateRequest request,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeUserService).FullName}.{nameof(UpdateAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(id), id),
                (nameof(request), request),
                (nameof(cancellationToken), cancellationToken)),
            () => UpdateCoreAsync(id, request, cancellationToken),
            cancellationToken);

    public Task<BackofficeIdentityDto> UpdateSelfAsync(
        int id,
        BackofficeSelfUpdateRequest request,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeUserService).FullName}.{nameof(UpdateSelfAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(id), id),
                (nameof(request), request),
                (nameof(cancellationToken), cancellationToken)),
            () => UpdateSelfCoreAsync(id, request, cancellationToken),
            cancellationToken);

    public Task DisableAsync(int id, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeUserService).FullName}.{nameof(DisableAsync)}",
            () => LogValueSummary.Inputs((nameof(id), id), (nameof(cancellationToken), cancellationToken)),
            () => DisableCoreAsync(id, cancellationToken),
            cancellationToken);

    private async Task<IReadOnlyList<BackofficeUserDto>> ListCoreAsync(CancellationToken cancellationToken)
    {
        var users = await database.BackofficeUsers
            .AsNoTracking()
            .Include(item => item.UserRoles)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return users.Select(BackofficeUserDto.From).ToList();
    }

    private async Task<BackofficeUserDto> GetCoreAsync(int id, CancellationToken cancellationToken)
    {
        var user = await FindUserAsync(id, false, cancellationToken);
        return BackofficeUserDto.From(user);
    }

    private async Task<BackofficeIdentityDto> GetIdentityCoreAsync(int id, CancellationToken cancellationToken)
    {
        var user = await FindUserAsync(id, false, cancellationToken);
        return BackofficeIdentityDto.From(user);
    }

    private async Task<IReadOnlyList<BackofficeRoleDto>> ListRolesCoreAsync(CancellationToken cancellationToken)
    {
        var roles = await database.BackofficeRoles
            .AsNoTracking()
            .OrderBy(item => item.Code)
            .ToListAsync(cancellationToken);
        return roles.Select(BackofficeRoleDto.From).ToList();
    }

    private async Task<BackofficeUserDto> CreateCoreAsync(
        BackofficeUserCreateRequest request,
        CancellationToken cancellationToken)
    {
        var roles = NormalizeRoles(request.Roles);
        ValidatePassword(request.Password);
        var email = NormalizeEmail(request.Email);
        var normalizedEmail = NormalizeEmailKey(email);
        if (await database.BackofficeUsers.AnyAsync(
                item => item.NormalizedEmail == normalizedEmail,
                cancellationToken))
        {
            throw EmailExists();
        }

        var now = timeProvider.GetUtcNow();
        var user = new BackofficeUser
        {
            Email = email,
            NormalizedEmail = normalizedEmail,
            FirstName = NormalizeName(request.FirstName),
            LastName = NormalizeName(request.LastName),
            Patronymic = NormalizeOptionalName(request.Patronymic),
            PasswordHash = passwordHasher.Hash(request.Password),
            CreatedAt = now,
            UpdatedAt = now,
            UserRoles = roles.Select(role => new BackofficeUserRole { RoleCode = role }).ToList()
        };
        database.BackofficeUsers.Add(user);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw EmailExists();
        }

        return BackofficeUserDto.From(user);
    }

    private async Task<BackofficeUserDto> UpdateCoreAsync(
        int id,
        BackofficeUserUpdateRequest request,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await AcquireAdministratorMutationLockAsync(cancellationToken);
        var roles = NormalizeRoles(request.Roles);
        var user = await FindUserAsync(id, true, cancellationToken);
        if (user.IsActive
            && user.UserRoles.Any(item => item.RoleCode == BackofficeRoles.Administrator)
            && (!request.IsActive || !roles.Contains(BackofficeRoles.Administrator)))
        {
            await EnsureAnotherAdministratorAsync(id, cancellationToken);
        }

        var email = NormalizeEmail(request.Email);
        var normalizedEmail = NormalizeEmailKey(email);
        var roleChanged = !user.UserRoles.Select(item => item.RoleCode).ToHashSet(StringComparer.Ordinal)
            .SetEquals(roles);
        var securityChanged = user.NormalizedEmail != normalizedEmail
            || user.IsActive != request.IsActive
            || roleChanged;
        if (request.IsActive
            && user.IsDemo
            && string.IsNullOrWhiteSpace(request.Password)
            && RealOperationsEnabled())
        {
            throw new ServiceException(StatusCodes.Status409Conflict, "demo_backoffice_forbidden");
        }

        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            ValidatePassword(request.Password);
            user.PasswordHash = passwordHasher.Hash(request.Password);
            user.IsDemo = false;
            securityChanged = true;
        }

        user.Email = email;
        user.NormalizedEmail = normalizedEmail;
        user.FirstName = NormalizeName(request.FirstName);
        user.LastName = NormalizeName(request.LastName);
        user.Patronymic = NormalizeOptionalName(request.Patronymic);
        user.IsActive = request.IsActive;
        user.UpdatedAt = timeProvider.GetUtcNow();
        if (roleChanged)
        {
            database.BackofficeUserRoles.RemoveRange(user.UserRoles);
            user.UserRoles = roles
                .Select(role => new BackofficeUserRole { BackofficeUser = user, RoleCode = role })
                .ToList();
        }

        if (securityChanged)
        {
            user.TokenVersion++;
            await RevokeSessionsAsync(user.Id, user.UpdatedAt, cancellationToken);
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw EmailExists();
        }

        return BackofficeUserDto.From(user);
    }

    private async Task<BackofficeIdentityDto> UpdateSelfCoreAsync(
        int id,
        BackofficeSelfUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var user = await FindUserAsync(id, true, cancellationToken);
        if (!user.IsActive)
        {
            throw UserNotFound();
        }

        user.FirstName = NormalizeName(request.FirstName);
        user.LastName = NormalizeName(request.LastName);
        user.Patronymic = NormalizeOptionalName(request.Patronymic);
        user.UpdatedAt = timeProvider.GetUtcNow();
        if (!string.IsNullOrWhiteSpace(request.Password))
        {
            ValidatePassword(request.Password);
            user.PasswordHash = passwordHasher.Hash(request.Password);
            user.IsDemo = false;
            user.TokenVersion++;
            await RevokeSessionsAsync(user.Id, user.UpdatedAt, cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);
        return BackofficeIdentityDto.From(user);
    }

    private async Task DisableCoreAsync(int id, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await AcquireAdministratorMutationLockAsync(cancellationToken);
        var user = await FindUserAsync(id, true, cancellationToken);
        if (!user.IsActive)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (user.UserRoles.Any(item => item.RoleCode == BackofficeRoles.Administrator))
        {
            await EnsureAnotherAdministratorAsync(id, cancellationToken);
        }

        user.IsActive = false;
        user.TokenVersion++;
        user.UpdatedAt = timeProvider.GetUtcNow();
        await RevokeSessionsAsync(user.Id, user.UpdatedAt, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<BackofficeUser> FindUserAsync(
        int id,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var query = database.BackofficeUsers.Include(item => item.UserRoles).AsQueryable();
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw UserNotFound();
    }

    private async Task EnsureAnotherAdministratorAsync(int excludedUserId, CancellationToken cancellationToken)
    {
        var hasAnother = await database.BackofficeUsers.AnyAsync(
            item => item.Id != excludedUserId
                && item.IsActive
                && item.UserRoles.Any(role => role.RoleCode == BackofficeRoles.Administrator),
            cancellationToken);
        if (!hasAnother)
        {
            throw new ServiceException(StatusCodes.Status409Conflict, "last_backoffice_administrator");
        }
    }

    private async Task RevokeSessionsAsync(
        int userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sessions = await database.BackofficeRefreshSessions
            .Where(item => item.BackofficeUserId == userId && item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
        }
    }

    private Task AcquireAdministratorMutationLockAsync(CancellationToken cancellationToken)
        => database.Database.ExecuteSqlRawAsync(
            AdministratorMutationLockSql,
            cancellationToken);

    private static IReadOnlySet<string> NormalizeRoles(IReadOnlyCollection<string>? roles)
    {
        var normalized = roles?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
        if (normalized.Count == 0 || normalized.Any(role => !BackofficeRoles.Codes.Contains(role)))
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_backoffice_role");
        }

        return normalized;
    }

    private static string NormalizeEmail(string email) => email.Trim();

    private static string NormalizeEmailKey(string email) => email.ToLowerInvariant();

    private static string NormalizeName(string name)
    {
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > 100)
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_backoffice_user_data");
        }

        return normalized;
    }

    private static string? NormalizeOptionalName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return NormalizeName(name);
    }

    private static void ValidatePassword(string password)
    {
        if (!BackofficePasswordRules.IsValid(password))
        {
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_backoffice_user_data");
        }
    }

    private static ServiceException UserNotFound()
        => new(StatusCodes.Status404NotFound, "backoffice_user_not_found");

    private static ServiceException EmailExists()
        => new(StatusCodes.Status409Conflict, "backoffice_email_exists");

    private bool RealOperationsEnabled()
        => _bootstrapOptions.RealOrdersEnabled || _bootstrapOptions.RealPaymentIntegrationEnabled;
}
