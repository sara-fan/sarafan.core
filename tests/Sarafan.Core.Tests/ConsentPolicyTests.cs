// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application
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
    private ConsentRightsService _rights = null!;
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
        _rights = new(_db, _clock, Options.Create(_options), NullLogger<ConsentRightsService>.Instance);
        _retention = new(_db, _clock, Options.Create(_options), NullLogger<ConsentRetentionService>.Instance);
        _admin = await _db.BackofficeUsers.MinAsync(x => x.Id);
        var customer = new Customer { Phone = "+78880000001", Profile = new() };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        _customer = customer.Id;
    }
    [TearDown]
    public async Task TearDown() { await _transaction.RollbackAsync(); await _transaction.DisposeAsync(); await _scope.DisposeAsync(); }

    private async Task<LegalDocumentDto> Draft(string kind = ConsentKinds.PersonalData, string? version = null)
    {
        var result = await _documents.SaveAsync(null, new()
        {
            Kind = kind,
            Title = "Согласие",
            DisplayVersion = version ?? Guid.NewGuid().ToString(),
            FileName = "consent.md",
            Source = Encoding.UTF8.GetBytes("# Согласие\n\nТекст **согласия**."),
            CookieCategories = kind == ConsentKinds.Cookies ? ["analytics", "marketing"] : []
        }, _admin, default);
        await _db.SaveChangesAsync();
        return result;
    }
    private async Task<LegalDocumentDto> Publish(string kind = ConsentKinds.PersonalData)
    {
        _clock.Now = _clock.Now.AddSeconds(1);
        var draft = await Draft(kind);
        var result = await _documents.PublishAsync(draft.Id, new() { Revision = draft.Revision, Now = true }, _admin, default);
        await _db.SaveChangesAsync(); return result;
    }
    private static ConsentDecisionRequest Decision(LegalDocumentDto doc, string decision = "grant", params string[] categories)
        => new() { DocumentId = doc.Id, ContentHash = doc.ContentHash, Decision = decision, Categories = categories, IdempotencyKey = Guid.NewGuid() };
    private static void Reject(Func<Task> action, string code) => Assert.That(async () => await action(), Throws.TypeOf<ServiceException>().With.Property("Code").EqualTo(code));

    [Test]
    public async Task Documents_AreImmutable_ScheduledAtMoscowMidnight_AndRequireRenewal()
    {
        var first = await Publish();
        Assert.That(first.State, Is.EqualTo("effective"));
        await _consents.DecidePersonalDataAsync(_customer, Decision(first), default); await _db.SaveChangesAsync();
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses[0].Status, Is.EqualTo("current"));
        var draft = await Draft();
        Reject(() => _documents.ReadAsync(draft.Id, false, default), "legal_document_not_found");
        Reject(() => _documents.PublishAsync(draft.Id, new() { Revision = 99, Now = true }, _admin, default), "consent_conflict");
        Reject(() => _documents.PublishAsync(draft.Id, new() { Revision = draft.Revision }, _admin, default), "invalid_effective_date");
        Reject(() => _documents.PublishAsync(draft.Id, new() { Revision = draft.Revision, Now = true, EffectiveDate = new(2020, 1, 1) }, _admin, default), "invalid_effective_date");
        Reject(() => _documents.PublishAsync(draft.Id, new() { Revision = draft.Revision, EffectiveDate = new(2020, 1, 1) }, _admin, default), "invalid_effective_date");
        var date = DateOnly.FromDateTime(_clock.Now.UtcDateTime.AddDays(3));
        var scheduled = await _documents.PublishAsync(draft.Id, new() { Revision = draft.Revision, EffectiveDate = date }, _admin, default); await _db.SaveChangesAsync();
        Assert.That(scheduled.State, Is.EqualTo("scheduled"));
        Assert.That(scheduled.EffectiveAt, Is.EqualTo(ConsentCalendar.Midnight(date)));
        var extra = await Draft();
        Reject(() => _documents.PublishAsync(extra.Id, new() { Revision = extra.Revision, Now = true }, _admin, default), "consent_conflict");
        Assert.That((await _documents.CurrentAsync(ConsentKinds.PersonalData, default)).NextChangeAt, Is.EqualTo(scheduled.EffectiveAt));
        Assert.That((await _consents.CustomerAsync(_customer, default)).NextChangeAt, Is.EqualTo(scheduled.EffectiveAt));
        _clock.Now = scheduled.EffectiveAt!.Value.AddTicks(-1);
        Assert.That((await _documents.CurrentAsync(ConsentKinds.PersonalData, default)).Document!.Id, Is.EqualTo(first.Id));
        _clock.Now = scheduled.EffectiveAt.Value;
        Assert.That((await _documents.ReadAsync(first.Id, false, default)).State, Is.EqualTo("superseded"));
        Assert.That((await _documents.CurrentAsync(ConsentKinds.PersonalData, default)).Document!.Id, Is.EqualTo(draft.Id));
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses[0].Status, Is.EqualTo("renewal-required"));
        Reject(() => _consents.DecidePersonalDataAsync(_customer, Decision(first), default), "consent_version_changed");
        Reject(() => _documents.CancelAsync(draft.Id, scheduled.Revision, _admin, default), "consent_conflict");
        var renewed = await _consents.DecidePersonalDataAsync(_customer, Decision(scheduled), default); await _db.SaveChangesAsync();
        // Nested transaction is saved by the caller; reload after save.
        renewed = await _consents.CustomerAsync(_customer, default);
        Assert.That(renewed.History.Count(x => x.Kind == ConsentKinds.PersonalData), Is.EqualTo(2));
        Assert.That((await _documents.ListAsync(null, default)).Select(x => x.State), Does.Contain("superseded"));
        Assert.That(await _documents.AuditAsync(draft.Id, default), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task DraftValidation_Edit_Cancel_AndDisposedAccess()
    {
        Reject(() => _documents.CurrentAsync("unknown", default), "invalid_legal_document");
        Reject(() => _documents.ListAsync("unknown", default), "invalid_legal_document");
        Reject(() => _documents.ReadAsync(Guid.NewGuid(), true, default), "legal_document_not_found");
        Reject(() => _documents.CancelAsync(Guid.NewGuid(), 1, _admin, default), "legal_document_not_found");
        var draft = await Draft(version: "editable");
        Reject(() => Draft(version: "editable"), "consent_conflict");
        var request = new LegalDocumentRequest { Kind = ConsentKinds.PersonalData, Title = "Правка", DisplayVersion = "editable", FileName = "new.md", Source = Encoding.UTF8.GetBytes("Новая редакция"), Revision = draft.Revision };
        var edited = await _documents.SaveAsync(draft.Id, request, _admin, default); await _db.SaveChangesAsync();
        Assert.That(edited.Revision, Is.EqualTo(2));
        Assert.That(await _documents.DownloadAsync(draft.Id, true, default), Is.EqualTo(request.Source));
        Reject(() => _documents.SaveAsync(draft.Id, request, _admin, default), "consent_conflict");
        request.Revision = 2; request.CookieCategories = ["analytics"];
        Reject(() => _documents.SaveAsync(draft.Id, request, _admin, default), "invalid_legal_document");
        request.CookieCategories = []; request.Locale = "en";
        Reject(() => _documents.SaveAsync(draft.Id, request, _admin, default), "invalid_legal_document");
        request.Locale = "ru";
        var scheduled = await _documents.PublishAsync(draft.Id, new() { Revision = 2, EffectiveDate = DateOnly.FromDateTime(_clock.Now.UtcDateTime.AddDays(5)) }, _admin, default); await _db.SaveChangesAsync();
        var cancelled = await _documents.CancelAsync(draft.Id, scheduled.Revision, _admin, default); await _db.SaveChangesAsync();
        Assert.That(cancelled.State, Is.EqualTo("cancelled"));
        Reject(() => _documents.SaveAsync(draft.Id, request, _admin, default), "consent_conflict");
        var entity = await _db.LegalDocuments.SingleAsync(x => x.Id == draft.Id); entity.DisposedAt = _clock.Now; await _db.SaveChangesAsync();
        Reject(() => _documents.ReadAsync(draft.Id, true, default), "legal_document_disposed");
    }

    [Test]
    public async Task BrowserChoices_AreIdempotent_Expire_AndNeverBecomeAccountAuthorization()
    {
        var doc = await Publish(ConsentKinds.Cookies);
        Assert.That((await _consents.CookieStatusAsync(null, default)).Status, Is.EqualTo("missing"));
        var grant = Decision(doc, "grant", "analytics");
        await _consents.DecideCookiesAsync(Browser, grant, default); await _db.SaveChangesAsync();
        Assert.That((await _consents.CookieStatusAsync(Browser, default)).Categories, Is.EqualTo(new[] { "analytics" }));
        await _consents.DecideCookiesAsync(Browser, grant, default); await _db.SaveChangesAsync();
        Assert.That(await _db.ConsentEvents.CountAsync(x => x.IdempotencyKey == grant.IdempotencyKey), Is.EqualTo(1));
        grant.Categories = ["marketing"];
        Reject(() => _consents.DecideCookiesAsync(Browser, grant, default), "consent_conflict");
        Reject(() => _consents.DecideCookiesAsync("short", Decision(doc), default), "invalid_consent_decision");
        Reject(() => _consents.DecideCookiesAsync(Browser, Decision(doc, "grant", "ads"), default), "invalid_consent_decision");
        Reject(() => _consents.DecideCookiesAsync(Browser, Decision(doc, "refuse", "analytics"), default), "invalid_consent_decision");
        Reject(() => _consents.DecideCookiesAsync(Browser, Decision(doc, "grant", "analytics", "analytics"), default), "invalid_consent_decision");
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
        await Publish(ConsentKinds.Cookies);
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
        var doc = await Publish(ConsentKinds.Cookies);
        await _consents.DecideCookiesAsync(Browser, Decision(doc, "grant", "analytics"), default); await _db.SaveChangesAsync();
        await _consents.DecideCookiesAsync(Browser, Decision(doc, "withdraw"), default); await _db.SaveChangesAsync();
        Assert.That((await _consents.CookieStatusAsync(Browser, default)).Status, Is.EqualTo("withdrawn"));
        _clock.Now = _clock.Now.AddDays(180);
        Assert.That((await _consents.CookieStatusAsync(Browser, default)).Status, Is.EqualTo("renewal-required"));
        await _consents.DecideCookiesAsync(Browser, Decision(doc, "withdraw"), default); await _db.SaveChangesAsync();
        await Publish(ConsentKinds.Cookies);
        var status = await _consents.CookieStatusAsync(Browser, default);
        Assert.That(status.Status, Is.EqualTo("renewal-required"));
        Assert.That(status.Categories, Is.Empty);
    }

    [Test]
    public async Task MissingRefusedAndWithdrawn_RestrictWrites_AndKeepRightsAccessible()
    {
        var document = await Publish();
        var missing = await _consents.CustomerAsync(_customer, default);
        Assert.That(missing.Statuses[0].Status, Is.EqualTo("missing"));
        Assert.That(missing.History, Is.Empty);
        Reject(() => _consents.WithPersonalDataAsync(_customer, () => Task.FromResult(true), default), "personal_data_consent_required");
        await _consents.DecidePersonalDataAsync(_customer, Decision(document, "refuse"), default); await _db.SaveChangesAsync();
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses[0].Status, Is.EqualTo("refused"));
        Reject(() => _consents.DecidePersonalDataAsync(_customer, Decision(document, "withdraw"), default), "invalid_consent_decision");
        var grant = Decision(document);
        await _consents.DecidePersonalDataAsync(_customer, grant, default); await _db.SaveChangesAsync();
        await _consents.DecidePersonalDataAsync(_customer, grant, default);
        Assert.That(await _consents.WithPersonalDataAsync(_customer, () => Task.FromResult(42), default), Is.EqualTo(42));
        var request = new RightsRequest { IdempotencyKey = Guid.NewGuid() };
        var rights = await _rights.CreateAsync(_customer, request, default); await _db.SaveChangesAsync();
        Assert.That(rights.DueAt, Is.EqualTo(_clock.Now.AddDays(30)));
        Assert.That(rights.ResponsibleStaffId, Is.Not.Null);
        Assert.That((await _rights.CreateAsync(_customer, request, default)).Id, Is.EqualTo(rights.Id));
        Assert.That((await _consents.CustomerAsync(_customer, default)).Statuses[0].Status, Is.EqualTo("withdrawn"));
        Reject(() => _consents.WithPersonalDataAsync(_customer, () => Task.FromResult(1), default), "personal_data_consent_required");
        request.Kind = "stop-processing";
        Reject(() => _rights.CreateAsync(_customer, request, default), "consent_conflict");
        Reject(() => _rights.CreateAsync(_customer, new() { Kind = "other", IdempotencyKey = Guid.NewGuid() }, default), "invalid_rights_request");
        Reject(() => _rights.CreateAsync(int.MaxValue, new() { IdempotencyKey = Guid.NewGuid() }, default), "customer_not_found");
        Reject(() => _consents.CustomerAsync(int.MaxValue, default), "customer_not_found");
    }

    [Test]
    public async Task RightsDeadlines_ExtendOnce_RequireEvidence_AndAuditCompletion()
    {
        var request = await _rights.CreateAsync(_customer, new() { Kind = "stop-processing", IdempotencyKey = Guid.NewGuid() }, default); await _db.SaveChangesAsync();
        Assert.That(request.DueAt, Is.EqualTo(ConsentCalendar.WorkingDeadline(_clock.Now, 10, _options)));
        Reject(() => _rights.UpdateAsync(Guid.NewGuid(), new(), _admin, default), "rights_case_not_found");
        Reject(() => _rights.UpdateAsync(request.Id, new() { Revision = 99 }, _admin, default), "consent_conflict");
        Reject(() => _rights.UpdateAsync(request.Id, new() { Revision = 1, State = "completed" }, _admin, default), "invalid_rights_request");
        Reject(() => _rights.UpdateAsync(request.Id, new() { Revision = 1, ResponsibleStaffId = int.MaxValue }, _admin, default), "invalid_rights_request");
        Reject(() => _rights.UpdateAsync(request.Id, new() { Revision = 1, Extend = true }, _admin, default), "invalid_rights_request");
        var extended = await _rights.UpdateAsync(request.Id, new() { Revision = 1, State = "in-progress", ResponsibleStaffId = _admin, Extend = true, ExtensionReason = "Ожидается подтверждение обработчика" }, _admin, default); await _db.SaveChangesAsync();
        Assert.That(extended.DueAt, Is.EqualTo(ConsentCalendar.WorkingDeadline(request.DueAt, 5, _options)));
        Reject(() => _rights.UpdateAsync(request.Id, new() { Revision = 2, Extend = true, ExtensionReason = "Повторно" }, _admin, default), "invalid_rights_request");
        var complete = await _rights.UpdateAsync(request.Id, new() { Revision = 2, State = "completed", ResponsibleStaffId = _admin, RetentionBasis = "Обязательный учёт, срок указан", CompletionEvidence = "Данные обработчиков удалены, акт № 1" }, _admin, default); await _db.SaveChangesAsync();
        Assert.That(complete.CompletedAt, Is.EqualTo(_clock.Now));
        Reject(() => _rights.UpdateAsync(request.Id, new() { Revision = 3 }, _admin, default), "consent_conflict");
        Assert.That(await _rights.ListAsync(_customer, default), Has.Length.EqualTo(1));
        Assert.That((await _consents.CustomerAsync(_customer, default)).RightsCases.Single().CompletionEvidence, Does.Contain("акт"));
    }

    [Test]
    public async Task Onboarding_RequiresExactVersionsPhoneAndUnexpiredSingleUseReceipt()
    {
        var pd = await Publish(); var agreement = await Publish(ConsentKinds.Agreement);
        Reject(() => _consents.BeginOnboardingAsync("+78880000001", Guid.NewGuid(), Decision(pd), default), "consent_version_changed");
        Reject(() => _consents.BeginOnboardingAsync("+78880000001", agreement.Id, Decision(pd, "refuse"), default), "consent_required");
        var receipt = await _consents.BeginOnboardingAsync("+78880000001", agreement.Id, Decision(pd), default); await _db.SaveChangesAsync();
        var customer = await _db.Customers.SingleAsync(x => x.Id == _customer);
        Reject(() => _consents.CompleteOnboardingAsync(customer, null, default), "consent_required");
        Reject(() => _consents.CompleteOnboardingAsync(customer, "unknown", default), "onboarding_consent_expired");
        await _consents.CompleteOnboardingAsync(customer, receipt, default); await _db.SaveChangesAsync();
        Reject(() => _consents.CompleteOnboardingAsync(customer, receipt, default), "onboarding_consent_expired");
        var mine = await _consents.CustomerAsync(_customer, default);
        Assert.That(mine.History.Select(x => x.Kind), Is.EquivalentTo(new[] { ConsentKinds.PersonalData, ConsentKinds.Agreement }));
        var stale = await _consents.BeginOnboardingAsync(customer.Phone, agreement.Id, Decision(pd), default); await _db.SaveChangesAsync();
        await Publish();
        Reject(() => _consents.CompleteOnboardingAsync(customer, stale, default), "consent_version_changed");
        _clock.Now = _clock.Now.AddMinutes(16);
        Reject(() => _consents.CompleteOnboardingAsync(customer, stale, default), "onboarding_consent_expired");
    }

    [Test]
    public async Task Retention_HoldsOperationalEvidence_AndDisposesUnreferencedArtifacts()
    {
        var pd = await Publish();
        await _consents.DecidePersonalDataAsync(_customer, Decision(pd), default); await _db.SaveChangesAsync();
        var orphan = await Draft();
        await _consents.BeginOnboardingAsync("+78880000001", (await _documents.CurrentAsync(ConsentKinds.Agreement, default)).Document!.Id, Decision(pd), default); await _db.SaveChangesAsync();
        _clock.Now = _clock.Now.AddDays(1100);
        var result = await _retention.SweepAsync(default); await _db.SaveChangesAsync();
        Assert.That(result.Onboarding, Is.GreaterThanOrEqualTo(1));
        Assert.That(result.Artifacts, Is.GreaterThanOrEqualTo(1));
        Assert.That((await _db.LegalDocuments.AsNoTracking().SingleAsync(x => x.Id == orphan.Id)).Source, Is.Empty);
        Assert.That(await _db.ConsentEvents.AnyAsync(x => x.CustomerId == _customer && x.Decision == "grant"), Is.True);
        await _rights.CreateAsync(_customer, new() { IdempotencyKey = Guid.NewGuid() }, default); await _db.SaveChangesAsync();
        _clock.Now = _clock.Now.AddDays(1100);
        await _retention.SweepAsync(default); await _db.SaveChangesAsync();
        Assert.That(await _db.ConsentEvents.CountAsync(x => x.CustomerId == _customer), Is.EqualTo(2));
    }

    [Test]
    public async Task ActivationDuringProtectedWrite_IsRejectedBeforeCommit()
    {
        var old = await Publish();
        await _consents.DecidePersonalDataAsync(_customer, Decision(old), default); await _db.SaveChangesAsync();
        var next = await Draft();
        var scheduled = await _documents.PublishAsync(next.Id, new() { Revision = next.Revision, EffectiveDate = DateOnly.FromDateTime(_clock.Now.UtcDateTime.AddDays(2)) }, _admin, default);
        await _db.SaveChangesAsync();
        Reject(() => _consents.WithPersonalDataAsync(_customer, () => { _clock.Now = scheduled.EffectiveAt!.Value; return Task.FromResult(true); }, default), "consent_version_changed");
    }

    [Test]
    public async Task Retention_DisposesExpiredDecisionsWithoutRevivingPermission()
    {
        var pd = await Publish();
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
    public async Task Retention_ContinuesPastAFullPageOfHeldEvidence()
    {
        var cookies = await Publish(ConsentKinds.Cookies);
        await _rights.CreateAsync(_customer, new() { IdempotencyKey = Guid.NewGuid() }, default);
        for (var i = 0; i < 1000; i++)
            _db.ConsentEvents.Add(new()
            {
                CustomerId = _customer,
                SubjectKey = $"customer:{_customer}",
                DocumentId = cookies.Id,
                ContentHash = cookies.ContentHash,
                Kind = ConsentKinds.Cookies,
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
            Kind = ConsentKinds.Cookies,
            Decision = "refuse",
            Source = "test",
            IdempotencyKey = Guid.NewGuid(),
            At = _clock.Now.AddYears(-4),
            RetainUntil = _clock.Now.AddDays(-1)
        };
        _db.ConsentEvents.Add(expired); await _db.SaveChangesAsync();
        var result = await _retention.SweepAsync(default);
        Assert.That(result.Events, Is.EqualTo(1));
        Assert.That(await _db.ConsentEvents.AnyAsync(x => x.Id == expired.Id), Is.False);
        Assert.That(await _db.ConsentEvents.CountAsync(x => x.CustomerId == _customer), Is.EqualTo(1000));
    }

    [Test]
    public void CalendarConfiguration_RejectsMalformedAndConflictingOverrides()
    {
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(_options);
        Assert.That(_options.Validate(context), Is.Empty);
        _options.WorkingDates = ["2026-01-09"];
        Assert.That(_options.Validate(context), Is.Not.Empty);
        _options.WorkingDates = ["invalid"];
        Assert.That(_options.Validate(context), Is.Not.Empty);
        _options.NonWorkingDates = null!;
        Assert.That(_options.Validate(context), Is.Not.Empty);
    }

    [TestCase("<script>alert(1)</script>")]
    [TestCase("<img src=x onerror=alert(1)>")]
    [TestCase("[test](javascript:alert%281%29)")]
    [TestCase("[test](data:text/html,hello)")]
    [TestCase("![image](https://example.test/pixel.png)")]
    [TestCase("`code`")]
    [TestCase("    indented code")]
    [TestCase("> quote")]
    [TestCase("---")]
    [TestCase("\u0001")]
    [TestCase(" ")]
    public void Renderer_RejectsActiveAndUnsupportedContent(string input)
        => Assert.That(() => ConsentDocumentRenderer.Render(Encoding.UTF8.GetBytes(input), "test.md"), Throws.TypeOf<ServiceException>());

    [Test]
    public void Renderer_PreservesExactSourceAndSafeCanonicalFormatting()
    {
        var source = Encoding.UTF8.GetBytes("\uFEFF# Текст\r\n\r\n**жирный** *курсив* [ссылка](https://example.test)\n\n- один\n- два\n\n| А | Б |\n|---|---|\n| 1 | 2 |\n");
        var rendered = ConsentDocumentRenderer.Render(source, "TEST.MD");
        Assert.That(rendered.Html, Does.Contain("<table>").And.Contain("<strong>жирный</strong>"));
        Assert.That(rendered.SourceHash, Has.Length.EqualTo(64));
        Assert.That(rendered.ContentHash, Is.Not.EqualTo(rendered.SourceHash));
        Assert.That(ConsentDocumentRenderer.Render(source, "test.md"), Is.EqualTo(rendered));
        Assert.That(() => ConsentDocumentRenderer.Render([255], "test.md"), Throws.TypeOf<ServiceException>());
        Assert.That(() => ConsentDocumentRenderer.Render([], "test.md"), Throws.TypeOf<ServiceException>());
        Assert.That(() => ConsentDocumentRenderer.Render(source, "test.docx"), Throws.TypeOf<ServiceException>());
        Assert.That(() => ConsentDocumentRenderer.Render(new byte[262145], "test.md"), Throws.TypeOf<ServiceException>());
    }
    [Test]
    public void Calendar_UsesMoscow_WeekendsHolidaysAndExplicitTransferOverrides()
    {
        Assert.That(ConsentCalendar.Midnight(new(2026, 9, 7)), Is.EqualTo(DateTimeOffset.Parse("2026-09-06T21:00:00Z")));
        var received = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var options = new ConsentOptions { NonWorkingDates = ["2026-01-09"], WorkingDates = ["2026-01-10"] };
        Assert.That(ConsentCalendar.WorkingDeadline(received, 1, options), Is.EqualTo(ConsentCalendar.Midnight(new(2026, 1, 11)).AddTicks(-1)));
    }
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
}
