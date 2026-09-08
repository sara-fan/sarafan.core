// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture, NonParallelizable]
public sealed class ConsentPolicyTests
{
    private AsyncServiceScope _scope;
    private AppDbContext _db = null!;
    private IDbContextTransaction _transaction = null!;
    private TestClock _clock = null!;
    private ConsentOptions _options = null!;
    private LegalDocumentService _documents = null!;
    private ConsentService _consents = null!;
    private ConsentWithdrawalRequestService _withdrawalRequests = null!;
    private ConsentRetentionService _retention = null!;
    private int _customer;
    private int _admin;
    private const string Browser = "browser-test-receipt-at-least-thirty-two-characters";

    [SetUp]
    public async Task SetUp()
    {
        _scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _transaction = await _db.Database.BeginTransactionAsync();
        _clock = new() { Now = DateTimeOffset.UtcNow.AddDays(1) };
        _options = new ConsentOptions();
        _documents = new(_db, _clock, NullLogger<LegalDocumentService>.Instance);
        _consents = new(_db, _clock, Options.Create(_options), _scope.ServiceProvider.GetRequiredService<IOptions<AuthenticationOptions>>(), NullLogger<ConsentService>.Instance);
        _withdrawalRequests = new(_db, _clock, NullLogger<ConsentWithdrawalRequestService>.Instance);
        _retention = new(_db, _clock, NullLogger<ConsentRetentionService>.Instance);
        _admin = await _db.BackofficeUsers.MinAsync(x => x.Id);
        var customer = new Customer { Phone = "+78880000001", Profile = new() };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        _customer = customer.Id;
    }
    [TearDown]
    public async Task TearDown() { await _transaction.RollbackAsync(); await _transaction.DisposeAsync(); await _scope.DisposeAsync(); }

    private async Task<LegalDocumentDto> CreateDocument(LegalDocumentKind kind = LegalDocumentKind.PersonalDataConsent, string? version = null, DateOnly? effectiveDate = null)
    {
        var result = await _documents.CreateAsync(new()
        {
            Kind = kind,
            Title = "Согласие",
            DisplayVersion = version ?? Guid.NewGuid().ToString(),
            FileName = "consent.md",
            Source = Encoding.UTF8.GetBytes("# Согласие\n\nТекст **согласия**."),
            EffectiveDate = effectiveDate ?? ConsentCalendar.LocalDate(_clock.Now)
        }, _admin, default);
        await _db.SaveChangesAsync();
        return result;
    }
    private async Task<LegalDocumentDto> CreateCurrent(LegalDocumentKind kind = LegalDocumentKind.PersonalDataConsent)
    {
        _clock.Now = _clock.Now.AddSeconds(1);
        return await CreateDocument(kind);
    }
    private static ConsentDecisionRequest Decision(LegalDocumentDto doc, string decision = "grant", params CookieCategory[] categories)
        => new()
        {
            DocumentId = doc.Id,
            ContentHash = doc.ContentHash,
            Decision = decision,
            Categories = categories.Length == 0 && decision == "grant" && doc.Kind == LegalDocumentKind.CookieConsent
                ? [CookieCategory.Mandatory]
                : categories,
            IdempotencyKey = Guid.NewGuid()
        };
    private static void Reject(Func<Task> action, string code) => Assert.That(async () => await action(), Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo(code));

