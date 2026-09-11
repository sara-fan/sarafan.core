// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture, NonParallelizable]
public sealed class ConsentReviewTests
{
    private string _connectionString = "";
    private NpgsqlConnection _admin = null!;
    private ReviewClock _clock = null!;
    private IOptions<AuthenticationOptions> _auth = null!;
    private readonly Dictionary<LegalDocumentKind, LegalDocumentDto> _documents = [];
    private const string Phone = "+78880000002";
    private const string Browser = "separate-review-browser-receipt-at-least-32-characters";
    private int _customer;
    private int _actor;

    [SetUp]
    public async Task SetUp()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var parent = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _auth = scope.ServiceProvider.GetRequiredService<IOptions<AuthenticationOptions>>();
        var builder = new NpgsqlConnectionStringBuilder(parent.Database.GetConnectionString())
        { Database = $"sarafan_review_test_{Guid.NewGuid():N}", Pooling = false };
        _connectionString = builder.ConnectionString;
        _admin = new NpgsqlConnection(parent.Database.GetConnectionString());
        await _admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{builder.Database}\"", _admin);
        await create.ExecuteNonQueryAsync();
        _clock = new() { Now = DateTimeOffset.UtcNow };
        await using var database = Database();
        await database.Database.MigrateAsync();
        database.Customers.Add(new Customer { Phone = Phone, Profile = new() });
        var actor = new BackofficeUser
        {
            Email = "consent-review@sarafan.test",
            NormalizedEmail = "CONSENT-REVIEW@SARAFAN.TEST",
            FirstName = "Consent",
            LastName = "Reviewer",
            PasswordHash = "not-a-real-password",
            IsActive = true,
            CreatedAt = _clock.Now,
            UpdatedAt = _clock.Now
        };
        database.BackofficeUsers.Add(actor);
        await database.SaveChangesAsync();
        _customer = await database.Customers.Select(x => x.Id).SingleAsync();
        _actor = actor.Id;
        _documents.Clear();
        foreach (var kind in new[] { LegalDocumentKind.PersonalDataConsent, LegalDocumentKind.UserAgreement, LegalDocumentKind.CookieConsent })
            _documents[kind] = await CreateDocument(database, kind);
    }

    [TearDown]
    public async Task TearDown()
    {
        var name = new NpgsqlConnectionStringBuilder(_connectionString).Database;
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", _admin);
        await drop.ExecuteNonQueryAsync();
        await _admin.DisposeAsync();
    }

    private AppDbContext Database(IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString);
        if (interceptor is not null) builder.AddInterceptors(interceptor);
        return new(builder.Options);
    }
    private ConsentService Consents(AppDbContext database) => new(database, _clock, Options.Create(new ConsentOptions()), _auth, NullLogger<ConsentService>.Instance);
    private LegalDocumentService Documents(AppDbContext database) => new(database, _clock, NullLogger<LegalDocumentService>.Instance);
    private Task<LegalDocumentDto> CreateDocument(AppDbContext database, LegalDocumentKind kind, DateOnly? effectiveDate = null) => Documents(database).CreateAsync(
        new()
        {
            Kind = kind,
            Title = "Тест",
            DisplayVersion = Guid.NewGuid().ToString(),
            FileName = "test.md",
            Source = Encoding.UTF8.GetBytes("# Текст"),
            EffectiveDate = effectiveDate ?? ConsentCalendar.LocalDate(_clock.Now)
        }, _actor, default);
    private ConsentDecisionRequest Decision(LegalDocumentKind kind) => new()
    { DocumentId = _documents[kind].Id, ContentHash = _documents[kind].ContentHash, Decision = "grant", IdempotencyKey = Guid.NewGuid(), Categories = kind == LegalDocumentKind.CookieConsent ? [CookieCategory.Mandatory] : [] };
    private Task<string> Onboarding(AppDbContext database) => Consents(database).BeginOnboardingAsync(Phone, _documents[LegalDocumentKind.UserAgreement].Id, Decision(LegalDocumentKind.PersonalDataConsent), default);

    private static async Task<string[]> Race(int count, Func<AppDbContext, Task> action, Func<AppDbContext> database)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(async _ =>
        {
            await using var context = database();
            await ready.Task;
            try { await action(context); return "success"; }
            catch (ServiceException error) { return error.Code; }
        }).ToArray();
        ready.SetResult();
        return await Task.WhenAll(tasks);
    }

    [TestCase(LegalDocumentKind.PersonalDataConsent)]
    [TestCase(LegalDocumentKind.CookieConsent)]
    public async Task ConcurrentDuplicateDecisionsPersistExactlyOneEvent(LegalDocumentKind kind)
    {
        var request = Decision(kind);
        var results = await Race(4, db => kind == LegalDocumentKind.CookieConsent
            ? Consents(db).DecideCookiesAsync(Browser, request, default)
            : Consents(db).DecidePersonalDataAsync(_customer, request, default), () => Database());
        Assert.That(results, Is.All.EqualTo("success"));
        await using var check = Database();
        Assert.That(await check.ConsentEvents.CountAsync(), Is.EqualTo(1));
    }

    [TestCase(LegalDocumentKind.PersonalDataConsent)]
    [TestCase(LegalDocumentKind.CookieConsent)]
    public async Task ExactRetryAfterVersionChangeReturnsStatusWithoutNewEvidence(LegalDocumentKind kind)
    {
        await using var database = Database();
        var request = Decision(kind);
        var service = Consents(database);
        if (kind == LegalDocumentKind.CookieConsent) await service.DecideCookiesAsync(Browser, request, default);
        else await service.DecidePersonalDataAsync(_customer, request, default);
        _clock.Now = ConsentCalendar.Midnight(ConsentCalendar.LocalDate(_clock.Now).AddDays(1));
        await CreateDocument(database, kind);
        var status = kind == LegalDocumentKind.CookieConsent
            ? (await service.DecideCookiesAsync(Browser, request, default)).Status
            : (await service.DecidePersonalDataAsync(_customer, request, default)).Statuses.Single().Status;
        Assert.That(status, Is.EqualTo("renewal-required"));
        Assert.That(await database.ConsentEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentOnboardingCompletionConsumesReceiptOnce()
    {
        await using var setup = Database();
        var receipt = await Onboarding(setup);
        var results = await Race(4, async db => await Consents(db).CompleteOnboardingAsync(await db.Customers.SingleAsync(), receipt, default), () => Database());
        Assert.That(results.Count(x => x == "success"), Is.EqualTo(1));
        Assert.That(results.Count(x => x == "onboarding_consent_expired"), Is.EqualTo(3));
        await using var check = Database();
        Assert.That(await check.ConsentEvents.CountAsync(), Is.EqualTo(2));
        Assert.That((await check.ConsentOnboarding.SingleAsync()).UsedAt, Is.Not.Null);
    }

    [Test]
    public async Task ConcurrentCreationKeepsEffectiveDatesUnique()
    {
        var date = ConsentCalendar.LocalDate(_clock.Now).AddDays(2);
        var results = await Race(2, async db =>
        {
            await CreateDocument(db, LegalDocumentKind.PersonalDataConsent, date);
        }, () => Database());
        Assert.That(results, Is.EquivalentTo(new[] { "success", "legal_document_effective_date_conflict" }));
        await using var check = Database();
        Assert.That(await check.LegalDocuments.CountAsync(x => x.EffectiveAt > _clock.Now), Is.EqualTo(1));
    }

    [TestCase("personal-data-consent")]
    [TestCase("cookie-consent")]
    [TestCase("begin-onboarding")]
    [TestCase("complete-onboarding")]
    public async Task ActivationDuringSaveRollsBackStaleEvidence(string operation)
    {
        await using var setup = Database();
        var date = DateOnly.FromDateTime(_clock.Now.UtcDateTime.AddDays(2));
        _clock.Now = ConsentCalendar.Midnight(date).AddMinutes(-1);
        var receipt = operation == "complete-onboarding" ? await Onboarding(setup) : null;
        var kind = operation == "cookie-consent" ? LegalDocumentKind.CookieConsent : LegalDocumentKind.PersonalDataConsent;
        await CreateDocument(setup, kind, date);
        await using var action = Database(new AfterSave(() => _clock.Now = ConsentCalendar.Midnight(date)));
        var service = Consents(action);
        var error = Assert.ThrowsAsync<ServiceException>(async () =>
        {
            if (operation == "begin-onboarding") await Onboarding(action);
            else if (operation == "complete-onboarding") await service.CompleteOnboardingAsync(await action.Customers.SingleAsync(), receipt, default);
            else if (operation == "cookie-consent") await service.DecideCookiesAsync(Browser, Decision(kind), default);
            else await service.DecidePersonalDataAsync(_customer, Decision(kind), default);
        });
        Assert.That(error!.Code, Is.EqualTo("consent_version_changed"));
        await using var check = Database();
        Assert.That(await check.ConsentEvents.CountAsync(), Is.Zero);
        Assert.That(await check.ConsentOnboarding.CountAsync(), Is.EqualTo(receipt is null ? 0 : 1));
        Assert.That(await check.ConsentOnboarding.AnyAsync(x => x.UsedAt != null), Is.False);
    }

    [Test]
    public async Task AgreementOnlyReplacementReportsAgreementForReceiptAndCompletion()
    {
        await using var database = Database();
        var replacement = await CreateDocument(database, LegalDocumentKind.UserAgreement, ConsentCalendar.LocalDate(_clock.Now).AddDays(1));
        _clock.Now = replacement.EffectiveAt.AddMinutes(-5);
        var receipt = await Onboarding(database);
        _clock.Now = replacement.EffectiveAt;
        foreach (var action in new Func<Task>[] {
            () => Consents(database).ValidateOnboardingReceiptAsync(receipt, default),
            async () => await Consents(database).CompleteOnboardingAsync(await database.Customers.SingleAsync(), receipt, default) })
        {
            var error = Assert.ThrowsAsync<ServiceException>(async () => await action());
            Assert.That(error!.RequiredDocumentId, Is.EqualTo(replacement.Id));
            Assert.That(error.ConsentKind, Is.EqualTo(LegalDocumentKind.UserAgreement));
        }
        Assert.That(await database.ConsentEvents.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task AssociationRequiresAndPreservesAuthenticatedTokenProvenance()
    {
        await using var database = Database();
        var service = Consents(database);
        await service.DecideCookiesAsync(Browser, Decision(LegalDocumentKind.CookieConsent), default);
        Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.AssociateBrowserAsync(_customer, Browser, Guid.Empty, default))!.Code, Is.EqualTo("invalid_access_token"));
        var tokenId = Guid.NewGuid();
        await service.AssociateBrowserAsync(_customer, Browser, tokenId, default);
        await service.AssociateBrowserAsync(_customer, Browser, Guid.NewGuid(), default);
        Assert.That((await database.ConsentAssociations.SingleAsync()).AuthenticationTokenId, Is.EqualTo(tokenId));
    }

    [TestCase(179, false)]
    [TestCase(180, true)]
    [TestCase(181, true)]
    public void EvidenceRetentionCoversCookieValidity(int evidenceDays, bool valid)
    {
        var options = new ConsentOptions { CookieDays = 180, EvidenceDays = evidenceDays };
        Assert.That(Validator.TryValidateObject(options, new ValidationContext(options), [], true), Is.EqualTo(valid));
    }

    [Test]
    public async Task ConsentPersistenceFailureDoesNotBecomeAccountExistsAndRollsBackCustomer()
    {
        const string phone = "+78880000003";
        await using var setup = Database();
        var receipt = await Consents(setup).BeginOnboardingAsync(phone, _documents[LegalDocumentKind.UserAgreement].Id, Decision(LegalDocumentKind.PersonalDataConsent), default);
        await using var database = Database(new FailEvidenceSave());
        var service = new AuthenticationService(database, new PhoneNormalizer(), new PhoneSuffixVerificationCodeProvider(),
            new VerificationAttemptStore(_clock), new JwtTokenService(_auth, _clock, NullLogger<JwtTokenService>.Instance),
            _auth, _clock, Consents(database), NullLogger<AuthenticationService>.Instance);
        Assert.ThrowsAsync<DbUpdateException>(() => service.VerifyCodeAsync(new()
        { Phone = phone, Code = "0003", TermsAccepted = true, OnboardingToken = receipt }, "test", null, default));
        await using var check = Database();
        Assert.That(await check.Customers.AnyAsync(x => x.Phone == phone), Is.False);
        Assert.That(await check.ConsentEvents.CountAsync(), Is.Zero);
        Assert.That((await check.ConsentOnboarding.SingleAsync()).UsedAt, Is.Null);
    }

    [Test]
    public async Task ReceiptExpiringDuringSaveRollsBackConsumption()
    {
        await using var setup = Database();
        var receipt = await Onboarding(setup);
        await using var database = Database(new AfterSave(() => _clock.Now = _clock.Now.AddMinutes(16)));
        var error = Assert.ThrowsAsync<ServiceException>(async () => await Consents(database).CompleteOnboardingAsync(await database.Customers.SingleAsync(), receipt, default));
        Assert.That(error!.Code, Is.EqualTo("onboarding_consent_expired"));
        await using var check = Database();
        Assert.That(await check.ConsentEvents.CountAsync(), Is.Zero);
        Assert.That((await check.ConsentOnboarding.SingleAsync()).UsedAt, Is.Null);
    }

    [Test]
    public async Task CreationReturnsExplicitMoscowEffectiveDate()
    {
        await using var database = Database();
        var date = ConsentCalendar.LocalDate(_clock.Now).AddDays(2);
        var future = await CreateDocument(database, LegalDocumentKind.CookieConsent, date);
        Assert.That(future.EffectiveLocalDate, Is.EqualTo(date));
        Assert.That(future.EffectiveAt, Is.EqualTo(ConsentCalendar.Midnight(date)));
        Assert.That(future.EffectiveTimeZone, Is.EqualTo("Europe/Moscow"));
        Assert.That(future.CanDelete, Is.True);
        _clock.Now = ConsentCalendar.Midnight(date.AddDays(1)).AddMinutes(30);
        var immediate = await CreateDocument(database, LegalDocumentKind.PersonalDataConsent);
        Assert.That(immediate.EffectiveAt, Is.EqualTo(ConsentCalendar.Midnight(date.AddDays(1))));
        Assert.That(immediate.EffectiveLocalDate, Is.EqualTo(date.AddDays(1)));
        Assert.That(immediate.EffectiveLocalDate, Is.Not.EqualTo(DateOnly.FromDateTime(_clock.Now.UtcDateTime)));
        Assert.That(immediate.EffectiveTimeZone, Is.EqualTo("Europe/Moscow"));
        Assert.That(immediate.CanDelete, Is.False);
    }

    [Test]
    public async Task RateLimitedRegistrationDoesNotPersistAnotherOnboardingReceipt()
    {
        await using var database = Database();
        var service = new AuthenticationService(database, new PhoneNormalizer(), new PhoneSuffixVerificationCodeProvider(),
            new VerificationAttemptStore(_clock), new JwtTokenService(_auth, _clock, NullLogger<JwtTokenService>.Instance),
            _auth, _clock, Consents(database), NullLogger<AuthenticationService>.Instance);
        var request = new RequestCodeRequest
        {
            Phone = "+78880000004",
            TermsAccepted = true,
            TermsDocumentId = _documents[LegalDocumentKind.UserAgreement].Id,
            PersonalDataConsent = Decision(LegalDocumentKind.PersonalDataConsent)
        };
        for (var attempt = 0; attempt < 3; attempt++) await service.RequestCodeAsync(request, "test", default);
        var error = Assert.ThrowsAsync<ServiceException>(() => service.RequestCodeAsync(request, "test", default));
        Assert.That(error!.StatusCode, Is.EqualTo(429));
        Assert.That(await database.ConsentOnboarding.CountAsync(), Is.EqualTo(3));
    }

    [Test]
    public async Task RetentionDeletesFullEvidencePagesWithBoundedDatabaseRoundTrips()
    {
        await using var setup = Database();
        var document = _documents[LegalDocumentKind.CookieConsent];
        setup.ConsentEvents.AddRange(Enumerable.Range(0, 2001).Select(index => new ConsentEvent
        {
            SubjectKey = $"expired-browser-{index}",
            DocumentId = document.Id,
            ContentHash = document.ContentHash,
            Kind = LegalDocumentKind.CookieConsent,
            Decision = "grant",
            IdempotencyKey = Guid.NewGuid(),
            At = _clock.Now.AddDays(-1100),
            RetainUntil = _clock.Now.AddDays(-1)
        }));
        await setup.SaveChangesAsync();
        var commands = new CountCommands();
        await using var database = Database(commands);
        var service = new ConsentRetentionService(database, _clock, NullLogger<ConsentRetentionService>.Instance);
        var result = await service.SweepAsync(default);
        Assert.That(result.Events, Is.EqualTo(2001));
        Assert.That(commands.Count, Is.LessThanOrEqualTo(25), "Each page must use set-based queries, not per-event lookups.");
        Assert.That(await setup.ConsentEvents.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task RetentionNeverDisposesLegalDocumentsOrTheirAudit()
    {
        await using var setup = Database();
        var documentCount = await setup.LegalDocuments.CountAsync();
        var auditCount = await setup.LegalDocumentAuditEvents.CountAsync();
        _clock.Now = _clock.Now.AddDays(1100);
        await new ConsentRetentionService(setup, _clock, NullLogger<ConsentRetentionService>.Instance).SweepAsync(default);
        Assert.That(await setup.LegalDocuments.CountAsync(), Is.EqualTo(documentCount));
        Assert.That(await setup.LegalDocumentAuditEvents.CountAsync(), Is.EqualTo(auditCount));
        Assert.That(await setup.LegalDocuments.AllAsync(x => x.Source.Length > 0 && x.Html != ""), Is.True);
    }

    [Test]
    public async Task CombinedCustomerAndBrowserHistoryReturnsOnlyTheLatest200Records()
    {
        await using var database = Database();
        for (var index = 0; index < 201; index++)
        {
            ConsentEvent Evidence(LegalDocumentKind kind, int offset) => new()
            {
                CustomerId = kind == LegalDocumentKind.PersonalDataConsent ? _customer : null,
                SubjectKey = kind == LegalDocumentKind.PersonalDataConsent ? $"customer:{_customer}" : "history-browser",
                DocumentId = _documents[kind].Id,
                ContentHash = _documents[kind].ContentHash,
                Kind = kind,
                Decision = "grant",
                IdempotencyKey = Guid.NewGuid(),
                At = _clock.Now.AddSeconds(index * 2 + offset - 500),
                RetainUntil = _clock.Now.AddDays(1000)
            };
            database.ConsentEvents.Add(Evidence(LegalDocumentKind.PersonalDataConsent, 0));
            database.ConsentAssociations.Add(new ConsentAssociation
            { Event = Evidence(LegalDocumentKind.CookieConsent, 1), CustomerId = _customer, AssociatedAt = _clock.Now, AuthenticationTokenId = Guid.NewGuid() });
        }
        await database.SaveChangesAsync();
        var history = (await Consents(database).CustomerAsync(_customer, default)).History;
        Assert.That(history, Has.Length.EqualTo(200));
        Assert.That(history.Count(x => x.Scope == "customer"), Is.EqualTo(100));
        Assert.That(history.Count(x => x.Scope == "observed-browser"), Is.EqualTo(100));
        Assert.That(history.Select(x => x.At), Is.Ordered.Descending);
        Assert.That(history.Last().At, Is.EqualTo(_clock.Now.AddSeconds(202 - 500)).Within(TimeSpan.FromMicroseconds(1)));
    }

    [TestCase("refuse")]
    [TestCase("withdraw")]
    [TestCase("grant")]
    public async Task CookieRetentionCannotReviveAnOlderUnexpiredGrantAfterConfigurationChanges(string denial)
    {
        await using var database = Database();
        var settings = new ConsentOptions { CookieDays = 365, EvidenceDays = 1095 };
        Assert.That(Validator.TryValidateObject(settings, new ValidationContext(settings), [], true), Is.True);
        var longRetention = new ConsentService(database, _clock, Options.Create(settings), _auth, NullLogger<ConsentService>.Instance);
        await longRetention.DecideCookiesAsync(Browser, Decision(LegalDocumentKind.CookieConsent), default);
        _clock.Now = _clock.Now.AddDays(10);
        var shorterRetention = new ConsentService(database, _clock, Options.Create(new ConsentOptions { CookieDays = 180, EvidenceDays = 180 }), _auth, NullLogger<ConsentService>.Instance);
        var request = Decision(LegalDocumentKind.CookieConsent);
        request.Decision = denial;
        request.Categories = denial == "grant" ? [CookieCategory.Mandatory] : [];
        await shorterRetention.DecideCookiesAsync(Browser, request, default);
        _clock.Now = _clock.Now.AddDays(181);
        var retention = new ConsentRetentionService(database, _clock, NullLogger<ConsentRetentionService>.Instance);
        await retention.SweepAsync(default);
        Assert.That(await database.ConsentEvents.CountAsync(), Is.EqualTo(2));
        var status = await shorterRetention.CookieStatusAsync(Browser, default);
        Assert.That(status.Status, Is.Not.EqualTo("current"));
        Assert.That(status.Categories, Is.Empty);
        _clock.Now = _clock.Now.AddDays(1100);
        await retention.SweepAsync(default);
        Assert.That(await database.ConsentEvents.CountAsync(), Is.Zero);
        Assert.That((await shorterRetention.CookieStatusAsync(Browser, default)).Categories, Is.Empty);
    }

    [TestCase(LegalDocumentKind.CookieConsent)]
    [TestCase(LegalDocumentKind.PersonalDataConsent)]
    public async Task DisposedEvidenceCannotBeReplayedAndMarkersExpireWithTheirDocument(LegalDocumentKind kind)
    {
        await using var database = Database();
        var request = Decision(kind);
        var service = Consents(database);
        if (kind == LegalDocumentKind.CookieConsent)
        {
            await service.DecideCookiesAsync(Browser, request, default);
            var error = Assert.ThrowsAsync<ServiceException>(() => service.DecideCookiesAsync("different-browser-with-at-least-32-characters", request, default));
            Assert.That(error!.Code, Is.EqualTo("consent_conflict"));
        }
        else
        {
            await service.DecidePersonalDataAsync(_customer, request, default);
            var refusal = Decision(kind); refusal.Decision = "refuse";
            await service.DecidePersonalDataAsync(_customer, refusal, default);
        }
        _clock.Now = _clock.Now.AddDays(1100);
        var retention = new ConsentRetentionService(database, _clock, NullLogger<ConsentRetentionService>.Instance);
        await retention.SweepAsync(default);
        Assert.That(await database.ConsentEvents.CountAsync(), Is.Zero);
        Assert.That(await database.ConsentReplayTombstones.CountAsync(), Is.EqualTo(kind == LegalDocumentKind.CookieConsent ? 1 : 2));
        var replay = Assert.ThrowsAsync<ServiceException>(async () =>
        {
            if (kind == LegalDocumentKind.CookieConsent) await service.DecideCookiesAsync(Browser, request, default);
            else await service.DecidePersonalDataAsync(_customer, request, default);
        });
        Assert.That(replay!.Code, Is.EqualTo("consent_conflict"));
        if (kind == LegalDocumentKind.CookieConsent)
        {
            Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.DecideCookiesAsync("different-browser-with-at-least-32-characters", request, default))!.Code, Is.EqualTo("consent_conflict"));
            Assert.That((await service.CookieStatusAsync(Browser, default)).Categories, Is.Empty);
            await service.DecideCookiesAsync(Browser, Decision(kind), default);
        }
        else await service.DecidePersonalDataAsync(_customer, Decision(kind), default);
        Assert.That(await database.ConsentEvents.CountAsync(), Is.EqualTo(1));
        _clock.Now = ConsentCalendar.Midnight(ConsentCalendar.LocalDate(_clock.Now).AddDays(1));
        await CreateDocument(database, kind);
        await retention.SweepAsync(default);
        Assert.That(await database.ConsentReplayTombstones.CountAsync(), Is.Zero);
        var stale = Assert.ThrowsAsync<ServiceException>(async () =>
        {
            if (kind == LegalDocumentKind.CookieConsent) await service.DecideCookiesAsync(Browser, request, default);
            else await service.DecidePersonalDataAsync(_customer, request, default);
        });
        Assert.That(stale!.Code, Is.EqualTo("consent_version_changed"));
        Assert.That(await database.ConsentEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task WithdrawalRequestsAreIndependentRecordsAndProcessingIsExact()
    {
        await using var database = Database();
        var service = new ConsentWithdrawalRequestService(database, _clock, NullLogger<ConsentWithdrawalRequestService>.Instance);
        var item = await service.CreateAsync(_customer, default);
        Assert.That(item.Processed, Is.False);
        var retry = await service.CreateAsync(_customer, default);
        Assert.That(retry, Is.EqualTo(item));
        var error = Assert.ThrowsAsync<ServiceException>(() => service.ProcessAsync(new()
        { CustomerId = _customer, RequestedAt = item.RequestedAt.AddTicks(1) }, default));
        Assert.That(error!.Code, Is.EqualTo("consent_withdrawal_request_not_found"));
        var processed = await service.ProcessAsync(new()
        { CustomerId = _customer, RequestedAt = item.RequestedAt }, default);
        Assert.That(processed.Processed, Is.True);
        Assert.That(await database.CustomerConsentWithdrawalRequests.CountAsync(), Is.EqualTo(1));
    }

    [TestCase("request", 20, "invalid_phone")]
    [TestCase("verify", 30, "invalid_phone")]
    public async Task AuthenticationIpQuotaBoundsConsentLookupsAndMalformedPhoneAttempts(string operation, int limit, string expected)
    {
        var commands = new CountCommands();
        await using var database = Database(commands);
        var service = new AuthenticationService(database, new PhoneNormalizer(), new PhoneSuffixVerificationCodeProvider(),
            new VerificationAttemptStore(_clock), new JwtTokenService(_auth, _clock, NullLogger<JwtTokenService>.Instance),
            _auth, _clock, Consents(database), NullLogger<AuthenticationService>.Instance);
        async Task Attempt()
        {
            if (operation == "request") await service.RequestCodeAsync(new()
            {
                Phone = "malformed",
                TermsAccepted = true,
                TermsDocumentId = _documents[LegalDocumentKind.UserAgreement].Id,
                PersonalDataConsent = Decision(LegalDocumentKind.PersonalDataConsent)
            }, "throttle-test", default);
            else await service.VerifyCodeAsync(new()
            { Phone = "malformed", OnboardingToken = Guid.NewGuid().ToString("N"), Code = "0000" }, "throttle-test", null, default);
        }
        for (var attempt = 0; attempt < limit; attempt++) Assert.That(Assert.ThrowsAsync<ServiceException>(Attempt)!.Code, Is.EqualTo(expected));
        var before = commands.Count;
        Assert.That(Assert.ThrowsAsync<ServiceException>(Attempt)!.Code, Is.EqualTo("rate_limited"));
        Assert.That(commands.Count, Is.EqualTo(before), "An exhausted IP quota must reject before any consent database lookup.");
        Assert.That(await database.ConsentOnboarding.CountAsync(), Is.Zero);
    }

    private sealed class CountCommands : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
    }

    private sealed class FailEvidenceSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ConsentEvent>().Any(x => x.State == EntityState.Added)) throw new DbUpdateException("test evidence failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class AfterSave(Action action) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        { action(); return ValueTask.FromResult(result); }
    }
}
