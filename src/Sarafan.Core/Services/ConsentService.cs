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

public sealed class ConsentService(AppDbContext database, TimeProvider clock, IOptions<ConsentOptions> options,
    IOptions<AuthenticationOptions> authentication, ILogger<ConsentService> logger)
{
    private ConsentOptions Settings => options.Value;

    internal async Task<bool> HasCurrentGrantAsync(int customerId, LegalDocumentKind kind, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var document = await LegalDocumentService.CurrentEntity(database, kind, now, token);
        if (document is null) return false;
        var last = await database.ConsentEvents.AsNoTracking()
            .Where(item => item.CustomerId == customerId && item.Kind == kind)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(token);
        return Status(last, document, now) == "current";
    }

    internal Task<string> BeginAuthenticationAsync(
        string phone,
        AuthenticationResolution resolution,
        RequestCodeRequest request,
        CancellationToken token)
        => Run(nameof(BeginAuthenticationAsync), () => ConsentTransaction.Run(database, async () =>
        {
            var requiresAgreement = resolution.RequiredDocumentKinds.Contains(LegalDocumentKind.UserAgreement);
            var requiresPersonalData = resolution.RequiredDocumentKinds.Contains(LegalDocumentKind.PersonalDataConsent);
            var agreement = await RequireDocument(LegalDocumentKind.UserAgreement, token);

            if (requiresAgreement)
            {
                if (!request.TermsAccepted) throw InvalidAuthenticationRequest();
                if (request.TermsDocumentId != agreement.Id) throw Changed(agreement);
            }
            else if (request.TermsAccepted || request.TermsDocumentId.HasValue)
            {
                throw InvalidAuthenticationRequest();
            }

            LegalDocument? personalData = null;
            if (requiresPersonalData)
            {
                if (request.PersonalDataConsent is null) throw InvalidAuthenticationRequest();
                personalData = await ValidateDecision(LegalDocumentKind.PersonalDataConsent, request.PersonalDataConsent, token);
                if (request.PersonalDataConsent.Decision != "grant") throw InvalidAuthenticationRequest();
            }
            else if (request.PersonalDataConsent is not null)
            {
                throw InvalidAuthenticationRequest();
            }

            var raw = JwtTokenService.CreateRefreshToken();
            var now = clock.GetUtcNow();
            database.ConsentOnboarding.Add(new ConsentOnboarding
            {
                TokenHash = JwtTokenService.HashRefreshToken(raw),
                PhoneHash = PhoneHash(phone),
                Flow = resolution.NextStep,
                TargetCustomerId = resolution.TargetCustomerId,
                PersonalDataDocumentId = personalData?.Id,
                TermsDocumentId = agreement.Id,
                PersonalDataHash = personalData?.ContentHash,
                TermsHash = agreement.ContentHash,
                TermsAccepted = requiresAgreement,
                PersonalDataIdempotencyKey = personalData is null ? null : request.PersonalDataConsent!.IdempotencyKey,
                TermsIdempotencyKey = requiresAgreement ? Guid.NewGuid() : null,
                At = now,
                ExpiresAt = now.AddMinutes(Settings.OnboardingMinutes)
            });
            return raw;
        }, token), token, request);

    internal async Task<ConsentOnboarding> ReadAuthenticationReceiptAsync(string raw, string phone, CancellationToken token)
    {
        var row = await ReadOnboarding(raw, token);
        if (row.PhoneHash != PhoneHash(phone))
            throw new ServiceException(400, "onboarding_consent_expired");
        return row;
    }

    internal async Task ValidateAuthenticationReceiptVersionsAsync(ConsentOnboarding row, CancellationToken token)
    {
        var agreement = await RequireDocument(LegalDocumentKind.UserAgreement, token);
        if (agreement.Id != row.TermsDocumentId || agreement.ContentHash != row.TermsHash) throw Changed(agreement);

        if (row.PersonalDataDocumentId.HasValue)
        {
            var personalData = await RequireDocument(LegalDocumentKind.PersonalDataConsent, token);
            if (personalData.Id != row.PersonalDataDocumentId || personalData.ContentHash != row.PersonalDataHash)
                throw Changed(personalData);
        }
    }

    internal async Task CompleteAuthenticationConsentsAsync(
        Customer customer,
        ConsentOnboarding row,
        string source,
        CancellationToken token)
    {
        await ValidateAuthenticationReceiptVersionsAsync(row, token);
        if (row.TermsAccepted)
        {
            var agreement = await database.LegalDocuments.SingleAsync(item => item.Id == row.TermsDocumentId, token);
            database.ConsentEvents.Add(NewEvent(
                CustomerKey(customer.Id), customer.Id, agreement, "grant", [], source,
                row.TermsIdempotencyKey ?? throw InvalidAuthenticationRequest(), row.At));
        }

        if (row.PersonalDataDocumentId is { } personalDataDocumentId)
        {
            var personalData = await database.LegalDocuments.SingleAsync(item => item.Id == personalDataDocumentId, token);
            var request = new ConsentDecisionRequest
            {
                DocumentId = personalData.Id,
                ContentHash = personalData.ContentHash,
                Decision = "grant",
                Categories = [],
                IdempotencyKey = row.PersonalDataIdempotencyKey ?? throw InvalidAuthenticationRequest()
            };
            var subject = CustomerKey(customer.Id);
            if (await FindRetry(subject, request, LegalDocumentKind.PersonalDataConsent, token) is null)
                database.ConsentEvents.Add(NewEvent(
                    subject, customer.Id, personalData, request.Decision, request.Categories, source,
                    request.IdempotencyKey, row.At));
        }

        row.UsedAt = clock.GetUtcNow();
    }

    internal async Task ValidateAuthenticationAtCommitAsync(string raw, CancellationToken token)
    {
        var hash = JwtTokenService.HashRefreshToken(raw);
        var row = await database.ConsentOnboarding.SingleAsync(item => item.TokenHash == hash, token);
        if (row.ExpiresAt <= clock.GetUtcNow()) throw new ServiceException(400, "onboarding_consent_expired");
        await ValidateAuthenticationReceiptVersionsAsync(row, token);
    }

    public Task ValidateOnboardingDocumentsAsync(Guid termsId, ConsentDecisionRequest request, CancellationToken token)
        => Run(nameof(ValidateOnboardingDocumentsAsync), async () =>
        {
            await ValidateDecision(LegalDocumentKind.PersonalDataConsent, request, token);
            if (request.Decision != "grant") throw new ServiceException(400, "consent_required");
            var agreement = await RequireDocument(LegalDocumentKind.UserAgreement, token);
            if (agreement.Id != termsId) throw Changed(agreement);
            return true;
        }, token, request);

    public Task ValidateOnboardingReceiptAsync(string? raw, CancellationToken token)
        => Run(nameof(ValidateOnboardingReceiptAsync), async () =>
        {
            var row = await ReadOnboarding(raw, token);
            await ValidateOnboardingVersions(row, token);
            return true;
        }, token, null);

    public Task<string> BeginOnboardingAsync(string phone, Guid termsId, ConsentDecisionRequest request, CancellationToken token)
        => Run(nameof(BeginOnboardingAsync), () => ConsentTransaction.Run(database, async () =>
        {
            var document = await ValidateDecision(LegalDocumentKind.PersonalDataConsent, request, token);
            if (request.Decision != "grant") throw new ServiceException(400, "consent_required");
            var agreement = await RequireDocument(LegalDocumentKind.UserAgreement, token);
            if (agreement.Id != termsId) throw Changed(agreement);
            var raw = JwtTokenService.CreateRefreshToken();
            var now = clock.GetUtcNow();
            database.ConsentOnboarding.Add(new ConsentOnboarding
            {
                TokenHash = JwtTokenService.HashRefreshToken(raw),
                PhoneHash = PhoneHash(phone),
                Flow = AuthenticationFlowStep.Registration,
                PersonalDataDocumentId = document.Id,
                TermsDocumentId = agreement.Id,
                PersonalDataHash = document.ContentHash,
                TermsHash = agreement.ContentHash,
                TermsAccepted = true,
                PersonalDataIdempotencyKey = request.IdempotencyKey,
                TermsIdempotencyKey = Guid.NewGuid(),
                At = now,
                ExpiresAt = now.AddMinutes(Settings.OnboardingMinutes)
            });
            return raw;
        }, token, () => ValidateOnboardingDocumentsAsync(termsId, request, token)), token, request);

    public Task CompleteOnboardingAsync(Customer customer, string? raw, CancellationToken token)
        => Run(nameof(CompleteOnboardingAsync), () => ConsentTransaction.Run(database, async () =>
        {
            var row = await ReadOnboarding(raw, token);
            var (current, terms) = await ValidateOnboardingVersions(row, token);
            if (row.PhoneHash != PhoneHash(customer.Phone)) throw new ServiceException(400, "onboarding_consent_expired");
            row.UsedAt = clock.GetUtcNow();
            database.ConsentEvents.Add(NewEvent(CustomerKey(customer.Id), customer.Id, current, "grant", [], "registration",
                row.PersonalDataIdempotencyKey ?? Guid.NewGuid(), row.At));
            database.ConsentEvents.Add(NewEvent(CustomerKey(customer.Id), customer.Id, terms, "grant", [], "registration",
                row.TermsIdempotencyKey ?? Guid.NewGuid(), row.At));
            return true;
        }, token, () => ValidateOnboardingAtCommitAsync(raw!, token)), token, customer);

    public Task<CookieConsentDto> CookieStatusAsync(string? raw, CancellationToken token) => Run(nameof(CookieStatusAsync), async () =>
    {
        var now = clock.GetUtcNow();
        var document = await LegalDocumentService.CurrentEntity(database, LegalDocumentKind.CookieConsent, now, token);
        var subject = BrowserKey(raw);
        var last = subject is null ? null : await database.ConsentEvents.AsNoTracking().Where(x => x.SubjectKey == subject)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
        var status = Status(last, document, now);
        return new CookieConsentDto(status, status == "current" ? last!.Categories : [], last?.DocumentId,
            last?.At, last?.ExpiresAt, now, await LegalDocumentService.NextChange(database, LegalDocumentKind.CookieConsent, now, token));
    }, token, null);

    public Task<CookieConsentDto> DecideCookiesAsync(string raw, ConsentDecisionRequest request, CancellationToken token) => Run(nameof(DecideCookiesAsync),
        async () =>
        {
            var wroteDecision = false;
            await ConsentTransaction.Run(database, async () =>
            {
                var subject = BrowserKey(raw) ?? throw new ServiceException(400, "invalid_consent_decision");
                var existing = await FindRetry(subject, request, LegalDocumentKind.CookieConsent, token);
                if (existing is not null) return true;
                var document = request.Decision == "withdraw"
                    ? await WithdrawalDocument(subject, LegalDocumentKind.CookieConsent, request, token)
                    : await ValidateDecision(LegalDocumentKind.CookieConsent, request, token);
                wroteDecision = true;
                database.ConsentEvents.Add(NewEvent(subject, null, document, request.Decision, request.Categories,
                    "cookie-settings", request.IdempotencyKey, clock.GetUtcNow()));
                return true;
            }, token, () => wroteDecision && request.Decision != "withdraw" ? ValidateDecision(LegalDocumentKind.CookieConsent, request, token) : Task.CompletedTask);
            return await CookieStatusAsync(raw, token);
        }, token, request);

    public Task<CustomerConsentsDto> DecidePersonalDataAsync(int customerId, ConsentDecisionRequest request, CancellationToken token) => Run(nameof(DecidePersonalDataAsync),
        async () =>
        {
            var wroteDecision = false;
            await ConsentTransaction.Run(database, async () =>
            {
                await RequireCustomer(customerId, token);
                if (request.Decision is not ("grant" or "refuse")) throw new ServiceException(400, "invalid_consent_decision");
                var subject = CustomerKey(customerId);
                if (await FindRetry(subject, request, LegalDocumentKind.PersonalDataConsent, token) is not null) return true;
                var document = await ValidateDecision(LegalDocumentKind.PersonalDataConsent, request, token);
                wroteDecision = true;
                database.ConsentEvents.Add(NewEvent(subject, customerId, document, request.Decision, [], "customer-consents",
                    request.IdempotencyKey, clock.GetUtcNow()));
                return true;
            }, token, () => wroteDecision ? ValidateDecision(LegalDocumentKind.PersonalDataConsent, request, token) : Task.CompletedTask);
            return await CustomerAsync(customerId, token);
        }, token, request);

    public Task AssociateBrowserAsync(int customerId, string? raw, Guid authenticationTokenId, CancellationToken token) => Run(nameof(AssociateBrowserAsync),
        () => ConsentTransaction.Run(database, async () =>
        {
            await RequireCustomer(customerId, token);
            if (authenticationTokenId == Guid.Empty) throw new ServiceException(401, "invalid_access_token");
            var subject = BrowserKey(raw);
            if (subject is null) return false;
            var last = await database.ConsentEvents.AsNoTracking().Where(x => x.SubjectKey == subject)
                .OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
            if (last is not null && !await database.ConsentAssociations.AnyAsync(x => x.CustomerId == customerId && x.ConsentEventId == last.Id, token))
                database.ConsentAssociations.Add(new ConsentAssociation
                { CustomerId = customerId, ConsentEventId = last.Id, AssociatedAt = clock.GetUtcNow(), AuthenticationTokenId = authenticationTokenId });
            return true;
        }, token), token, null);

    public Task<CustomerConsentsDto> CustomerAsync(int customerId, CancellationToken token) => Run(nameof(CustomerAsync), async () =>
    {
        await RequireCustomer(customerId, token);
        var now = clock.GetUtcNow();
        var events = await database.ConsentEvents.AsNoTracking().Include(x => x.Document)
            .Where(x => x.CustomerId == customerId).OrderByDescending(x => x.Id).Take(200).ToArrayAsync(token);
        var observed = await database.ConsentAssociations.AsNoTracking().Include(x => x.Event).ThenInclude(x => x.Document)
            .Where(x => x.CustomerId == customerId).OrderByDescending(x => x.Id).Take(200).ToArrayAsync(token);
        var document = await LegalDocumentService.CurrentEntity(database, LegalDocumentKind.PersonalDataConsent, now, token);
        var last = events.FirstOrDefault(x => x.Kind == LegalDocumentKind.PersonalDataConsent);
        var status = Status(last, document, now);
        var history = events.Select(x => History(x, "customer", null))
            .Concat(observed.Select(x => History(x.Event, "observed-browser", x.AssociatedAt)))
            .OrderByDescending(x => x.At).Take(200).ToArray();
        var withdrawalRequest = await database.CustomerConsentWithdrawalRequests.AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.RequestedAt)
            .FirstOrDefaultAsync(token);
        var nextChangeAt = await database.LegalDocuments.Where(x => x.Kind == LegalDocumentKind.PersonalDataConsent && x.Locale == "ru"
            && x.EffectiveAt > now).Select(x => (DateTimeOffset?)x.EffectiveAt).MinAsync(token);
        return new CustomerConsentsDto(customerId, now,
            [new(LegalDocumentKind.PersonalDataConsent, status, document?.Id, events.FirstOrDefault(x => x.Kind == LegalDocumentKind.PersonalDataConsent && x.Decision == "grant")?.DocumentId, last?.At)], history,
            withdrawalRequest is null ? null : ConsentWithdrawalRequestService.ToDto(withdrawalRequest), nextChangeAt);
    }, token, customerId);

    public Task<T> WithPersonalDataAsync<T>(int customerId, Func<Task<T>> action, CancellationToken token)
        => Run(nameof(WithPersonalDataAsync), () => ConsentTransaction.Run(database, async () =>
        {
            await RequireCustomer(customerId, token);
            var current = await RequireDocument(LegalDocumentKind.PersonalDataConsent, token);
            var last = await database.ConsentEvents.AsNoTracking().Where(x => x.CustomerId == customerId && x.Kind == LegalDocumentKind.PersonalDataConsent)
                .OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
            if (Status(last, current, clock.GetUtcNow()) != "current") throw new ServiceException(409, "personal_data_consent_required");
            var result = await action();
            var atCommit = await RequireDocument(LegalDocumentKind.PersonalDataConsent, token);
            if (atCommit.Id != current.Id) throw Changed(atCommit);
            return result;
        }, token), token, customerId);

    private async Task<ConsentOnboarding> ReadOnboarding(string? raw, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new ServiceException(400, "consent_required");
        var hash = JwtTokenService.HashRefreshToken(raw);
        var row = await database.ConsentOnboarding.SingleOrDefaultAsync(x => x.TokenHash == hash, token);
        if (row is null || row.UsedAt is not null || row.ExpiresAt <= clock.GetUtcNow())
            throw new ServiceException(400, "onboarding_consent_expired");
        return row;
    }

    private async Task<(LegalDocument PersonalData, LegalDocument Agreement)> ValidateOnboardingVersions(ConsentOnboarding row, CancellationToken token)
    {
        var current = await RequireDocument(LegalDocumentKind.PersonalDataConsent, token);
        if (current.Id != row.PersonalDataDocumentId || current.ContentHash != row.PersonalDataHash) throw Changed(current);
        var terms = await RequireDocument(LegalDocumentKind.UserAgreement, token);
        if (terms.Id != row.TermsDocumentId || terms.ContentHash != row.TermsHash) throw Changed(terms);
        return (current, terms);
    }

    internal async Task ValidateOnboardingAtCommitAsync(string raw, CancellationToken token)
    {
        var hash = JwtTokenService.HashRefreshToken(raw);
        var row = await database.ConsentOnboarding.SingleAsync(x => x.TokenHash == hash, token);
        if (row.ExpiresAt <= clock.GetUtcNow()) throw new ServiceException(400, "onboarding_consent_expired");
        await ValidateOnboardingVersions(row, token);
    }

    private Task<T> Run<T>(string name, Func<Task<T>> action, CancellationToken token, object? input) => OperationLogging.RunAsync(logger,
        $"{typeof(ConsentService).FullName}.{name}", () => LogValueSummary.Inputs(("request", input)), action, token);
    private Task RequireCustomer(int id, CancellationToken token) => RequireCustomerCore(id, token);
    private async Task RequireCustomerCore(int id, CancellationToken token)
    {
        if (!await database.Customers.AnyAsync(x => x.Id == id && x.State != CustomerState.Disabled, token))
            throw new ServiceException(404, "customer_not_found");
    }
    private async Task<LegalDocument> RequireDocument(LegalDocumentKind kind, CancellationToken token) =>
        await LegalDocumentService.CurrentEntity(database, kind, clock.GetUtcNow(), token) is { } value ? value
            : throw new ServiceException(409, "consent_document_unavailable");

    private async Task<LegalDocument> ValidateDecision(LegalDocumentKind kind, ConsentDecisionRequest request, CancellationToken token)
    {
        ValidateShape(request, kind);
        var document = await RequireDocument(kind, token);
        if (document.Id != request.DocumentId || document.ContentHash != request.ContentHash)
            throw Changed(document);
        if (request.Categories.Any(category => !document.CookieCategories.Contains(category))
            || kind == LegalDocumentKind.CookieConsent && request.Decision == "grant"
            && document.CookieCategories.Where(category => category.IsRequired()).Any(category => !request.Categories.Contains(category)))
            throw new ServiceException(400, "invalid_consent_categories");
        return document;
    }
    private async Task<LegalDocument> WithdrawalDocument(string subject, LegalDocumentKind kind, ConsentDecisionRequest request, CancellationToken token)
    {
        ValidateShape(request, kind);
        var last = await database.ConsentEvents.Include(x => x.Document).Where(x => x.SubjectKey == subject && x.Kind == kind)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
        if (last is null || last.DocumentId != request.DocumentId || last.ContentHash != request.ContentHash)
            throw new ServiceException(400, "invalid_consent_decision");
        return last.Document;
    }
    private async Task<ConsentEvent?> FindRetry(string subject, ConsentDecisionRequest request, LegalDocumentKind kind, CancellationToken token)
    {
        ValidateShape(request, kind);
        var query = database.ConsentEvents.AsNoTracking().Where(x => x.IdempotencyKey == request.IdempotencyKey);
        query = kind == LegalDocumentKind.CookieConsent ? query.Where(x => x.Kind == LegalDocumentKind.CookieConsent) : query.Where(x => x.SubjectKey == subject);
        var found = await query.SingleOrDefaultAsync(token);
        if (found is null && await database.ConsentReplayTombstones.AnyAsync(x => x.KeyHash == ReplayKey(subject, request.IdempotencyKey, kind), token))
            throw new ServiceException(409, "consent_conflict");
        if (found is not null && (found.SubjectKey != subject || found.DocumentId != request.DocumentId || found.ContentHash != request.ContentHash
            || found.Decision != request.Decision || !found.Categories.SequenceEqual(request.Categories.Order())))
            throw new ServiceException(409, "consent_conflict");
        return found;
    }
    internal static string ReplayKey(string subject, Guid key, LegalDocumentKind kind) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"{(kind == LegalDocumentKind.CookieConsent ? "browser" : subject)}:{key:D}:{(int)kind}"))));

    private static ServiceException Changed(LegalDocument document) => new(409, "consent_version_changed")
    { RequiredDocumentId = document.Id, ConsentKind = document.Kind };
    private static ServiceException InvalidAuthenticationRequest() => new(400, "invalid_auth_request");
    private static void ValidateShape(ConsentDecisionRequest request, LegalDocumentKind kind)
    {
        if (request.IdempotencyKey == Guid.Empty || request.Decision is not ("grant" or "refuse" or "withdraw"))
            throw new ServiceException(400, "invalid_consent_decision");

        if (kind != LegalDocumentKind.CookieConsent)
        {
            if (request.Categories is null || request.Categories.Length > 0)
                throw new ServiceException(400, "invalid_consent_decision");
            return;
        }

        if (request.Categories is null || request.Categories.Length > Enum.GetValues<CookieCategory>().Length
            || request.Categories.Distinct().Count() != request.Categories.Length
            || request.Categories.Any(category => !Enum.IsDefined(category))
            || request.Decision != "grant" && request.Categories.Length > 0)
            throw new ServiceException(400, "invalid_consent_categories");
    }
    private ConsentEvent NewEvent(string subject, int? customer, LegalDocument document, string decision, CookieCategory[] categories,
        string source, Guid key, DateTimeOffset at) => new()
        {
            SubjectKey = subject,
            CustomerId = customer,
            DocumentId = document.Id,
            ContentHash = document.ContentHash,
            Kind = document.Kind,
            Decision = decision,
            Categories = categories.Order().ToArray(),
            Source = source,
            IdempotencyKey = key,
            At = at,
            ExpiresAt = document.Kind == LegalDocumentKind.CookieConsent ? at.AddDays(Settings.CookieDays) : null,
            RetainUntil = at.AddDays(Settings.EvidenceDays)
        };
    private string PhoneHash(string phone) => Convert.ToHexStringLower(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(authentication.Value.SigningKey), Encoding.UTF8.GetBytes("consent-onboarding:" + phone)));
    private static string CustomerKey(int id) => $"customer:{id}";
    private static string? BrowserKey(string? raw) => raw is { Length: >= 32 and <= 128 }
        ? "browser:" + JwtTokenService.HashRefreshToken(raw) : null;
    internal static string Status(ConsentEvent? last, LegalDocument? document, DateTimeOffset now)
    {
        if (document is null) return "unavailable";
        if (last is null) return "missing";
        if (last.DocumentId != document.Id || last.ExpiresAt <= now) return "renewal-required";
        if (last.Decision == "grant" && document.Kind == LegalDocumentKind.CookieConsent
            && document.CookieCategories.Where(category => category.IsRequired()).Any(category => !last.Categories.Contains(category)))
            return "renewal-required";
        return last.Decision switch { "grant" => "current", "withdraw" => "withdrawn", _ => "refused" };
    }
    private static ConsentHistoryDto History(ConsentEvent row, string scope, DateTimeOffset? associated) => new(
        row.Id.ToString(), row.Kind, row.Decision, row.DocumentId, row.Document.DisplayVersion, row.ContentHash,
        row.Categories, row.At, row.Source, scope, associated);
}
