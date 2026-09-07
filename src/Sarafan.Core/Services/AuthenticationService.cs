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

public sealed record AuthenticationSession(AuthenticationSessionDto Response, string RefreshToken);

public sealed class AuthenticationService(
    AppDbContext database,
    IPhoneNormalizer phoneNormalizer,
    IVerificationCodeProvider codeProvider,
    VerificationAttemptStore attemptStore,
    JwtTokenService tokenService,
    IOptions<AuthenticationOptions> options,
    TimeProvider timeProvider,
    ConsentService consents,
    ILogger<AuthenticationService> logger)
{
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);
    private readonly AuthenticationOptions _options = options.Value;

    public Task<string?> RequestCodeAsync(RequestCodeRequest request, string remoteAddress, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(AuthenticationService).FullName}.{nameof(RequestCodeAsync)}",
            () => LogValueSummary.Inputs((nameof(request), request), (nameof(remoteAddress), remoteAddress), (nameof(cancellationToken), cancellationToken)),
            () => RequestCodeCoreAsync(request, remoteAddress, cancellationToken), cancellationToken);

    public Task<AuthenticationSession> VerifyCodeAsync(
        VerifyCodeRequest request, string remoteAddress, string? userAgent, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(AuthenticationService).FullName}.{nameof(VerifyCodeAsync)}",
            () => LogValueSummary.Inputs((nameof(request), request), (nameof(remoteAddress), remoteAddress), (nameof(userAgent), userAgent), (nameof(cancellationToken), cancellationToken)),
            () => VerifyCodeCoreAsync(request, remoteAddress, userAgent, cancellationToken), cancellationToken);

    public Task<AuthenticationSession> RefreshAsync(
        string rawToken, string remoteAddress, string? userAgent, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(AuthenticationService).FullName}.{nameof(RefreshAsync)}",
            () => LogValueSummary.Inputs((nameof(rawToken), rawToken), (nameof(remoteAddress), remoteAddress), (nameof(userAgent), userAgent), (nameof(cancellationToken), cancellationToken)),
            () => RefreshCoreAsync(rawToken, remoteAddress, userAgent, cancellationToken), cancellationToken);

    public Task LogoutAsync(string? rawToken, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(AuthenticationService).FullName}.{nameof(LogoutAsync)}",
            () => LogValueSummary.Inputs((nameof(rawToken), rawToken), (nameof(cancellationToken), cancellationToken)),
            () => LogoutCoreAsync(rawToken, cancellationToken), cancellationToken);

    private async Task<string?> RequestCodeCoreAsync(
        RequestCodeRequest request,
        string remoteAddress,
        CancellationToken cancellationToken)
    {
        var purpose = ValidatePurpose(request.Purpose);
        if (purpose == "register")
        {
            if (!request.TermsAccepted || request.PersonalDataConsent is null) throw new ServiceException(400, "consent_required");
            await consents.ValidateOnboardingDocumentsAsync(request.TermsDocumentId, request.PersonalDataConsent, cancellationToken);
        }
        var phone = NormalizePhone(request.Phone);
        CheckAttemptLimit($"request:ip:{remoteAddress}", 20);
        CheckAttemptLimit($"request:phone:{phone}", 3);
        var onboarding = purpose == "register" ? await consents.BeginOnboardingAsync(phone, request.TermsDocumentId, request.PersonalDataConsent!, cancellationToken) : null;
        await codeProvider.RequestCodeAsync(phone, cancellationToken);
        return onboarding;
    }

    private async Task<AuthenticationSession> VerifyCodeCoreAsync(
        VerifyCodeRequest request,
        string remoteAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var purpose = ValidatePurpose(request.Purpose);
        if (purpose == "register") await consents.ValidateOnboardingReceiptAsync(request.OnboardingToken, cancellationToken);
        var phone = NormalizePhone(request.Phone);
        CheckAttemptLimit($"verify:phone:{phone}", 5);
        CheckAttemptLimit($"verify:ip:{remoteAddress}", 30);

        if (!await codeProvider.VerifyCodeAsync(phone, request.Code, cancellationToken))
        {
            throw new ServiceException(
                StatusCodes.Status401Unauthorized,
                "invalid_code");
        }

        return purpose == "register"
            ? await RegisterAsync(phone, request, remoteAddress, userAgent, cancellationToken)
            : await LoginAsync(phone, remoteAddress, userAgent, cancellationToken);
    }

    private async Task<AuthenticationSession> RefreshCoreAsync(
        string rawToken,
        string remoteAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var tokenHash = JwtTokenService.HashRefreshToken(rawToken);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var current = await database.RefreshSessions
            .Include(item => item.Customer)
            .ThenInclude(item => item.Profile)
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);

        if (current is null)
        {
            throw InvalidRefreshToken();
        }

        if (current.RevokedAt is not null || current.ReplacedByTokenHash is not null || current.ExpiresAt <= now)
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
        database.RefreshSessions.Add(CreateRefreshSession(
            current.Customer,
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
            var reused = await database.RefreshSessions
                .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
            if (reused is not null)
            {
                await RevokeFamilyAsync(reused.FamilyId, now, cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
            }

            throw InvalidRefreshToken();
        }

        var hasPhoto = await database.CustomerPhotos
            .AnyAsync(item => item.CustomerId == current.CustomerId, cancellationToken);
        return CreateSession(current.Customer, hasPhoto, nextRawToken);
    }

    private async Task LogoutCoreAsync(string? rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        var hash = JwtTokenService.HashRefreshToken(rawToken);
        var current = await database.RefreshSessions
            .SingleOrDefaultAsync(item => item.TokenHash == hash, cancellationToken);
        if (current is null)
        {
            return;
        }

        await RevokeFamilyAsync(current.FamilyId, timeProvider.GetUtcNow(), cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<AuthenticationSession> RegisterAsync(
        string phone,
        VerifyCodeRequest request,
        string remoteAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OnboardingToken))
        {
            throw new ServiceException(
                StatusCodes.Status400BadRequest,
                "consent_required");
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await ConsentTransaction.Lock(database, cancellationToken);
        if (await database.Customers.AnyAsync(item => item.Phone == phone, cancellationToken))
        {
            throw new ServiceException(
                StatusCodes.Status409Conflict,
                "account_exists");
        }

        var now = timeProvider.GetUtcNow();
        var customer = new Customer
        {
            Phone = phone,
            CreatedAt = now,
            UpdatedAt = now,
            Profile = new CustomerProfile()
        };
        var rawRefreshToken = JwtTokenService.CreateRefreshToken();
        customer.RefreshSessions.Add(CreateRefreshSession(
            customer,
            Guid.NewGuid(),
            JwtTokenService.HashRefreshToken(rawRefreshToken),
            remoteAddress,
            userAgent,
            now));
        database.Customers.Add(customer);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ServiceException(
                StatusCodes.Status409Conflict,
                "account_exists");
        }

        await consents.CompleteOnboardingAsync(customer, request.OnboardingToken, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await consents.ValidateOnboardingAtCommitAsync(request.OnboardingToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CreateSession(customer, false, rawRefreshToken);
    }

    private async Task<AuthenticationSession> LoginAsync(
        string phone,
        string remoteAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var customer = await database.Customers
            .Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Phone == phone, cancellationToken);
        if (customer is null || customer.State == CustomerState.Disabled)
        {
            throw new ServiceException(
                StatusCodes.Status401Unauthorized,
                "login_failed");
        }

        var now = timeProvider.GetUtcNow();
        var rawRefreshToken = JwtTokenService.CreateRefreshToken();
        database.RefreshSessions.Add(CreateRefreshSession(
            customer,
            Guid.NewGuid(),
            JwtTokenService.HashRefreshToken(rawRefreshToken),
            remoteAddress,
            userAgent,
            now));
        await database.SaveChangesAsync(cancellationToken);
        var hasPhoto = await database.CustomerPhotos
            .AnyAsync(item => item.CustomerId == customer.Id, cancellationToken);
        return CreateSession(customer, hasPhoto, rawRefreshToken);
    }

    private AuthenticationSession CreateSession(Customer customer, bool hasPhoto, string refreshToken)
    {
        var accessToken = tokenService.CreateAccessToken(customer);
        return new AuthenticationSession(
            new AuthenticationSessionDto(
                accessToken.Token,
                accessToken.ExpiresAt,
                CustomerDto.From(customer, hasPhoto)),
            refreshToken);
    }

    private RefreshSession CreateRefreshSession(
        Customer customer,
        Guid familyId,
        string tokenHash,
        string remoteAddress,
        string? userAgent,
        DateTimeOffset now) => new()
        {
            Customer = customer,
            FamilyId = familyId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_options.RefreshTokenDays),
            CreatedByIp = Limit(remoteAddress, 64),
            UserAgent = Limit(userAgent, 256)
        };

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessions = await database.RefreshSessions
            .Where(item => item.FamilyId == familyId && item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.RevokedAt = now;
        }
    }

    private string NormalizePhone(string? phone)
    {
        if (!phoneNormalizer.TryNormalize(phone, out var normalized))
        {
            throw new ServiceException(
                StatusCodes.Status400BadRequest,
                "invalid_phone");
        }

        return normalized;
    }

    private static string ValidatePurpose(string purpose)
    {
        var normalized = purpose.Trim().ToLowerInvariant();
        if (normalized is not ("register" or "login"))
        {
            throw new ServiceException(
                StatusCodes.Status400BadRequest,
                "invalid_purpose");
        }

        return normalized;
    }

    private void CheckAttemptLimit(string key, int limit)
    {
        if (!attemptStore.TryConsume(key, limit, AttemptWindow))
        {
            throw new ServiceException(
                StatusCodes.Status429TooManyRequests,
                "rate_limited");
        }
    }

    private static ServiceException InvalidRefreshToken() => new(
        StatusCodes.Status401Unauthorized,
        "invalid_refresh_token");

    private static string? Limit(string? value, int length)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
