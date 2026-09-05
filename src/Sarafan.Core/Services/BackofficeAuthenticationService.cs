// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed record BackofficeAuthenticationSession(
    BackofficeAuthenticationSessionDto Response,
    string RefreshToken);

public sealed class BackofficeAuthenticationService(
    AppDbContext database,
    IBackofficePasswordHasher passwordHasher,
    BackofficeJwtTokenService tokenService,
    VerificationAttemptStore attemptStore,
    IOptions<BackofficeAuthenticationOptions> options,
    IOptions<BackofficeBootstrapOptions> bootstrapOptions,
    TimeProvider timeProvider,
    ILogger<BackofficeAuthenticationService> logger)
{
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
    private readonly BackofficeAuthenticationOptions _options = options.Value;
    private readonly BackofficeBootstrapOptions _bootstrapOptions = bootstrapOptions.Value;

    public Task<BackofficeAuthenticationSession> LoginAsync(
        BackofficeLoginRequest request,
        string remoteAddress,
        string? userAgent,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeAuthenticationService).FullName}.{nameof(LoginAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(request), request),
                (nameof(remoteAddress), remoteAddress),
                (nameof(userAgent), userAgent),
                (nameof(cancellationToken), cancellationToken)),
            () => LoginCoreAsync(request, remoteAddress, userAgent, cancellationToken),
            cancellationToken);

    public Task<BackofficeAuthenticationSession> RefreshAsync(
        string rawToken,
        string remoteAddress,
        string? userAgent,
        CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeAuthenticationService).FullName}.{nameof(RefreshAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(rawToken), rawToken),
                (nameof(remoteAddress), remoteAddress),
                (nameof(userAgent), userAgent),
                (nameof(cancellationToken), cancellationToken)),
            () => RefreshCoreAsync(rawToken, remoteAddress, userAgent, cancellationToken),
            cancellationToken);

    public Task LogoutAsync(string? rawToken, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeAuthenticationService).FullName}.{nameof(LogoutAsync)}",
            () => LogValueSummary.Inputs(
                (nameof(rawToken), rawToken),
                (nameof(cancellationToken), cancellationToken)),
            () => LogoutCoreAsync(rawToken, cancellationToken),
            cancellationToken);

    private async Task<BackofficeAuthenticationSession> LoginCoreAsync(
        BackofficeLoginRequest request,
        string remoteAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(request.Email);
        var user = await database.BackofficeUsers
            .Include(item => item.UserRoles)
            .SingleOrDefaultAsync(item => item.NormalizedEmail == email, cancellationToken);
        if (user is null
            || !user.IsActive
            || (user.IsDemo && RealOperationsEnabled())
            || user.UserRoles.Count == 0
            || !VerifyPassword(request.Password, user.PasswordHash))
        {
            CheckAttemptLimit($"backoffice:login:account:{HashRateLimitKey(email)}", 10);
            CheckAttemptLimit($"backoffice:login:origin:{HashRateLimitKey(remoteAddress)}", 30);
            throw LoginFailed();
        }

        var now = timeProvider.GetUtcNow();
        var rawRefreshToken = JwtTokenService.CreateRefreshToken();
        database.BackofficeRefreshSessions.Add(CreateRefreshSession(
            user,
            Guid.NewGuid(),
            JwtTokenService.HashRefreshToken(rawRefreshToken),
            remoteAddress,
            userAgent,
            now));
        await database.SaveChangesAsync(cancellationToken);
        return CreateSession(user, rawRefreshToken);
    }

    private async Task<BackofficeAuthenticationSession> RefreshCoreAsync(
        string rawToken,
        string remoteAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var tokenHash = JwtTokenService.HashRefreshToken(rawToken);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var current = await database.BackofficeRefreshSessions
            .Include(item => item.BackofficeUser)
            .ThenInclude(item => item.UserRoles)
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);

        if (current is null)
        {
            throw InvalidRefreshToken();
        }

        if (current.RevokedAt is not null
            || current.ReplacedByTokenHash is not null
            || current.ExpiresAt <= now
            || !current.BackofficeUser.IsActive
            || (current.BackofficeUser.IsDemo && RealOperationsEnabled()))
        {
            await RevokeFamilyAsync(current.FamilyId, now, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw InvalidRefreshToken();
        }

        var nextRawToken = JwtTokenService.CreateRefreshToken();
        var nextHash = JwtTokenService.HashRefreshToken(nextRawToken);
        current.RevokedAt = now;
        current.ReplacedByTokenHash = nextHash;
        database.BackofficeRefreshSessions.Add(CreateRefreshSession(
            current.BackofficeUser,
            current.FamilyId,
            nextHash,
            remoteAddress,
            userAgent,
            now));

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            var reused = await database.BackofficeRefreshSessions
                .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
            if (reused is not null)
            {
                await RevokeFamilyAsync(reused.FamilyId, now, cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
            }

            throw InvalidRefreshToken();
        }

        return CreateSession(current.BackofficeUser, nextRawToken);
    }

    private async Task LogoutCoreAsync(string? rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        var tokenHash = JwtTokenService.HashRefreshToken(rawToken);
        var current = await database.BackofficeRefreshSessions
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
        if (current is null)
        {
            return;
        }

        await RevokeFamilyAsync(current.FamilyId, timeProvider.GetUtcNow(), cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
    }

    private BackofficeAuthenticationSession CreateSession(BackofficeUser user, string refreshToken)
    {
        var accessToken = tokenService.CreateAccessToken(user);
        return new BackofficeAuthenticationSession(
            new BackofficeAuthenticationSessionDto(
                accessToken.Token,
                accessToken.ExpiresAt,
                BackofficeIdentityDto.From(user)),
            refreshToken);
    }

    private BackofficeRefreshSession CreateRefreshSession(
        BackofficeUser user,
        Guid familyId,
        string tokenHash,
        string remoteAddress,
        string? userAgent,
        DateTimeOffset now) => new()
        {
            BackofficeUser = user,
            FamilyId = familyId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.RefreshTokenDays),
            CreatedByIp = Limit(remoteAddress, 64),
            UserAgent = Limit(userAgent, 256)
        };

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessions = await database.BackofficeRefreshSessions
            .Where(item => item.FamilyId == familyId && item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
        }
    }

    private bool VerifyPassword(string password, string passwordHash)
    {
        if (!BackofficePasswordRules.IsValid(password))
        {
            return false;
        }

        try
        {
            return passwordHasher.Verify(password, passwordHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    private void CheckAttemptLimit(string key, int limit)
    {
        if (!attemptStore.TryConsume(key, limit, AttemptWindow))
        {
            throw new ServiceException(StatusCodes.Status429TooManyRequests, "rate_limited");
        }
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string HashRateLimitKey(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ServiceException LoginFailed()
        => new(StatusCodes.Status401Unauthorized, "backoffice_login_failed");

    private static ServiceException InvalidRefreshToken()
        => new(StatusCodes.Status401Unauthorized, "invalid_backoffice_refresh_token");

    private bool RealOperationsEnabled()
        => _bootstrapOptions.RealOrdersEnabled || _bootstrapOptions.RealPaymentIntegrationEnabled;

    private static string? Limit(string? value, int length)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
