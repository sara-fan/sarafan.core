// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.ComponentModel.DataAnnotations;
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
