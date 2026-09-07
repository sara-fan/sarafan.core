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
    private readonly Dictionary<string, LegalDocumentDto> _documents = [];
    private const string Phone = "+78880000002";
    private const string Browser = "separate-review-browser-receipt-at-least-32-characters";
    private int _customer;

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
        await database.SaveChangesAsync();
        _customer = await database.Customers.Select(x => x.Id).SingleAsync();
        _documents.Clear();
        foreach (var kind in new[] { ConsentKinds.PersonalData, ConsentKinds.Agreement, ConsentKinds.Cookies })
        {
            var draft = await Draft(database, kind);
            _documents[kind] = await Documents(database).PublishAsync(draft.Id, new() { Revision = draft.Revision, Now = true }, 1, default);
        }
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
    private Task<LegalDocumentDto> Draft(AppDbContext database, string kind) => Documents(database).SaveAsync(null,
        new() { Kind = kind, Title = "Тест", DisplayVersion = Guid.NewGuid().ToString(), FileName = "test.md", Source = Encoding.UTF8.GetBytes("# Текст"), CookieCategories = kind == ConsentKinds.Cookies ? ["analytics"] : [] }, 1, default);
    private ConsentDecisionRequest Decision(string kind) => new()
    { DocumentId = _documents[kind].Id, ContentHash = _documents[kind].ContentHash, Decision = "grant", IdempotencyKey = Guid.NewGuid(), Categories = kind == ConsentKinds.Cookies ? ["analytics"] : [] };
    private Task<string> Onboarding(AppDbContext database) => Consents(database).BeginOnboardingAsync(Phone, _documents[ConsentKinds.Agreement].Id, Decision(ConsentKinds.PersonalData), default);

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

    [TestCase(ConsentKinds.PersonalData)]
    [TestCase(ConsentKinds.Cookies)]
    public async Task ConcurrentDuplicateDecisionsPersistExactlyOneEvent(string kind)
    {
        var request = Decision(kind);
        var results = await Race(4, db => kind == ConsentKinds.Cookies
            ? Consents(db).DecideCookiesAsync(Browser, request, default)
            : Consents(db).DecidePersonalDataAsync(_customer, request, default), () => Database());
        Assert.That(results, Is.All.EqualTo("success"));
        await using var check = Database();
        Assert.That(await check.ConsentEvents.CountAsync(), Is.EqualTo(1));
    }

    [TestCase(ConsentKinds.PersonalData)]
    [TestCase(ConsentKinds.Cookies)]
    public async Task ExactRetryAfterVersionChangeReturnsStatusWithoutNewEvidence(string kind)
    {
        await using var database = Database();
        var request = Decision(kind);
        var service = Consents(database);
        if (kind == ConsentKinds.Cookies) await service.DecideCookiesAsync(Browser, request, default);
        else await service.DecidePersonalDataAsync(_customer, request, default);
        _clock.Now = _clock.Now.AddSeconds(1);
        var draft = await Draft(database, kind);
        await Documents(database).PublishAsync(draft.Id, new() { Revision = draft.Revision, Now = true }, 1, default);
        var status = kind == ConsentKinds.Cookies
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
    public async Task ConcurrentPublicationKeepsOnlyOneNextVersion()
    {
        await using var setup = Database();
        var drafts = new[] { await Draft(setup, ConsentKinds.PersonalData), await Draft(setup, ConsentKinds.PersonalData) };
        var index = -1;
        var date = DateOnly.FromDateTime(_clock.Now.UtcDateTime.AddDays(2));
        var results = await Race(2, async db =>
        {
            var draft = drafts[Interlocked.Increment(ref index)];
            await Documents(db).PublishAsync(draft.Id, new() { Revision = draft.Revision, EffectiveDate = date }, 1, default);
        }, () => Database());
        Assert.That(results, Is.EquivalentTo(new[] { "success", "consent_conflict" }));
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
        var kind = operation == ConsentKinds.Cookies ? ConsentKinds.Cookies : ConsentKinds.PersonalData;
        var next = await Draft(setup, kind);
        await Documents(setup).PublishAsync(next.Id, new() { Revision = next.Revision, EffectiveDate = date }, 1, default);
        await using var action = Database(new AfterSave(() => _clock.Now = ConsentCalendar.Midnight(date)));
        var service = Consents(action);
        var error = Assert.ThrowsAsync<ServiceException>(async () =>
        {
            if (operation == "begin-onboarding") await Onboarding(action);
            else if (operation == "complete-onboarding") await service.CompleteOnboardingAsync(await action.Customers.SingleAsync(), receipt, default);
            else if (operation == ConsentKinds.Cookies) await service.DecideCookiesAsync(Browser, Decision(kind), default);
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
        var receipt = await Onboarding(database);
        _clock.Now = _clock.Now.AddSeconds(1);
        var draft = await Draft(database, ConsentKinds.Agreement);
        await Documents(database).PublishAsync(draft.Id, new() { Now = true, Revision = draft.Revision }, 1, default);
        foreach (var action in new Func<Task>[] {
            () => Consents(database).ValidateOnboardingReceiptAsync(receipt, default),
            async () => await Consents(database).CompleteOnboardingAsync(await database.Customers.SingleAsync(), receipt, default) })
        {
            var error = Assert.ThrowsAsync<ServiceException>(async () => await action());
            Assert.That(error!.RequiredDocumentId, Is.EqualTo(draft.Id));
            Assert.That(error.ConsentKind, Is.EqualTo(ConsentKinds.Agreement));
        }
        Assert.That(await database.ConsentEvents.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task AssociationRequiresAndPreservesAuthenticatedTokenProvenance()
    {
        await using var database = Database();
        var service = Consents(database);
        await service.DecideCookiesAsync(Browser, Decision(ConsentKinds.Cookies), default);
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
        var receipt = await Consents(setup).BeginOnboardingAsync(phone, _documents[ConsentKinds.Agreement].Id, Decision(ConsentKinds.PersonalData), default);
        await using var database = Database(new FailEvidenceSave());
        var service = new AuthenticationService(database, new PhoneNormalizer(), new PhoneSuffixVerificationCodeProvider(),
            new VerificationAttemptStore(_clock), new JwtTokenService(_auth, _clock, NullLogger<JwtTokenService>.Instance),
            _auth, _clock, Consents(database), NullLogger<AuthenticationService>.Instance);
        Assert.ThrowsAsync<DbUpdateException>(() => service.VerifyCodeAsync(new()
        { Phone = phone, Purpose = "register", Code = "0003", TermsAccepted = true, OnboardingToken = receipt }, "test", null, default));
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
    public async Task PublicationReturnsExplicitMoscowDateForDraftScheduledAndImmediateDocuments()
    {
        await using var database = Database();
        var draft = await Draft(database, ConsentKinds.Cookies);
        Assert.That(draft.EffectiveAt, Is.Null);
        Assert.That(draft.EffectiveLocalDate, Is.Null);
        Assert.That(draft.EffectiveTimeZone, Is.EqualTo("Europe/Moscow"));
        var date = ConsentCalendar.LocalDate(_clock.Now).AddDays(2);
        var scheduled = await Documents(database).PublishAsync(draft.Id, new() { Revision = draft.Revision, EffectiveDate = date }, 1, default);
        Assert.That(scheduled.EffectiveLocalDate, Is.EqualTo(date));
        Assert.That(scheduled.EffectiveAt, Is.EqualTo(ConsentCalendar.Midnight(date)));
        Assert.That(scheduled.EffectiveTimeZone, Is.EqualTo("Europe/Moscow"));
        _clock.Now = ConsentCalendar.Midnight(date.AddDays(1)).AddMinutes(30);
        var immediateDraft = await Draft(database, ConsentKinds.PersonalData);
        var immediate = await Documents(database).PublishAsync(immediateDraft.Id, new() { Revision = immediateDraft.Revision, Now = true }, 1, default);
        Assert.That(immediate.EffectiveAt, Is.EqualTo(_clock.Now));
        Assert.That(immediate.EffectiveLocalDate, Is.EqualTo(date.AddDays(1)));
        Assert.That(immediate.EffectiveLocalDate, Is.Not.EqualTo(DateOnly.FromDateTime(_clock.Now.UtcDateTime)));
        Assert.That(immediate.EffectiveTimeZone, Is.EqualTo("Europe/Moscow"));
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
            Phone = Phone,
            Purpose = "register",
            TermsAccepted = true,
            TermsDocumentId = _documents[ConsentKinds.Agreement].Id,
            PersonalDataConsent = Decision(ConsentKinds.PersonalData)
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
        var document = _documents[ConsentKinds.Cookies];
        setup.ConsentEvents.AddRange(Enumerable.Range(0, 2001).Select(index => new ConsentEvent
        {
            SubjectKey = $"expired-browser-{index}",
            DocumentId = document.Id,
            ContentHash = document.ContentHash,
            Kind = ConsentKinds.Cookies,
            Decision = "grant",
            IdempotencyKey = Guid.NewGuid(),
            At = _clock.Now.AddDays(-1100),
            RetainUntil = _clock.Now.AddDays(-1)
        }));
        await setup.SaveChangesAsync();
        var commands = new CountCommands();
        await using var database = Database(commands);
        var service = new ConsentRetentionService(database, _clock, Options.Create(new ConsentOptions()), NullLogger<ConsentRetentionService>.Instance);
        var result = await service.SweepAsync(default);
        Assert.That(result.Events, Is.EqualTo(2001));
        Assert.That(commands.Count, Is.LessThanOrEqualTo(15), "Each page must use set-based queries, not per-event lookups.");
        Assert.That(await setup.ConsentEvents.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task RetentionPagesArtifactsAndPreservesCurrentFutureAndReferencedDocuments()
    {
        await using var setup = Database();
        var old = _clock.Now.AddDays(-1100);
        LegalDocument Artifact(string version, string state = "published") => new()
        {
            Kind = ConsentKinds.Cookies,
            DisplayVersion = version,
            Source = [1, 2, 3],
            Html = "artifact",
            ContentHash = new string('a', 64),
            SourceHash = new string('b', 64),
            CreatedBy = 1,
            CreatedAt = old,
            UpdatedAt = old,
            PublishedAt = state == "published" ? old : null,
            EffectiveAt = state == "published" ? old : null,
            State = state
        };
        setup.LegalDocuments.AddRange(Enumerable.Range(0, 2001).Select(i => Artifact($"expired-{i}", i % 2 == 0 ? "draft" : "published")));
        var future = Artifact("future"); future.EffectiveAt = _clock.Now.AddDays(1);
        var evidenceHeld = Artifact("evidence-held");
        var onboardingHeld = Artifact("onboarding-held");
        setup.LegalDocuments.AddRange(future, evidenceHeld, onboardingHeld);
        foreach (var current in await setup.LegalDocuments.Where(x => x.EffectiveAt == _clock.Now).ToArrayAsync()) current.UpdatedAt = old;
        setup.ConsentEvents.Add(new ConsentEvent
        {
            SubjectKey = "held-browser",
            Document = evidenceHeld,
            Kind = ConsentKinds.Cookies,
            Decision = "refuse",
            ContentHash = evidenceHeld.ContentHash,
            IdempotencyKey = Guid.NewGuid(),
            At = old,
            RetainUntil = _clock.Now.AddDays(1)
        });
        setup.ConsentOnboarding.Add(new ConsentOnboarding
        {
            TokenHash = new string('c', 64),
            PhoneHash = new string('d', 64),
            PersonalDataDocumentId = onboardingHeld.Id,
            TermsDocumentId = _documents[ConsentKinds.Agreement].Id,
            At = _clock.Now,
            ExpiresAt = _clock.Now.AddMinutes(10)
        });
        await setup.SaveChangesAsync();
        var commands = new CountCommands();
        await using var database = Database(commands);
        var result = await new ConsentRetentionService(database, _clock, Options.Create(new ConsentOptions()), NullLogger<ConsentRetentionService>.Instance).SweepAsync(default);
        Assert.That(result.Artifacts, Is.EqualTo(2001));
        Assert.That(commands.Count, Is.LessThanOrEqualTo(20), "Artifact disposal must not query each document.");
        Assert.That(await setup.LegalDocuments.CountAsync(x => x.DisposedAt != null && x.Source.Length == 0 && x.Html == "" && x.CreatedBy == 0 && x.Revision == 2), Is.EqualTo(2001));
        Assert.That(await setup.LegalAuditEvents.CountAsync(x => x.Action == "artifact-disposed"), Is.EqualTo(2001));
        Assert.That(await setup.LegalDocuments.CountAsync(x => x.DisposedAt == null), Is.EqualTo(6));
    }

    [Test]
    public async Task CombinedCustomerAndBrowserHistoryReturnsOnlyTheLatest200Records()
    {
        await using var database = Database();
        for (var index = 0; index < 201; index++)
        {
            ConsentEvent Evidence(string kind, int offset) => new()
            {
                CustomerId = kind == ConsentKinds.PersonalData ? _customer : null,
                SubjectKey = kind == ConsentKinds.PersonalData ? $"customer:{_customer}" : "history-browser",
                DocumentId = _documents[kind].Id,
                ContentHash = _documents[kind].ContentHash,
                Kind = kind,
                Decision = "grant",
                IdempotencyKey = Guid.NewGuid(),
                At = _clock.Now.AddSeconds(index * 2 + offset - 500),
                RetainUntil = _clock.Now.AddDays(1000)
            };
            database.ConsentEvents.Add(Evidence(ConsentKinds.PersonalData, 0));
            database.ConsentAssociations.Add(new ConsentAssociation
            { Event = Evidence(ConsentKinds.Cookies, 1), CustomerId = _customer, AssociatedAt = _clock.Now, AuthenticationTokenId = Guid.NewGuid() });
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
        await longRetention.DecideCookiesAsync(Browser, Decision(ConsentKinds.Cookies), default);
        _clock.Now = _clock.Now.AddDays(10);
        var shorterRetention = new ConsentService(database, _clock, Options.Create(new ConsentOptions { CookieDays = 180, EvidenceDays = 180 }), _auth, NullLogger<ConsentService>.Instance);
        var request = Decision(ConsentKinds.Cookies); request.Decision = denial; request.Categories = [];
        await shorterRetention.DecideCookiesAsync(Browser, request, default);
        _clock.Now = _clock.Now.AddDays(181);
        var retention = new ConsentRetentionService(database, _clock, Options.Create(new ConsentOptions { CookieDays = 180, EvidenceDays = 180 }), NullLogger<ConsentRetentionService>.Instance);
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
