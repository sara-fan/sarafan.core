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
internal sealed record AuthenticationResolution(
    AuthenticationFlowStep NextStep,
    LegalDocumentKind[] RequiredDocumentKinds,
    int? TargetCustomerId);

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

    public Task<PhoneResolveDto> ResolveAsync(PhoneResolveRequest request, string remoteAddress, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(logger, $"{typeof(AuthenticationService).FullName}.{nameof(ResolveAsync)}",
            () => LogValueSummary.Inputs((nameof(request), request), (nameof(remoteAddress), remoteAddress), (nameof(cancellationToken), cancellationToken)),
            async () =>
            {
                CheckAttemptLimit($"resolve:ip:{remoteAddress}", 20);
                var phone = NormalizePhone(request.Phone);
                CheckAttemptLimit($"resolve:phone:{PhoneAttemptKey(phone)}", 10);
                var resolution = await ResolveCoreAsync(phone, cancellationToken);
                return new PhoneResolveDto(resolution.NextStep, resolution.RequiredDocumentKinds);
            }, cancellationToken);

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

    private async Task<string?> RequestCodeCoreAsync(RequestCodeRequest request, string remoteAddress, CancellationToken cancellationToken)
    {
        CheckAttemptLimit($"request:ip:{remoteAddress}", 20);
        var phone = NormalizePhone(request.Phone);
        CheckAttemptLimit($"request:phone:{PhoneAttemptKey(phone)}", 3);

        var resolution = await ResolveCoreAsync(phone, cancellationToken);
        string? receipt;
        if (resolution.NextStep == AuthenticationFlowStep.Code)
        {
            if (HasConsentPayload(request)) throw InvalidAuthenticationRequest();
            receipt = null;
        }
        else
        {
            receipt = await ConsentTransaction.Run(database, async () =>
            {
                var lockedResolution = await ResolveCoreAsync(phone, cancellationToken);
                if (lockedResolution.NextStep == AuthenticationFlowStep.Code)
                {
                    if (HasConsentPayload(request)) throw InvalidAuthenticationRequest();
                    return null;
                }

                return await consents.BeginAuthenticationAsync(
                    phone, lockedResolution, request, cancellationToken);
            }, cancellationToken);
        }

        await codeProvider.RequestCodeAsync(phone, cancellationToken);
        return receipt;
    }

    private async Task<AuthenticationSession> VerifyCodeCoreAsync(
        VerifyCodeRequest request, string remoteAddress, string? userAgent, CancellationToken cancellationToken)
    {
        CheckAttemptLimit($"verify:ip:{remoteAddress}", 30);
        var phone = NormalizePhone(request.Phone);
        CheckAttemptLimit($"verify:phone:{PhoneAttemptKey(phone)}", 5);

        if (!await codeProvider.VerifyCodeAsync(phone, request.Code, cancellationToken))
            throw new ServiceException(StatusCodes.Status401Unauthorized, "invalid_code");

        if (string.IsNullOrWhiteSpace(request.OnboardingToken))
        {
            return await LoginAsync(phone, remoteAddress, userAgent, cancellationToken);
        }

        return await CompleteReceiptAsync(phone, request.OnboardingToken, remoteAddress, userAgent, cancellationToken);
    }

    private async Task<AuthenticationSession> CompleteReceiptAsync(
        string phone, string receiptToken, string remoteAddress, string? userAgent, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await ConsentTransaction.Lock(database, cancellationToken);

        var receipt = await consents.ReadAuthenticationReceiptAsync(receiptToken, phone, cancellationToken);
        if (receipt.TargetCustomerId is { } targetCustomerId)
            await ConsentTransaction.LockCustomer(database, targetCustomerId, cancellationToken);
        var customer = await database.Customers
            .Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Phone == phone, cancellationToken);
        var resolution = await ResolveCoreAsync(phone, cancellationToken);

        ValidateReceiptAccount(receipt, customer, resolution);
        await consents.ValidateAuthenticationReceiptVersionsAsync(receipt, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var created = false;
        var reactivated = false;
        if (customer is null)
        {
            customer = new Customer
            {
                Phone = phone,
                CreatedAt = now,
                UpdatedAt = now,
                Profile = new CustomerProfile()
            };
            database.Customers.Add(customer);
            created = true;
            await database.SaveChangesAsync(cancellationToken);
        }
        else if (customer.State == CustomerState.Disabled)
        {
            customer.State = CustomerProfileState.Evaluate(customer.Profile);
            customer.TokenVersion++;
            customer.UpdatedAt = now;
            await RevokeAllSessionsAsync(customer.Id, now, cancellationToken);
            reactivated = true;
        }

        var source = created ? "registration" : reactivated ? "reactivation" : "authentication";
        await consents.CompleteAuthenticationConsentsAsync(customer, receipt, source, cancellationToken);

        var rawRefreshToken = JwtTokenService.CreateRefreshToken();
        database.RefreshSessions.Add(CreateRefreshSession(
            customer, Guid.NewGuid(), JwtTokenService.HashRefreshToken(rawRefreshToken), remoteAddress, userAgent, now));

        await database.SaveChangesAsync(cancellationToken);
        await consents.ValidateAuthenticationAtCommitAsync(receiptToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var hasPhoto = !created && await database.CustomerPhotos.AsNoTracking()
            .AnyAsync(item => item.CustomerId == customer.Id, cancellationToken);
        return CreateSession(customer, hasPhoto, rawRefreshToken);
    }

    private static void ValidateReceiptAccount(ConsentOnboarding receipt, Customer? customer, AuthenticationResolution resolution)
    {
        if (receipt.Flow == AuthenticationFlowStep.Agreement)
        {
            if (resolution.NextStep != AuthenticationFlowStep.Agreement
                || customer is null
                || customer.State == CustomerState.Disabled
                || receipt.TargetCustomerId != customer.Id
                || receipt.TargetCustomerId != resolution.TargetCustomerId)
                throw RequirementsChanged(resolution);
            return;
        }

        if (receipt.Flow != AuthenticationFlowStep.Registration
            || resolution.NextStep != AuthenticationFlowStep.Registration
            || receipt.TargetCustomerId != resolution.TargetCustomerId
            || receipt.TargetCustomerId is not null && receipt.TargetCustomerId != customer?.Id
            || resolution.RequiredDocumentKinds.Contains(LegalDocumentKind.UserAgreement) && !receipt.TermsAccepted
            || !receipt.PersonalDataDocumentId.HasValue)
            throw RequirementsChanged(resolution);
    }

    private async Task<AuthenticationSession> RefreshCoreAsync(
        string rawToken, string remoteAddress, string? userAgent, CancellationToken cancellationToken)
    {
        var tokenHash = JwtTokenService.HashRefreshToken(rawToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var customerId = await database.RefreshSessions.AsNoTracking()
            .Where(item => item.TokenHash == tokenHash)
            .Select(item => (int?)item.CustomerId)
            .SingleOrDefaultAsync(cancellationToken);
        if (customerId is null) throw InvalidRefreshToken();

        await ConsentTransaction.LockCustomer(database, customerId.Value, cancellationToken);
        var now = timeProvider.GetUtcNow();

        var current = await database.RefreshSessions.Include(item => item.Customer).ThenInclude(item => item.Profile)
            .SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
        if (current is null) throw InvalidRefreshToken();

        if (current.Customer.State == CustomerState.Disabled)
        {
            await RevokeAllSessionsAsync(current.CustomerId, now, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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
        database.RefreshSessions.Add(CreateRefreshSession(current.Customer, current.FamilyId, nextHash, remoteAddress, userAgent, now));

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            database.ChangeTracker.Clear();
            var reused = await database.RefreshSessions.SingleOrDefaultAsync(item => item.TokenHash == tokenHash, cancellationToken);
            if (reused is not null)
            {
                await RevokeFamilyAsync(reused.FamilyId, now, cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
            }
            throw InvalidRefreshToken();
        }

        var hasPhoto = await database.CustomerPhotos.AnyAsync(item => item.CustomerId == current.CustomerId, cancellationToken);
        return CreateSession(current.Customer, hasPhoto, nextRawToken);
    }

    private async Task LogoutCoreAsync(string? rawToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return;
        var hash = JwtTokenService.HashRefreshToken(rawToken);
        var current = await database.RefreshSessions.SingleOrDefaultAsync(item => item.TokenHash == hash, cancellationToken);
        if (current is null) return;
        await RevokeFamilyAsync(current.FamilyId, timeProvider.GetUtcNow(), cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task<AuthenticationSession> LoginAsync(
        string phone, string remoteAddress, string? userAgent, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await ConsentTransaction.Lock(database, cancellationToken);

        var resolution = await ResolveCoreAsync(phone, cancellationToken);
        if (resolution.NextStep != AuthenticationFlowStep.Code) throw RequirementsChanged(resolution);

        var customer = await database.Customers.Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Phone == phone, cancellationToken);
        if (customer is null
            || customer.State == CustomerState.Disabled
            || resolution.TargetCustomerId != customer.Id)
            throw RequirementsChanged(await ResolveCoreAsync(phone, cancellationToken));

        var now = timeProvider.GetUtcNow();
        var rawRefreshToken = JwtTokenService.CreateRefreshToken();
        database.RefreshSessions.Add(CreateRefreshSession(
            customer, Guid.NewGuid(), JwtTokenService.HashRefreshToken(rawRefreshToken), remoteAddress, userAgent, now));
        await database.SaveChangesAsync(cancellationToken);

        var atCommit = await ResolveCoreAsync(phone, cancellationToken);
        if (atCommit.NextStep != AuthenticationFlowStep.Code || atCommit.TargetCustomerId != customer.Id)
            throw RequirementsChanged(atCommit);

        await transaction.CommitAsync(cancellationToken);
        var hasPhoto = await database.CustomerPhotos.AnyAsync(item => item.CustomerId == customer.Id, cancellationToken);
        return CreateSession(customer, hasPhoto, rawRefreshToken);
    }

    private async Task<AuthenticationResolution> ResolveCoreAsync(string phone, CancellationToken cancellationToken)
    {
        var customer = await database.Customers.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Phone == phone, cancellationToken);
        var hasCurrentAgreement = customer is not null
            && await consents.HasCurrentGrantAsync(customer.Id, LegalDocumentKind.UserAgreement, cancellationToken);

        if (customer is not null && customer.State != CustomerState.Disabled)
            return hasCurrentAgreement
                ? new(AuthenticationFlowStep.Code, [], customer.Id)
                : new(AuthenticationFlowStep.Agreement, [LegalDocumentKind.UserAgreement], customer.Id);

        var required = hasCurrentAgreement
            ? new[] { LegalDocumentKind.PersonalDataConsent }
            : new[] { LegalDocumentKind.UserAgreement, LegalDocumentKind.PersonalDataConsent };
        return new(AuthenticationFlowStep.Registration, required, customer?.Id);
    }

    private AuthenticationSession CreateSession(Customer customer, bool hasPhoto, string refreshToken)
    {
        var accessToken = tokenService.CreateAccessToken(customer);
        return new AuthenticationSession(
            new AuthenticationSessionDto(accessToken.Token, accessToken.ExpiresAt, CustomerDto.From(customer, hasPhoto)),
            refreshToken);
    }

    private RefreshSession CreateRefreshSession(
        Customer customer, Guid familyId, string tokenHash, string remoteAddress, string? userAgent, DateTimeOffset now) => new()
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
        var sessions = await database.RefreshSessions.Where(item => item.FamilyId == familyId && item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions) session.RevokedAt = now;
    }

    private async Task RevokeAllSessionsAsync(int customerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var sessions = await database.RefreshSessions.Where(item => item.CustomerId == customerId && item.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions) session.RevokedAt = now;
    }

    private string NormalizePhone(string? phone)
    {
        if (!phoneNormalizer.TryNormalize(phone, out var normalized))
            throw new ServiceException(StatusCodes.Status400BadRequest, "invalid_phone");
        return normalized;
    }

    private static bool HasConsentPayload(RequestCodeRequest request) =>
        request.TermsAccepted || request.TermsDocumentId.HasValue || request.PersonalDataConsent is not null;
    private static ServiceException InvalidAuthenticationRequest() => new(StatusCodes.Status400BadRequest, "invalid_auth_request");
    private static ServiceException RequirementsChanged(AuthenticationResolution resolution) =>
        new(StatusCodes.Status409Conflict, "authentication_requirements_changed")
        {
            NextStep = resolution.NextStep,
            RequiredDocumentKinds = resolution.RequiredDocumentKinds
        };

    private void CheckAttemptLimit(string key, int limit)
    {
        if (!attemptStore.TryConsume(key, limit, AttemptWindow))
            throw new ServiceException(StatusCodes.Status429TooManyRequests, "rate_limited");
    }

    private static string PhoneAttemptKey(string phone) => JwtTokenService.HashRefreshToken(phone);
    private static ServiceException InvalidRefreshToken() => new(StatusCodes.Status401Unauthorized, "invalid_refresh_token");
    private static string? Limit(string? value, int length)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}
