// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
public sealed class ConsentReviewTests
{
    private readonly InMemoryDatabaseRoot _databaseRoot = new();
    private string _databaseName = "";
    private ReviewClock _clock = null!;
    private IOptions<AuthenticationOptions> _auth = null!;
    private readonly Dictionary<LegalDocumentKind, LegalDocumentDto> _documents = [];
    private const string Phone = "+78880000002";
    private int _customer;
    private int _actor;

    [SetUp]
    public async Task SetUp()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        _auth = scope.ServiceProvider.GetRequiredService<IOptions<AuthenticationOptions>>();
        _databaseName = $"sarafan_review_test_{Guid.NewGuid():N}";
        _clock = new() { Now = DateTimeOffset.UtcNow };
        await using var database = Database();
        await database.Database.EnsureCreatedAsync();
        database.Customers.Add(new Customer { Phone = Phone });
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
        foreach (var kind in new[] { LegalDocumentKind.PersonalDataConsent, LegalDocumentKind.UserAgreement })
            _documents[kind] = await CreateDocument(database, kind);
    }

    [TearDown]
    public async Task TearDown()
    {
        await using var database = Database();
        await database.Database.EnsureDeletedAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AgreementActivationAfterPersistenceIsRejected(bool accepting)
    {
        await using var setup = Database();
        var service = Consents(setup);
        if (!accepting)
        {
            await service.DecidePersonalDataAsync(_customer, Decision(LegalDocumentKind.PersonalDataConsent), default);
            await service.AcceptAgreementAsync(_customer, Decision(LegalDocumentKind.UserAgreement), default);
        }
        var next = await CreateDocument(setup, LegalDocumentKind.UserAgreement, ConsentCalendar.LocalDate(_clock.Now).AddDays(1));
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_databaseName, _databaseRoot)
            .AddInterceptors(new AdvanceClockAfterSave(() => _clock.Now = next.EffectiveAt))
            .Options);
        var error = await Assert.ThrowsAsync<ServiceException>(async () =>
        {
            if (accepting) await Consents(database).AcceptAgreementAsync(_customer, Decision(LegalDocumentKind.UserAgreement), default);
            else await Consents(database).WithOrderConsentsAsync(_customer, async () =>
            {
                var customer = await database.Customers.SingleAsync(x => x.Id == _customer);
                customer.FirstName = "Test";
                return true;
            }, default);
        });
        Assert.That(error!.Code, Is.EqualTo("consent_version_changed"));
        Assert.That(error.ConsentKind, Is.EqualTo(LegalDocumentKind.UserAgreement));
    }

    [Test]
    public async Task PersonalDataActivationDuringAgreementLoginIsRejected()
    {
        _clock.Now = ConsentCalendar.Midnight(ConsentCalendar.LocalDate(_clock.Now).AddDays(1)).AddMinutes(-1);
        await using var setup = Database();
        await Consents(setup).DecidePersonalDataAsync(_customer, Decision(LegalDocumentKind.PersonalDataConsent), default);
        var next = await CreateDocument(setup, LegalDocumentKind.PersonalDataConsent, ConsentCalendar.LocalDate(_clock.Now).AddDays(1));
        var receipt = await Authentication(setup).RequestCodeAsync(new RequestCodeRequest
        {
            Phone = Phone,
            TermsAccepted = true,
            TermsDocumentId = _documents[LegalDocumentKind.UserAgreement].Id
        }, "consent-boundary-test", default);
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_databaseName, _databaseRoot)
            .AddInterceptors(new AdvanceClockAfterSave(() => _clock.Now = next.EffectiveAt))
            .Options);
        var error = await Assert.ThrowsAsync<ServiceException>(() => Authentication(database).VerifyCodeAsync(new VerifyCodeRequest
        {
            Phone = Phone,
            Code = Phone[^4..],
            OnboardingToken = receipt
        }, "consent-boundary-test", null, default));
        Assert.That(error!.Code, Is.EqualTo("authentication_requirements_changed"));
        Assert.That(error.RequiredDocumentKinds, Is.EqualTo(new[] { LegalDocumentKind.PersonalDataConsent }));
    }

    private sealed class AdvanceClockAfterSave(Action advance) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            advance();
            return ValueTask.FromResult(result);
        }
    }

    private AppDbContext Database()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_databaseName, _databaseRoot)
            .Options);
    private ConsentService Consents(AppDbContext database) => new(database, _clock, Options.Create(new ConsentOptions()), _auth, NullLogger<ConsentService>.Instance);
    private AuthenticationService Authentication(AppDbContext database) => new(
        database,
        new PhoneNormalizer(),
        new PhoneSuffixVerificationCodeProvider(),
        new VerificationAttemptStore(_clock),
        new JwtTokenService(_auth, _clock, NullLogger<JwtTokenService>.Instance),
        _auth,
        _clock,
        Consents(database),
        NullLogger<AuthenticationService>.Instance);
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
    { DocumentId = _documents[kind].Id, ContentHash = _documents[kind].ContentHash, Decision = "grant", IdempotencyKey = Guid.NewGuid() };
    private Task<string> Onboarding(AppDbContext database) => Consents(database).BeginOnboardingAsync(Phone, _documents[LegalDocumentKind.UserAgreement].Id, Decision(LegalDocumentKind.PersonalDataConsent), default);


    [TestCase(LegalDocumentKind.PersonalDataConsent)]
    public async Task ExactRetryAfterVersionChangeReturnsStatusWithoutNewEvidence(LegalDocumentKind kind)
    {
        await using var database = Database();
        var request = Decision(kind);
        var service = Consents(database);
        await service.DecidePersonalDataAsync(_customer, request, default);
        _clock.Now = ConsentCalendar.Midnight(ConsentCalendar.LocalDate(_clock.Now).AddDays(1));
        await CreateDocument(database, kind);
        var status = (await service.DecidePersonalDataAsync(_customer, request, default)).Statuses.Single(x => x.Kind == LegalDocumentKind.PersonalDataConsent).Status;
        Assert.That(status, Is.EqualTo("renewal-required"));
        Assert.That(await database.ConsentEvents.CountAsync(), Is.EqualTo(1));
    }


    [Test, Combinatorial]
    public async Task VerificationRejectsConsentPayloadWithoutChangingAuthenticationState(
        [Values("termsAccepted", "termsDocumentId", "personalDataConsent")] string field,
        [Values] bool withReceipt,
        [Values] bool validCode)
    {
        string? receipt = null;
        await using (var setup = Database())
        {
            if (withReceipt)
            {
                receipt = await Authentication(setup).RequestCodeAsync(new RequestCodeRequest
                {
                    Phone = Phone,
                    TermsAccepted = true,
                    TermsDocumentId = _documents[LegalDocumentKind.UserAgreement].Id,
                    PersonalDataConsent = Decision(LegalDocumentKind.PersonalDataConsent)
                }, "verify-consent-test", default);
                Assert.That(receipt, Is.Not.Empty);
            }
            else
            {
                var onboarding = await Onboarding(setup);
                await Consents(setup).CompleteOnboardingAsync(await setup.Customers.SingleAsync(), onboarding, default);
            }
        }

        var request = new VerifyCodeRequest
        {
            Phone = Phone,
            Code = validCode ? "0002" : "0000",
            OnboardingToken = receipt
        };
        request.AdditionalFields = new Dictionary<string, JsonElement>
        {
            [field] = field switch
            {
                "termsAccepted" => JsonSerializer.SerializeToElement(true),
                "termsDocumentId" => JsonSerializer.SerializeToElement(_documents[LegalDocumentKind.UserAgreement].Id),
                _ => JsonSerializer.SerializeToElement(Decision(LegalDocumentKind.PersonalDataConsent))
            }
        };

        await using var database = Database();
        var service = Authentication(database);
        var error = await Assert.ThrowsAsync<ServiceException>(() => service.VerifyCodeAsync(
            request, "verify-consent-test", null, default));

        await using (var check = Database())
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(error!.StatusCode, Is.EqualTo(validCode ? 400 : 401));
                Assert.That(error.Code, Is.EqualTo(validCode ? "invalid_auth_request" : "invalid_code"));
                Assert.That(error.NextStep, Is.Null);
                Assert.That(error.RequiredDocumentKinds, Is.Null);
                Assert.That(await check.RefreshSessions.CountAsync(), Is.Zero);
                Assert.That(await check.ConsentEvents.CountAsync(), Is.EqualTo(withReceipt ? 0 : 2));
                Assert.That((await check.ConsentOnboarding.SingleAsync()).UsedAt.HasValue, Is.EqualTo(!withReceipt));
            }
        }

        var session = await service.VerifyCodeAsync(new VerifyCodeRequest
        {
            Phone = Phone,
            Code = "0002",
            OnboardingToken = receipt
        }, "verify-consent-test", null, default);
        Assert.That(session.Response.Customer.Id, Is.EqualTo(_customer));
        await using var completed = Database();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await completed.RefreshSessions.CountAsync(), Is.EqualTo(1));
            Assert.That(await completed.ConsentEvents.CountAsync(), Is.EqualTo(2));
            Assert.That((await completed.ConsentOnboarding.SingleAsync()).UsedAt, Is.Not.Null);
        }
    }


    [Test]
    public async Task ReactivationReusesExactPersonalDataIdempotencyRetry()
    {
        string receipt;
        Guid existingKey;
        await using (var setup = Database())
        {
            var onboarding = await Onboarding(setup);
            var customer = await setup.Customers.SingleAsync();
            await Consents(setup).CompleteOnboardingAsync(customer, onboarding, default);
            var existing = await setup.ConsentEvents.SingleAsync(item =>
                item.Kind == LegalDocumentKind.PersonalDataConsent);
            existingKey = existing.IdempotencyKey;
            customer.State = CustomerState.Disabled;
            await setup.SaveChangesAsync();

            receipt = (await Authentication(setup).RequestCodeAsync(new RequestCodeRequest
            {
                Phone = Phone,
                PersonalDataConsent = new ConsentDecisionRequest
                {
                    DocumentId = existing.DocumentId,
                    ContentHash = existing.ContentHash,
                    Decision = "grant",
                    IdempotencyKey = existingKey
                }
            }, "idempotent-reactivation-test", default))!;
        }

        await using var authenticationDatabase = Database();
        var session = await Authentication(authenticationDatabase).VerifyCodeAsync(new VerifyCodeRequest
        {
            Phone = Phone,
            Code = Phone[^4..],
            OnboardingToken = receipt
        }, "idempotent-reactivation-test", null, default);

        Assert.That(session.RefreshToken, Is.Not.Empty);
        await using var check = Database();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await check.ConsentEvents.CountAsync(item =>
                item.Kind == LegalDocumentKind.PersonalDataConsent), Is.EqualTo(1));
            Assert.That((await check.Customers.SingleAsync()).State, Is.Not.EqualTo(CustomerState.Disabled));
            Assert.That(await check.ConsentOnboarding
                .Where(item => item.PersonalDataIdempotencyKey == existingKey)
                .AllAsync(item => item.UsedAt != null), Is.True);
        }
    }

    [Test]
    public async Task ReactivationRejectsConflictingPersonalDataIdempotencyRetry()
    {
        string receipt;
        Guid conflictingKey;
        await using (var setup = Database())
        {
            var onboarding = await Onboarding(setup);
            var customer = await setup.Customers.SingleAsync();
            await Consents(setup).CompleteOnboardingAsync(customer, onboarding, default);

            var refusal = Decision(LegalDocumentKind.PersonalDataConsent);
            refusal.Decision = "refuse";
            conflictingKey = refusal.IdempotencyKey;
            await Consents(setup).DecidePersonalDataAsync(customer.Id, refusal, default);
            customer.State = CustomerState.Disabled;
            await setup.SaveChangesAsync();

            receipt = (await Authentication(setup).RequestCodeAsync(new RequestCodeRequest
            {
                Phone = Phone,
                PersonalDataConsent = new ConsentDecisionRequest
                {
                    DocumentId = refusal.DocumentId,
                    ContentHash = refusal.ContentHash,
                    Decision = "grant",
                    IdempotencyKey = conflictingKey
                }
            }, "conflicting-reactivation-test", default))!;
        }

        await using var authenticationDatabase = Database();
        var error = await Assert.ThrowsAsync<ServiceException>(() => Authentication(authenticationDatabase).VerifyCodeAsync(
            new VerifyCodeRequest
            {
                Phone = Phone,
                Code = Phone[^4..],
                OnboardingToken = receipt
            }, "conflicting-reactivation-test", null, default));
        Assert.That(error!.Code, Is.EqualTo("consent_conflict"));

        await using var check = Database();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await check.Customers.SingleAsync()).State, Is.EqualTo(CustomerState.Disabled));
            Assert.That(await check.RefreshSessions.CountAsync(), Is.Zero);
            Assert.That((await check.ConsentOnboarding.SingleAsync(item =>
                item.PersonalDataIdempotencyKey == conflictingKey)).UsedAt, Is.Null);
        }
    }

    [Test]
    public async Task AuthenticationRequestReportsCurrentAgreementForStaleDocumentId()
    {
        await using var database = Database();
        var error = await Assert.ThrowsAsync<ServiceException>(() => Authentication(database).RequestCodeAsync(
            new RequestCodeRequest
            {
                Phone = Phone,
                TermsAccepted = true,
                TermsDocumentId = Guid.NewGuid()
            }, "agreement-test", default));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error!.Code, Is.EqualTo("consent_version_changed"));
            Assert.That(error.RequiredDocumentId, Is.EqualTo(_documents[LegalDocumentKind.UserAgreement].Id));
            Assert.That(error.ConsentKind, Is.EqualTo(LegalDocumentKind.UserAgreement));
        }
        Assert.That(await database.ConsentOnboarding.CountAsync(), Is.Zero);
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
            var error = await Assert.ThrowsAsync<ServiceException>(async () => await action());
            Assert.That(error!.RequiredDocumentId, Is.EqualTo(replacement.Id));
            Assert.That(error.ConsentKind, Is.EqualTo(LegalDocumentKind.UserAgreement));
        }
        Assert.That(await database.ConsentEvents.CountAsync(), Is.Zero);
    }




    [Test]
    public async Task CreationReturnsExplicitMoscowEffectiveDate()
    {
        await using var database = Database();
        var date = ConsentCalendar.LocalDate(_clock.Now).AddDays(2);
        var future = await CreateDocument(database, LegalDocumentKind.PersonalDataConsent, date);
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
        var error = await Assert.ThrowsAsync<ServiceException>(() => service.RequestCodeAsync(request, "test", default));
        Assert.That(error!.StatusCode, Is.EqualTo(429));
        Assert.That(await database.ConsentOnboarding.CountAsync(), Is.EqualTo(3));
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
    public async Task CustomerHistoryReturnsOnlyTheLatest200Records()
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
        }
        await database.SaveChangesAsync();
        var history = (await Consents(database).CustomerAsync(_customer, default)).History;
        Assert.That(history, Has.Length.EqualTo(200));
        Assert.That(history.Select(x => x.At), Is.Ordered.Descending);
        Assert.That(history.Last().At, Is.EqualTo(_clock.Now.AddSeconds(2 - 500)).Within(TimeSpan.FromMicroseconds(1)));
    }


    [Test]
    public async Task RetainedEvidenceRetriesNeverOverrideLaterDecisions()
    {
        await using var database = Database();
        var request = Decision(LegalDocumentKind.PersonalDataConsent);
        var service = Consents(database);
        await service.DecidePersonalDataAsync(_customer, request, default);
        var refusal = Decision(LegalDocumentKind.PersonalDataConsent); refusal.Decision = "refuse";
        await service.DecidePersonalDataAsync(_customer, refusal, default);
        _clock.Now = _clock.Now.AddDays(1100);
        var retention = new ConsentRetentionService(database, _clock, NullLogger<ConsentRetentionService>.Instance);
        await retention.SweepAsync(default);
        var retry = await service.DecidePersonalDataAsync(_customer, request, default);
        Assert.That(retry.Statuses.Single(x => x.Kind == LegalDocumentKind.PersonalDataConsent).Status, Is.EqualTo("refused"));
        Assert.That(await database.ConsentEvents.CountAsync(), Is.EqualTo(2));
        Assert.That(await database.ConsentReplayTombstones.CountAsync(), Is.Zero);
        _clock.Now = ConsentCalendar.Midnight(ConsentCalendar.LocalDate(_clock.Now).AddDays(1));
        await CreateDocument(database, LegalDocumentKind.PersonalDataConsent);
        retry = await service.DecidePersonalDataAsync(_customer, request, default);
        Assert.That(retry.Statuses.Single(x => x.Kind == LegalDocumentKind.PersonalDataConsent).Status, Is.EqualTo("renewal-required"));
        Assert.That(await database.ConsentEvents.CountAsync(), Is.EqualTo(2));
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
        var error = await Assert.ThrowsAsync<ServiceException>(() => service.ProcessAsync(new()
        { CustomerId = _customer, RequestedAt = item.RequestedAt.AddTicks(1) }, default));
        Assert.That(error!.Code, Is.EqualTo("consent_withdrawal_request_not_found"));
        var processed = await service.ProcessAsync(new()
        { CustomerId = _customer, RequestedAt = item.RequestedAt }, default);
        Assert.That(processed.Processed, Is.True);
        Assert.That(await database.CustomerConsentWithdrawalRequests.CountAsync(), Is.EqualTo(1));
    }

    [TestCase("request", 20, "invalid_phone")]
    [TestCase("verify", 30, "invalid_phone")]
    public async Task AuthenticationIpQuotaLimitsMalformedPhoneAttempts(string operation, int limit, string expected)
    {
        await using var database = Database();
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
        for (var attempt = 0; attempt < limit; attempt++) Assert.That((await Assert.ThrowsAsync<ServiceException>(Attempt))!.Code, Is.EqualTo(expected));
        Assert.That((await Assert.ThrowsAsync<ServiceException>(Attempt))!.Code, Is.EqualTo("rate_limited"));
        Assert.That(await database.ConsentOnboarding.CountAsync(), Is.Zero);
    }

    private sealed class ReviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
