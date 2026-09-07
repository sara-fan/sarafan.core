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

    public Task ValidateOnboardingDocumentsAsync(Guid termsId, ConsentDecisionRequest request, CancellationToken token)
        => Run(nameof(ValidateOnboardingDocumentsAsync), async () =>
        {
            await ValidateDecision(ConsentKinds.PersonalData, request, token);
            if (request.Decision != "grant") throw new ServiceException(400, "consent_required");
            var agreement = await RequireDocument(ConsentKinds.Agreement, token);
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
            var document = await ValidateDecision(ConsentKinds.PersonalData, request, token);
            if (request.Decision != "grant") throw new ServiceException(400, "consent_required");
            var agreement = await RequireDocument(ConsentKinds.Agreement, token);
            if (agreement.Id != termsId) throw Changed(agreement);
            var raw = JwtTokenService.CreateRefreshToken();
            var now = clock.GetUtcNow();
            database.ConsentOnboarding.Add(new ConsentOnboarding
            {
                TokenHash = JwtTokenService.HashRefreshToken(raw),
                PhoneHash = PhoneHash(phone),
                PersonalDataDocumentId = document.Id,
                TermsDocumentId = agreement.Id,
                PersonalDataHash = document.ContentHash,
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
            database.ConsentEvents.Add(NewEvent(CustomerKey(customer.Id), customer.Id, current, "grant", [], "registration", Guid.NewGuid(), row.At));
            database.ConsentEvents.Add(NewEvent(CustomerKey(customer.Id), customer.Id, terms, "grant", [], "registration", Guid.NewGuid(), row.At));
            return true;
        }, token, () => ValidateOnboardingAtCommitAsync(raw!, token)), token, customer);

    public Task<CookieConsentDto> CookieStatusAsync(string? raw, CancellationToken token) => Run(nameof(CookieStatusAsync), async () =>
    {
        var now = clock.GetUtcNow();
        var document = await LegalDocumentService.CurrentEntity(database, ConsentKinds.Cookies, now, token);
        var subject = BrowserKey(raw);
        var last = subject is null ? null : await database.ConsentEvents.AsNoTracking().Where(x => x.SubjectKey == subject)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
        var status = Status(last, document, now);
        return new CookieConsentDto(status, status == "current" ? last!.Categories : [], last?.DocumentId,
            last?.At, last?.ExpiresAt, now, await LegalDocumentService.NextChange(database, ConsentKinds.Cookies, now, token));
    }, token, null);

    public Task<CookieConsentDto> DecideCookiesAsync(string raw, ConsentDecisionRequest request, CancellationToken token) => Run(nameof(DecideCookiesAsync),
        async () =>
        {
            var wroteDecision = false;
            await ConsentTransaction.Run(database, async () =>
            {
                var subject = BrowserKey(raw) ?? throw new ServiceException(400, "invalid_consent_decision");
                var existing = await FindRetry(subject, request, ConsentKinds.Cookies, token);
                if (existing is not null) return true;
                var document = request.Decision == "withdraw"
                    ? await WithdrawalDocument(subject, ConsentKinds.Cookies, request, token)
                    : await ValidateDecision(ConsentKinds.Cookies, request, token);
                wroteDecision = true;
                database.ConsentEvents.Add(NewEvent(subject, null, document, request.Decision, request.Categories,
                    "cookie-settings", request.IdempotencyKey, clock.GetUtcNow()));
                return true;
            }, token, () => wroteDecision && request.Decision != "withdraw" ? ValidateDecision(ConsentKinds.Cookies, request, token) : Task.CompletedTask);
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
                if (await FindRetry(subject, request, ConsentKinds.PersonalData, token) is not null) return true;
                var document = await ValidateDecision(ConsentKinds.PersonalData, request, token);
                wroteDecision = true;
                database.ConsentEvents.Add(NewEvent(subject, customerId, document, request.Decision, [], "customer-consents",
                    request.IdempotencyKey, clock.GetUtcNow()));
                return true;
            }, token, () => wroteDecision ? ValidateDecision(ConsentKinds.PersonalData, request, token) : Task.CompletedTask);
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
        var document = await LegalDocumentService.CurrentEntity(database, ConsentKinds.PersonalData, now, token);
        var last = events.FirstOrDefault(x => x.Kind == ConsentKinds.PersonalData);
        var status = Status(last, document, now);
        var history = events.Select(x => History(x, "customer", null))
            .Concat(observed.Select(x => History(x.Event, "observed-browser", x.AssociatedAt)))
            .OrderByDescending(x => x.At).Take(200).ToArray();
        var cases = await database.ConsentRightsCases.AsNoTracking().Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.ReceivedAt).Take(200).ToArrayAsync(token);
        var nextChangeAt = await database.LegalDocuments.Where(x => x.Kind == ConsentKinds.PersonalData && x.Locale == "ru"
            && x.State == "published" && x.EffectiveAt > now).MinAsync(x => x.EffectiveAt, token);
        return new CustomerConsentsDto(customerId, now,
            [new(ConsentKinds.PersonalData, status, document?.Id, events.FirstOrDefault(x => x.Kind == ConsentKinds.PersonalData && x.Decision == "grant")?.DocumentId, last?.At)], history,
            cases.Select(ConsentRightsService.ToDto).ToArray(), nextChangeAt);
    }, token, customerId);

    public Task<T> WithPersonalDataAsync<T>(int customerId, Func<Task<T>> action, CancellationToken token)
        => Run(nameof(WithPersonalDataAsync), () => ConsentTransaction.Run(database, async () =>
        {
            await RequireCustomer(customerId, token);
            var current = await RequireDocument(ConsentKinds.PersonalData, token);
            var last = await database.ConsentEvents.AsNoTracking().Where(x => x.CustomerId == customerId && x.Kind == ConsentKinds.PersonalData)
                .OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
            if (Status(last, current, clock.GetUtcNow()) != "current") throw new ServiceException(409, "personal_data_consent_required");
            var result = await action();
            var atCommit = await RequireDocument(ConsentKinds.PersonalData, token);
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
        var current = await RequireDocument(ConsentKinds.PersonalData, token);
        if (current.Id != row.PersonalDataDocumentId || current.ContentHash != row.PersonalDataHash) throw Changed(current);
        var terms = await RequireDocument(ConsentKinds.Agreement, token);
        if (terms.Id != row.TermsDocumentId) throw Changed(terms);
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
    private async Task<LegalDocument> RequireDocument(string kind, CancellationToken token) =>
        await LegalDocumentService.CurrentEntity(database, kind, clock.GetUtcNow(), token) is { DisposedAt: null } value ? value
            : throw new ServiceException(409, "consent_document_unavailable");

    private async Task<LegalDocument> ValidateDecision(string kind, ConsentDecisionRequest request, CancellationToken token)
    {
        ValidateShape(request);
        var document = await RequireDocument(kind, token);
        if (document.Id != request.DocumentId || document.ContentHash != request.ContentHash)
            throw Changed(document);
        if (request.Categories.Any(x => !document.CookieCategories.Contains(x))) throw new ServiceException(400, "invalid_consent_decision");
        return document;
    }
    private async Task<LegalDocument> WithdrawalDocument(string subject, string kind, ConsentDecisionRequest request, CancellationToken token)
    {
        ValidateShape(request);
        var last = await database.ConsentEvents.Include(x => x.Document).Where(x => x.SubjectKey == subject && x.Kind == kind)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(token);
        if (last is null || last.DocumentId != request.DocumentId || last.ContentHash != request.ContentHash)
            throw new ServiceException(400, "invalid_consent_decision");
        return last.Document;
    }
    private async Task<ConsentEvent?> FindRetry(string subject, ConsentDecisionRequest request, string kind, CancellationToken token)
    {
        ValidateShape(request);
        var query = database.ConsentEvents.AsNoTracking().Where(x => x.IdempotencyKey == request.IdempotencyKey);
        query = kind == ConsentKinds.Cookies ? query.Where(x => x.Kind == ConsentKinds.Cookies) : query.Where(x => x.SubjectKey == subject);
        var found = await query.SingleOrDefaultAsync(token);
        if (found is null && await database.ConsentReplayTombstones.AnyAsync(x => x.KeyHash == ReplayKey(subject, request.IdempotencyKey, kind), token))
            throw new ServiceException(409, "consent_conflict");
        if (found is not null && (found.SubjectKey != subject || found.DocumentId != request.DocumentId || found.ContentHash != request.ContentHash
            || found.Decision != request.Decision || !found.Categories.SequenceEqual(request.Categories.Order())))
            throw new ServiceException(409, "consent_conflict");
        return found;
    }
    internal static string ReplayKey(string subject, Guid key, string kind) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{(kind == ConsentKinds.Cookies ? "browser" : subject)}:{key:D}")));

    private static ServiceException Changed(LegalDocument document) => new(409, "consent_version_changed")
    { RequiredDocumentId = document.Id, ConsentKind = document.Kind };
    private static void ValidateShape(ConsentDecisionRequest request)
    {
        if (request.IdempotencyKey == Guid.Empty || request.Decision is not ("grant" or "refuse" or "withdraw")
            || request.Categories is null || request.Categories.Length > 2 || request.Categories.Distinct().Count() != request.Categories.Length
            || request.Decision != "grant" && request.Categories.Length > 0)
            throw new ServiceException(400, "invalid_consent_decision");
    }
    private ConsentEvent NewEvent(string subject, int? customer, LegalDocument document, string decision, string[] categories,
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
            ExpiresAt = document.Kind == ConsentKinds.Cookies ? at.AddDays(Settings.CookieDays) : null,
            RetainUntil = at.AddDays(Settings.EvidenceDays)
        };
    private string PhoneHash(string phone) => Convert.ToHexStringLower(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(authentication.Value.SigningKey), Encoding.UTF8.GetBytes("consent-onboarding:" + phone)));
    private static string CustomerKey(int id) => $"customer:{id}";
    private static string? BrowserKey(string? raw) => raw is { Length: >= 32 and <= 128 }
        ? "browser:" + JwtTokenService.HashRefreshToken(raw) : null;
    internal static string Status(ConsentEvent? last, LegalDocument? document, DateTimeOffset now)
    {
        if (last?.Decision == "withdraw" && last.Kind != ConsentKinds.Cookies) return "withdrawn";
        if (document is null || document.DisposedAt is not null) return "unavailable";
        if (last is null) return "missing";
        if (last.DocumentId != document.Id || last.ExpiresAt <= now) return "renewal-required";
        return last.Decision switch { "grant" => "current", "withdraw" => "withdrawn", _ => "refused" };
    }
    private static ConsentHistoryDto History(ConsentEvent row, string scope, DateTimeOffset? associated) => new(
        row.Id.ToString(), row.Kind, row.Decision, row.DocumentId, row.Document.DisplayVersion, row.ContentHash,
        row.Categories, row.At, row.Source, scope, associated);
}