    [Test]
    public void LegalDocumentKindsHaveStableValuesNamesAndAliases()
    {
        var expected = new[]
        {
            (LegalDocumentKind.CookieConsent, 0, "Согласие на куки", "cookie-consent"),
            (LegalDocumentKind.PersonalDataConsent, 1, "Согласие на обработку персональных данных", "personal-data-consent"),
            (LegalDocumentKind.UserAgreement, 2, "Пользовательское соглашение", "user-agreement"),
            (LegalDocumentKind.OrderRules, 3, "Правила заказа товаров", "order-rules"),
            (LegalDocumentKind.PrivacyPolicy, 4, "Политика обработки персональных данных", "privacy-policy")
        };

        var operations = LegalDocumentService.Operations().Kinds;
        Assert.That(operations.Select(item => item.Value), Is.EqualTo(expected.Select(item => item.Item2)));
        Assert.That(operations.Select(item => item.Name), Is.EqualTo(expected.Select(item => item.Item3)));
        Assert.That(operations.Select(item => item.RouteAlias), Is.EqualTo(expected.Select(item => item.Item4)));
        Assert.That(operations.Select(item => item.RouteAlias).Distinct().Count(), Is.EqualTo(expected.Length));
        var categories = LegalDocumentService.Operations().CookieCategories;
        Assert.That(categories, Has.Count.EqualTo(1));
        Assert.That(categories.Single(), Is.EqualTo(new CookieCategoryOpsItemDto(0, "Обязательные", true)));
        Assert.That((int)CookieCategory.Mandatory, Is.Zero);
        Assert.That(CookieCategory.Mandatory.GetDisplayName(), Is.EqualTo("Обязательные"));
        Assert.That(CookieCategory.Mandatory.IsRequired(), Is.True);
        foreach (var item in expected)
        {
            Assert.That((int)item.Item1, Is.EqualTo(item.Item2));
            Assert.That(item.Item1.GetDisplayName(), Is.EqualTo(item.Item3));
            Assert.That(item.Item1.GetRouteAlias(), Is.EqualTo(item.Item4));
            Assert.That(LegalDocumentKindExtensions.TryFromRouteAlias(item.Item4, out var resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(item.Item1));
        }
        Assert.That(LegalDocumentKindExtensions.TryFromRouteAlias("unknown", out _), Is.False);
        Assert.That(() => ((LegalDocumentKind)99).GetDisplayName(), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => ((LegalDocumentKind)99).GetRouteAlias(), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void ReplayHashesEncodeNumericKindValues()
    {
        var key = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        Assert.That(ConsentService.ReplayKey("customer:42", key, LegalDocumentKind.CookieConsent),
            Is.EqualTo(Hash("browser:12345678-1234-1234-1234-1234567890ab:0")));
        Assert.That(ConsentService.ReplayKey("customer:42", key, LegalDocumentKind.PersonalDataConsent),
            Is.EqualTo(Hash("customer:42:12345678-1234-1234-1234-1234567890ab:1")));
    }

    [Test]
    public async Task Documents_AreImmutable_EffectiveAtMoscowMidnight_AndRequireRenewal()
    {
        var first = await CreateCurrent();
        Assert.That(first.CanDelete, Is.False);
        await _consents.DecidePersonalDataAsync(_customer, Decision(first), default); await _db.SaveChangesAsync();
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses[0].Status, Is.EqualTo("current"));
        var date = ConsentCalendar.LocalDate(_clock.Now).AddDays(3);
        var future = await CreateDocument(effectiveDate: date);
        Assert.That(future.CanDelete, Is.True);
        Assert.That(future.EffectiveAt, Is.EqualTo(ConsentCalendar.Midnight(date)));
        Reject(() => _documents.ReadAsync(future.Id, false, default), "legal_document_not_found");
        Assert.That((await _documents.CurrentAsync(LegalDocumentKind.PersonalDataConsent, default)).NextChangeAt, Is.EqualTo(future.EffectiveAt));
        Assert.That((await _consents.CustomerAsync(_customer, default)).NextChangeAt, Is.EqualTo(future.EffectiveAt));
        _clock.Now = future.EffectiveAt.AddTicks(-1);
        Assert.That((await _documents.CurrentAsync(LegalDocumentKind.PersonalDataConsent, default)).Document!.Id, Is.EqualTo(first.Id));
        _clock.Now = future.EffectiveAt;
        Assert.That((await _documents.ReadAsync(first.Id, false, default)).Id, Is.EqualTo(first.Id));
        Assert.That((await _documents.CurrentAsync(LegalDocumentKind.PersonalDataConsent, default)).Document!.Id, Is.EqualTo(future.Id));
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses[0].Status, Is.EqualTo("renewal-required"));
        Reject(() => _consents.DecidePersonalDataAsync(_customer, Decision(first), default), "consent_version_changed");
        Reject(() => _documents.DeleteAsync(future.Id, _admin, default), "legal_document_already_effective");
        Assert.That(await _db.LegalDocumentAuditEvents.CountAsync(x => x.DocumentId == future.Id), Is.EqualTo(1));
        var renewed = await _consents.DecidePersonalDataAsync(_customer, Decision(future), default); await _db.SaveChangesAsync();
        // Nested transaction is saved by the caller; reload after save.
        renewed = await _consents.CustomerAsync(_customer, default);
        Assert.That(renewed.History.Count(x => x.Kind == LegalDocumentKind.PersonalDataConsent), Is.EqualTo(2));
        Assert.That(await _documents.ListAsync(null, default), Has.Length.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task Validation_Uniqueness_Deletion_AndAuditAreEnforced()
    {
        Reject(() => _documents.CurrentAsync((LegalDocumentKind)99, default), "invalid_legal_document_kind");
        Reject(() => _documents.ListAsync((LegalDocumentKind)99, default), "invalid_legal_document_kind");
        Reject(() => _documents.ReadAsync(Guid.NewGuid(), true, default), "legal_document_not_found");
        Reject(() => _documents.DeleteAsync(Guid.NewGuid(), _admin, default), "legal_document_not_found");
        var date = ConsentCalendar.LocalDate(_clock.Now).AddDays(5);
        var request = new LegalDocumentRequest { Kind = LegalDocumentKind.PersonalDataConsent, Title = "Документ", FileName = "new.md", Source = Encoding.UTF8.GetBytes("Новая редакция"), EffectiveDate = date };
        var preview = await _documents.PreviewAsync(request, default);
        Reject(() => _documents.CreateAsync(request, _admin, default), "invalid_legal_document_version");
        request.DisplayVersion = "immutable";
        Assert.That(await _db.LegalDocuments.AnyAsync(x => x.DisplayVersion == "immutable"), Is.False);
        Assert.That(await _db.LegalDocumentAuditEvents.AnyAsync(x => x.DisplayVersion == "immutable"), Is.False);
        var document = await _documents.CreateAsync(request, _admin, default); await _db.SaveChangesAsync();
        Assert.That(document.Html, Is.EqualTo(preview.Html));
        Assert.That(document.ContentHash, Has.Length.EqualTo(64));
        Assert.That(await _documents.DownloadAsync(document.Id, true, default), Is.EqualTo(request.Source));
        request.EffectiveDate = date.AddDays(1);
        Reject(() => _documents.CreateAsync(request, _admin, default), "legal_document_version_conflict");
        request.DisplayVersion = "another"; request.EffectiveDate = date;
        Reject(() => _documents.CreateAsync(request, _admin, default), "legal_document_effective_date_conflict");
        request.EffectiveDate = ConsentCalendar.LocalDate(_clock.Now).AddDays(-1);
        Reject(() => _documents.CreateAsync(request, _admin, default), "invalid_effective_date");
        request.EffectiveDate = date.AddDays(1); request.Locale = "en";
        Reject(() => _documents.CreateAsync(request, _admin, default), "invalid_legal_document_locale");
        Assert.That(await _db.LegalDocumentAuditEvents.CountAsync(x => x.DocumentId == document.Id), Is.EqualTo(1));
        await _documents.DeleteAsync(document.Id, _admin, default); await _db.SaveChangesAsync();
        Assert.That(await _db.LegalDocuments.AnyAsync(x => x.Id == document.Id), Is.False);
        var audit = await _documents.AuditAsync(null, null, null, document.Id, 1, 50, default);
        Assert.That(audit.Items.Select(x => x.Action), Is.EqualTo(new[] { "deleted", "created" }));
        Assert.That(audit.Items, Has.All.Property(nameof(LegalDocumentAuditDto.ContentHash)).EqualTo(document.ContentHash));
        var searched = await _documents.AuditAsync(LegalDocumentKind.PersonalDataConsent, "deleted", "Документ", null, 1, 1, default);
        Assert.That(searched.Total, Is.EqualTo(1));
        Assert.That(searched.Items.Single().DocumentId, Is.EqualTo(document.Id));
        Reject(() => _documents.AuditAsync(null, "changed", null, null, 1, 50, default), "invalid_legal_document_audit_filter");
        Reject(() => _documents.AuditAsync(null, null, new string('x', 201), null, 1, 50, default), "invalid_legal_document_audit_filter");
    }

    [Test]
    public async Task DocumentMetadataValidationReportsTheRejectedField()
    {
        var request = new LegalDocumentRequest
        {
            Kind = LegalDocumentKind.PersonalDataConsent,
            Locale = "ru",
            Title = "Документ",
            DisplayVersion = "v1",
            FileName = "document.md",
            Source = Encoding.UTF8.GetBytes("Текст"),
            EffectiveDate = ConsentCalendar.LocalDate(_clock.Now).AddDays(1)
        };
        request.Kind = null;
        Reject(() => _documents.PreviewAsync(request, default), "invalid_legal_document_kind");
        request.Kind = LegalDocumentKind.PersonalDataConsent; request.Locale = "en";
        Reject(() => _documents.PreviewAsync(request, default), "invalid_legal_document_locale");
        request.Locale = "ru"; request.Title = " ";
        Reject(() => _documents.PreviewAsync(request, default), "invalid_legal_document_title");
        request.Title = "Документ"; request.DisplayVersion = new string('v', 65);
        Reject(() => _documents.PreviewAsync(request, default), "invalid_legal_document_version");
        request.DisplayVersion = null!;
        Assert.That((await _documents.PreviewAsync(request, default)).Html, Does.Contain("Текст"));
        Reject(() => _documents.CreateAsync(request, _admin, default), "invalid_legal_document_version");
        request.DisplayVersion = "v1";
    }

    [Test]
    public async Task BrowserChoices_AreIdempotent_Expire_AndNeverBecomeAccountAuthorization()
    {
        var doc = await CreateCurrent(LegalDocumentKind.CookieConsent);
        Assert.That((await _consents.CookieStatusAsync(null, default)).Status, Is.EqualTo("missing"));
        Assert.That(doc.CookieCategories, Is.EqualTo(new[] { CookieCategory.Mandatory }));
        var grant = Decision(doc);
        await _consents.DecideCookiesAsync(Browser, grant, default); await _db.SaveChangesAsync();
        Assert.That((await _consents.CookieStatusAsync(Browser, default)).Categories, Is.EqualTo(new[] { CookieCategory.Mandatory }));
        await _consents.DecideCookiesAsync(Browser, grant, default); await _db.SaveChangesAsync();
        Assert.That(await _db.ConsentEvents.CountAsync(x => x.IdempotencyKey == grant.IdempotencyKey), Is.EqualTo(1));
        grant.Categories = [];
        Reject(() => _consents.DecideCookiesAsync(Browser, grant, default), "consent_conflict");
        Reject(() => _consents.DecideCookiesAsync("short", Decision(doc), default), "invalid_consent_decision");
        Reject(() => _consents.DecideCookiesAsync(Browser, Decision(doc, "grant", (CookieCategory)99), default), "invalid_consent_categories");
        Reject(() => _consents.DecideCookiesAsync(Browser, Decision(doc, "refuse", CookieCategory.Mandatory), default), "invalid_consent_categories");
        Reject(() => _consents.DecideCookiesAsync(Browser, Decision(doc, "grant", CookieCategory.Mandatory, CookieCategory.Mandatory), default), "invalid_consent_categories");
        var missingMandatory = Decision(doc); missingMandatory.Categories = [];
        Reject(() => _consents.DecideCookiesAsync(Browser, missingMandatory, default), "invalid_consent_categories");
        var invalid = Decision(doc); invalid.IdempotencyKey = Guid.Empty;
        Reject(() => _consents.DecideCookiesAsync(Browser, invalid, default), "invalid_consent_decision");
        await _consents.AssociateBrowserAsync(_customer, null, Guid.NewGuid(), default);
        await _consents.AssociateBrowserAsync(_customer, Browser, Guid.NewGuid(), default); await _db.SaveChangesAsync();
        await _consents.AssociateBrowserAsync(_customer, Browser, Guid.NewGuid(), default); await _db.SaveChangesAsync();
        var mine = await _consents.CustomerAsync(_customer, default);
        Assert.That(mine.History.Single().Scope, Is.EqualTo("observed-browser"));
        Assert.That(mine.Statuses[0].Status, Is.EqualTo("missing"));
        Assert.That((await _consents.CookieStatusAsync("another-browser-with-at-least-thirty-two-characters", default)).Categories, Is.Empty);
        _clock.Now = _clock.Now.AddDays(_options.CookieDays);
        Assert.That((await _consents.CookieStatusAsync(Browser, default)).Status, Is.EqualTo("renewal-required"));
        await CreateCurrent(LegalDocumentKind.CookieConsent);
        await _consents.DecideCookiesAsync(Browser, Decision(doc, "withdraw"), default); await _db.SaveChangesAsync();
        // The old grant can still be withdrawn, while the replacement requires a fresh decision.
        Assert.That((await _consents.CookieStatusAsync(Browser, default)).Status, Is.EqualTo("renewal-required"));
        Assert.That(await _db.ConsentEvents.AnyAsync(x => x.DocumentId == doc.Id && x.Decision == "withdraw"), Is.True);
        var wrong = Decision(doc, "withdraw"); wrong.ContentHash = "wrong";
        Reject(() => _consents.DecideCookiesAsync(Browser, wrong, default), "invalid_consent_decision");
    }

    [Test]
    public async Task CookieWithdrawal_RequiresANewDecisionAfterExpiryOrReplacement()
    {
        var doc = await CreateCurrent(LegalDocumentKind.CookieConsent);
        await _consents.DecideCookiesAsync(Browser, Decision(doc), default); await _db.SaveChangesAsync();
        await _consents.DecideCookiesAsync(Browser, Decision(doc, "withdraw"), default); await _db.SaveChangesAsync();
        Assert.That((await _consents.CookieStatusAsync(Browser, default)).Status, Is.EqualTo("withdrawn"));
        _clock.Now = _clock.Now.AddDays(180);
        Assert.That((await _consents.CookieStatusAsync(Browser, default)).Status, Is.EqualTo("renewal-required"));
        await _consents.DecideCookiesAsync(Browser, Decision(doc, "withdraw"), default); await _db.SaveChangesAsync();
        await CreateCurrent(LegalDocumentKind.CookieConsent);
        var status = await _consents.CookieStatusAsync(Browser, default);
        Assert.That(status.Status, Is.EqualTo("renewal-required"));
        Assert.That(status.Categories, Is.Empty);
    }

    [Test]
    public async Task WithdrawalRequest_IsQueueOnly_Idempotent_AndAllowsANewRequestAfterProcessing()
    {
        var document = await CreateCurrent();
        var missing = await _consents.CustomerAsync(_customer, default);
        Assert.That(missing.Statuses[0].Status, Is.EqualTo("missing"));
        Assert.That(missing.History, Is.Empty);
        Reject(() => _consents.WithPersonalDataAsync(_customer, () => Task.FromResult(true), default), "personal_data_consent_required");
        await _consents.DecidePersonalDataAsync(_customer, Decision(document, "refuse"), default); await _db.SaveChangesAsync();
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses[0].Status, Is.EqualTo("refused"));
        Reject(() => _consents.DecidePersonalDataAsync(_customer, Decision(document, "withdraw"), default), "invalid_consent_decision");
        Reject(() => _consents.DecidePersonalDataAsync(_customer, Decision(document, "grant", CookieCategory.Mandatory), default),
            "invalid_consent_decision");
        var grant = Decision(document);
        await _consents.DecidePersonalDataAsync(_customer, grant, default); await _db.SaveChangesAsync();
        await _consents.DecidePersonalDataAsync(_customer, grant, default);
        Assert.That(await _consents.WithPersonalDataAsync(_customer, () => Task.FromResult(42), default), Is.EqualTo(42));
        var eventCount = await _db.ConsentEvents.CountAsync();
        var first = await _withdrawalRequests.CreateAsync(_customer, default); await _db.SaveChangesAsync();
        var retry = await _withdrawalRequests.CreateAsync(_customer, default);
        Assert.That(retry, Is.EqualTo(first));
        Assert.That(first.Processed, Is.False);
        Assert.That(await _db.ConsentEvents.CountAsync(), Is.EqualTo(eventCount));
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses[0].Status, Is.EqualTo("current"));
        Assert.That(await _consents.WithPersonalDataAsync(_customer, () => Task.FromResult(1), default), Is.EqualTo(1));
        Reject(() => _withdrawalRequests.ProcessAsync(new() { CustomerId = int.MaxValue, RequestedAt = first.RequestedAt }, default),
            "consent_withdrawal_request_not_found");
        var processed = await _withdrawalRequests.ProcessAsync(new() { CustomerId = _customer, RequestedAt = first.RequestedAt }, default);
        await _db.SaveChangesAsync();
        Assert.That(processed.Processed, Is.True);
        Assert.That((await _withdrawalRequests.ProcessAsync(new() { CustomerId = _customer, RequestedAt = first.RequestedAt }, default)).Processed, Is.True);
        var second = await _withdrawalRequests.CreateAsync(_customer, default); await _db.SaveChangesAsync();
        Assert.That(second.RequestedAt, Is.GreaterThan(first.RequestedAt));
        Assert.That(second.Processed, Is.False);
        Assert.That((await _withdrawalRequests.ListAsync(default))
            .Where(item => item.CustomerId == _customer)
            .Select(item => item.Processed), Is.EqualTo(new[] { false, true }));
        Assert.That((await _consents.CustomerAsync(_customer, default)).WithdrawalRequest, Is.EqualTo(second));
        Reject(() => _withdrawalRequests.CreateAsync(int.MaxValue, default), "customer_not_found");
        Reject(() => _consents.CustomerAsync(int.MaxValue, default), "customer_not_found");
    }

    [Test]
    public void WithdrawalRequest_HasOnlyTheMvpAttributes()
    {
        Assert.That(typeof(CustomerConsentWithdrawalRequest).GetProperties().Select(property => property.Name),
            Is.EquivalentTo(new[] { "CustomerId", "RequestedAt", "Processed" }));
    }

    [Test]
    public async Task Onboarding_RequiresExactVersionsPhoneAndUnexpiredSingleUseReceipt()
    {
        var pd = await CreateCurrent(); var agreement = await CreateCurrent(LegalDocumentKind.UserAgreement);
        Reject(() => _consents.BeginOnboardingAsync("+78880000001", Guid.NewGuid(), Decision(pd), default), "consent_version_changed");
        Reject(() => _consents.BeginOnboardingAsync("+78880000001", agreement.Id, Decision(pd, "refuse"), default), "consent_required");
        var receipt = await _consents.BeginOnboardingAsync("+78880000001", agreement.Id, Decision(pd), default); await _db.SaveChangesAsync();
        var customer = await _db.Customers.SingleAsync(x => x.Id == _customer);
        Reject(() => _consents.CompleteOnboardingAsync(customer, null, default), "consent_required");
        Reject(() => _consents.CompleteOnboardingAsync(customer, "unknown", default), "onboarding_consent_expired");
        await _consents.CompleteOnboardingAsync(customer, receipt, default); await _db.SaveChangesAsync();
        Reject(() => _consents.CompleteOnboardingAsync(customer, receipt, default), "onboarding_consent_expired");
        var mine = await _consents.CustomerAsync(_customer, default);
        Assert.That(mine.History.Select(x => x.Kind), Is.EquivalentTo(new[] { LegalDocumentKind.PersonalDataConsent, LegalDocumentKind.UserAgreement }));
        var replacementDate = ConsentCalendar.LocalDate(_clock.Now).AddDays(1);
        var replacement = await CreateDocument(effectiveDate: replacementDate);
        _clock.Now = replacement.EffectiveAt.AddMinutes(-5);
        var stale = await _consents.BeginOnboardingAsync(customer.Phone, agreement.Id, Decision(pd), default); await _db.SaveChangesAsync();
        _clock.Now = ConsentCalendar.Midnight(replacementDate);
        Reject(() => _consents.CompleteOnboardingAsync(customer, stale, default), "consent_version_changed");
        _clock.Now = _clock.Now.AddMinutes(16);
        Reject(() => _consents.CompleteOnboardingAsync(customer, stale, default), "onboarding_consent_expired");
    }

    [Test]
    public async Task Retention_KeepsLegalDocumentsAndWithdrawalRequests()
    {
        var pd = await CreateCurrent();
        await _consents.DecidePersonalDataAsync(_customer, Decision(pd), default); await _db.SaveChangesAsync();
        var historical = await CreateDocument(effectiveDate: ConsentCalendar.LocalDate(_clock.Now).AddDays(1));
        await _consents.BeginOnboardingAsync("+78880000001", (await _documents.CurrentAsync(LegalDocumentKind.UserAgreement, default)).Document!.Id, Decision(pd), default); await _db.SaveChangesAsync();
        await _withdrawalRequests.CreateAsync(_customer, default); await _db.SaveChangesAsync();
        _clock.Now = _clock.Now.AddDays(1100);
        var result = await _retention.SweepAsync(default); await _db.SaveChangesAsync();
        Assert.That(result.Onboarding, Is.GreaterThanOrEqualTo(1));
        Assert.That((await _db.LegalDocuments.AsNoTracking().SingleAsync(x => x.Id == historical.Id)).Source, Is.Not.Empty);
        Assert.That(await _db.CustomerConsentWithdrawalRequests.CountAsync(x => x.CustomerId == _customer), Is.EqualTo(1));
    }

    [Test]
    public async Task ActivationDuringProtectedWrite_IsRejectedBeforeCommit()
    {
        var old = await CreateCurrent();
        await _consents.DecidePersonalDataAsync(_customer, Decision(old), default); await _db.SaveChangesAsync();
        var next = await CreateDocument(effectiveDate: ConsentCalendar.LocalDate(_clock.Now).AddDays(2));
        Reject(() => _consents.WithPersonalDataAsync(_customer, () => { _clock.Now = next.EffectiveAt; return Task.FromResult(true); }, default), "consent_version_changed");
    }

    [Test]
    public async Task Retention_DisposesExpiredDecisionsWithoutRevivingPermission()
    {
        var pd = await CreateCurrent();
        await _consents.DecidePersonalDataAsync(_customer, Decision(pd), default); await _db.SaveChangesAsync();
        await _consents.DecidePersonalDataAsync(_customer, Decision(pd, "refuse"), default); await _db.SaveChangesAsync();
        _clock.Now = _clock.Now.AddDays(1100);
        var older = await _db.ConsentEvents.FirstAsync(x => x.CustomerId == _customer && x.Decision == "grant");
        older.RetainUntil = _clock.Now.AddDays(1); await _db.SaveChangesAsync();
        await _retention.SweepAsync(default); await _db.SaveChangesAsync();
        Assert.That(await _db.ConsentEvents.CountAsync(x => x.CustomerId == _customer), Is.EqualTo(2));
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses.Single().Status, Is.EqualTo("refused"));
        _clock.Now = _clock.Now.AddDays(2);
        var next = await _retention.SweepAsync(default); await _db.SaveChangesAsync();
        Assert.That(next.Events, Is.EqualTo(2));
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses.Single().Status, Is.EqualTo("missing"));
        var customer = await _db.Customers.SingleAsync(x => x.Id == _customer); customer.State = CustomerState.Disabled; await _db.SaveChangesAsync();
        Reject(() => _consents.WithPersonalDataAsync(_customer, () => Task.FromResult(true), default), "customer_not_found");
    }

    [Test]
    public async Task Retention_ContinuesPastAFullPageOfExpiredEvidence()
    {
        var cookies = await CreateCurrent(LegalDocumentKind.CookieConsent);
        for (var i = 0; i < 1000; i++)
            _db.ConsentEvents.Add(new()
            {
                CustomerId = _customer,
                SubjectKey = $"customer:{_customer}",
                DocumentId = cookies.Id,
                ContentHash = cookies.ContentHash,
                Kind = LegalDocumentKind.CookieConsent,
                Decision = "refuse",
                Source = "test",
                IdempotencyKey = Guid.NewGuid(),
                At = _clock.Now.AddYears(-4),
                RetainUntil = _clock.Now.AddDays(-1)
            });
        await _db.SaveChangesAsync();
        var expired = new ConsentEvent
        {
            SubjectKey = "browser:expired-test",
            DocumentId = cookies.Id,
            ContentHash = cookies.ContentHash,
            Kind = LegalDocumentKind.CookieConsent,
            Decision = "refuse",
            Source = "test",
            IdempotencyKey = Guid.NewGuid(),
            At = _clock.Now.AddYears(-4),
            RetainUntil = _clock.Now.AddDays(-1)
        };
        _db.ConsentEvents.Add(expired); await _db.SaveChangesAsync();
        var result = await _retention.SweepAsync(default);
        Assert.That(result.Events, Is.EqualTo(1001));
        Assert.That(await _db.ConsentEvents.AnyAsync(x => x.Id == expired.Id), Is.False);
        Assert.That(await _db.ConsentEvents.CountAsync(x => x.CustomerId == _customer), Is.Zero);
    }

    [Test]
    public void ConsentOptions_RequireEvidenceRetentionToCoverCookieValidity()
    {
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(_options);
        Assert.That(_options.Validate(context), Is.Empty);
        _options.EvidenceDays = _options.CookieDays - 1;
        Assert.That(_options.Validate(context), Is.Not.Empty);
    }

    [TestCase("<script>alert(1)</script>", "legal_document_html_not_allowed")]
    [TestCase("<img src=x onerror=alert(1)>", "legal_document_html_not_allowed")]
    [TestCase("[test](javascript:alert%281%29)", "legal_document_link_not_allowed")]
    [TestCase("[test](data:text/html,hello)", "legal_document_link_not_allowed")]
    [TestCase("<ftp://example.test>", "legal_document_link_not_allowed")]
    [TestCase("![image](https://example.test/pixel.png)", "legal_document_image_not_allowed")]
    [TestCase("`code`", "legal_document_code_not_allowed")]
    [TestCase("    indented code", "legal_document_code_not_allowed")]
    [TestCase("> quote", "legal_document_quote_not_allowed")]
    [TestCase("---", "legal_document_separator_not_allowed")]
    [TestCase(" ", "legal_document_text_required")]
    public void Renderer_RejectsActiveAndUnsupportedContent(string input, string code)
        => Assert.That(() => ConsentDocumentRenderer.Render(Encoding.UTF8.GetBytes(input), "test.md"),
            Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo(code));

    [Test]
    public void Renderer_RejectsControlCharacters()
        => Assert.That(() => ConsentDocumentRenderer.Render(Encoding.UTF8.GetBytes(new string((char)1, 1)), "test.md"),
            Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo("legal_document_control_character"));

    [Test]
    public void Renderer_PreservesExactSourceAndSafeCanonicalFormatting()
    {
        var source = Encoding.UTF8.GetBytes("\uFEFF# Текст\r\n\r\n**жирный** *курсив* [ссылка](https://example.test)\n\n- один\n- два\n\n| А | Б |\n|---|---|\n| 1 | 2 |\n");
        var rendered = ConsentDocumentRenderer.Render(source, "TEST.MD");
        Assert.That(rendered.Html, Does.Contain("<table>").And.Contain("<strong>жирный</strong>"));
        Assert.That(rendered.SourceHash, Has.Length.EqualTo(64));
        Assert.That(rendered.ContentHash, Is.Not.EqualTo(rendered.SourceHash));
        Assert.That(ConsentDocumentRenderer.Render(source, "test.md"), Is.EqualTo(rendered));
        Assert.That(() => ConsentDocumentRenderer.Render([255], "test.md"), Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo("legal_document_encoding"));
        Assert.That(() => ConsentDocumentRenderer.Render([], "test.md"), Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo("legal_document_file_required"));
        Assert.That(() => ConsentDocumentRenderer.Render(null, "test.md"), Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo("legal_document_file_required"));
        Assert.That(() => ConsentDocumentRenderer.Render(source, "test.docx"), Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo("legal_document_file_type"));
        Assert.That(() => ConsentDocumentRenderer.Render(source, null), Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo("legal_document_file_type"));
        Assert.That(() => ConsentDocumentRenderer.Render(new byte[262145], "test.md"), Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo("legal_document_file_too_large"));
    }
    [Test]
    public void Calendar_UsesMoscowMidnight()
    {
        Assert.That(ConsentCalendar.Midnight(new(2026, 9, 7)), Is.EqualTo(DateTimeOffset.Parse("2026-09-06T21:00:00Z")));
        Assert.That(ConsentCalendar.LocalDate(DateTimeOffset.Parse("2026-09-06T22:00:00Z")), Is.EqualTo(new DateOnly(2026, 9, 7)));
    }
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
}
