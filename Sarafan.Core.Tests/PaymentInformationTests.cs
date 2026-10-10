// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;
using Sarafan.Core.StoreTests;

namespace Sarafan.Core.Tests;

[TestFixture]
public sealed class PaymentInformationTests
{
    internal static readonly string[] Admin = [BackofficeRoles.Administrator];
    private AppDbContext _db = null!;
    private DbContextOptions<AppDbContext> _options = null!;
    private PaymentInformationService _service = null!;
    private readonly PaymentClock _clock = new();
    private const int Actor = 1;

    [SetUp]
    public void Setup()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new(_options);
        _db.BackofficeUsers.Add(new()
        {
            Id = Actor,
            Email = "payment@test",
            NormalizedEmail = "payment@test",
            FirstName = "Payment",
            LastName = "Administrator",
            PasswordHash = "unused",
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow()
        });
        _db.SaveChanges();
        _service = Service(_db);
    }
    [TearDown] public void TearDown() => _db.Dispose();
    private PaymentInformationService Service(AppDbContext db) => new(db, _clock, NullLogger<PaymentInformationService>.Instance);

    internal static PaymentInformationWriteRequest Complete(bool qr = true) => new()
    {
        RecipientType = PaymentRecipientType.LegalEntity,
        RecipientName = " Получатель ",
        Inn = "0012345678",
        Kpp = "001234567",
        SettlementAccount = "00000000000000000001",
        BankName = "Банк",
        Bik = "001234567",
        CorrespondentAccount = "00000000000000000002",
        PaymentLink = "https://BANK.example/Pay/Ab%2Fc?Token=AbC+%2B&x=1",
        Qr = qr ? StoreServiceTests.Upload(StoreServiceTests.Png) : null
    };
    private Task<PaymentBundleDto> Create(PaymentInformationWriteRequest? request = null) => _service.CreateAsync(request ?? Complete(), Actor, Admin, default);
    private Task<PaymentBundleDto> Enable(PaymentBundleDto row, PaymentBundleDto? active = null) => _service.EnableAsync(row.Id,
        new(row.Version, active is null ? null : new(active.Id, active.Version)), Actor, Admin, default);
    private static ServiceException Reject(Func<Task> action, string code, int status = 409)
    {
        var problem = Assert.ThrowsAsync<ServiceException>(action).GetAwaiter().GetResult()!;
        Assert.That(problem.Code, Is.EqualTo(code)); Assert.That(problem.StatusCode, Is.EqualTo(status)); return problem;
    }

    [Test]
    public async Task CompleteLifecycleFreezesCopiesSwitchesAndDeletesInactivePublishedBundles()
    {
        Assert.That((await _service.CurrentAsync(default)).PaymentInformation, Is.Null);
        var draft = await Create(new());
        Assert.That(draft.State, Is.EqualTo("draft")); Assert.That(draft.CanEdit, Is.True); Assert.That(draft.CanEnable, Is.False);
        Reject(async () => await Enable(draft), "validation_failed", 400);
        Reject(async () => await _service.CopyAsync(draft.Id, draft.Version, Actor, Admin, default), "payment_bundle_copy_unavailable");
        var request = Complete(); request.Version = draft.Version;
        var complete = await _service.UpdateAsync(draft.Id, request, Actor, Admin, default);
        Assert.That(complete.Information.RecipientName, Is.EqualTo("Получатель"));
        Assert.That(complete.Information.Inn, Is.EqualTo("0012345678"));
        Assert.That(complete.Information.PaymentLink, Is.EqualTo(request.PaymentLink));
        Assert.That(complete.CanEnable, Is.True); Assert.That(complete.Version, Is.Not.EqualTo(draft.Version));
        var first = await Enable(complete);
        Assert.That(first.Enabled, Is.True); Assert.That(first.State, Is.EqualTo("enabled"));
        Assert.That(first.CanEdit, Is.False); Assert.That(first.CanDelete, Is.False); Assert.That(first.CanCopy, Is.True);
        var idempotent = await Enable(first, first);
        Assert.That(idempotent.Version, Is.EqualTo(first.Version));
        request.Version = first.Version;
        Reject(async () => await _service.UpdateAsync(first.Id, request, Actor, Admin, default), "payment_bundle_frozen");
        Reject(async () => await _service.DeleteAsync(first.Id, first.Version, Actor, Admin, default), "payment_bundle_enabled");
        var current = (await _service.CurrentAsync(default)).PaymentInformation!;
        Assert.That(current.BundleId, Is.EqualTo(first.Id));
        var digest = current.QrUrl.Split("v=")[1];
        var image = await _service.QrAsync(null, digest, null, default);
        Assert.That(image.Content, Is.EqualTo(StoreServiceTests.Png));
        Reject(async () => await _service.QrAsync(null, "wrong", null, default), "resource_not_found", 404);
        Reject(async () => await _service.QrAsync(999, digest, Admin, default), "resource_not_found", 404);
        var copy = await _service.CopyAsync(first.Id, first.Version, Actor, Admin, default);
        Assert.That(copy.State, Is.EqualTo("draft")); Assert.That(copy.CanEdit && copy.CanEnable && copy.CanDelete, Is.True);
        Assert.That(copy.Information, Is.EqualTo(first.Information)); Assert.That(copy.Version, Is.Not.EqualTo(first.Version));
        Assert.That((await _service.QrAsync(copy.Id, digest, Admin, default)).Content, Is.EqualTo(StoreServiceTests.Png));
        var second = await Enable(copy, first);
        var old = await _service.GetAsync(first.Id, Admin, default);
        Assert.That(old.State, Is.EqualTo("disabled")); Assert.That(old.CanDelete, Is.True); Assert.That(old.CanEdit, Is.False);
        Assert.That(old.Version, Is.Not.EqualTo(first.Version));
        request.Version = old.Version;
        Reject(async () => await _service.UpdateAsync(old.Id, request, Actor, Admin, default), "payment_bundle_frozen");
        var restored = await Enable(old, second);
        Assert.That(restored.State, Is.EqualTo("enabled")); Assert.That(restored.CanEdit, Is.False);
        var disabledSecond = await _service.GetAsync(second.Id, Admin, default);
        await _service.DeleteAsync(second.Id, disabledSecond.Version, Actor, Admin, default);
        Reject(async () => await _service.GetAsync(second.Id, Admin, default), "resource_not_found", 404);
        var inactive = await _service.DisableAsync(restored.Id, restored.Version, Actor, Admin, default);
        Assert.That((await _service.CurrentAsync(default)).PaymentInformation, Is.Null);
        Reject(async () => await _service.QrAsync(null, digest, null, default), "resource_not_found", 404);
        Reject(async () => await _service.DisableAsync(inactive.Id, inactive.Version, Actor, Admin, default), "payment_bundle_update_conflict");
        var enabledAgain = await Enable(inactive);
        Assert.That(enabledAgain.State, Is.EqualTo("enabled")); Assert.That(enabledAgain.CanEdit, Is.False);
        Assert.That(_db.PaymentInformationBundles.Count(row => row.Enabled), Is.EqualTo(1));
    }

    [Test]
    public async Task DraftUpdatesRetainAndReplaceOriginalImagesAndAllowMissingFields()
    {
        var draft = await Create();
        var request = Complete(false); request.Version = draft.Version;
        var unchanged = await _service.UpdateAsync(draft.Id, request, Actor, Admin, default);
        Assert.That(unchanged.QrUrl, Is.EqualTo(draft.QrUrl));
        request.Version = unchanged.Version; request.Qr = StoreServiceTests.Upload(StoreImageFixtures.Webp, "IMAGE/WEBP");
        var replaced = await _service.UpdateAsync(draft.Id, request, Actor, Admin, default);
        Assert.That(replaced.QrUrl, Is.Not.EqualTo(draft.QrUrl));
        var stored = _db.PaymentInformationBundles.Single();
        Assert.That(stored.QrContent, Is.EqualTo(StoreImageFixtures.Webp));
        Assert.That(stored.QrContentType, Is.EqualTo("image/webp"));
        var empty = new PaymentInformationWriteRequest { Version = replaced.Version, RecipientName = "  " };
        var result = await _service.UpdateAsync(draft.Id, empty, Actor, Admin, default);
        Assert.That(result.Information.RecipientName, Is.Null); Assert.That(result.QrUrl, Is.EqualTo(replaced.QrUrl));
        await _service.DeleteAsync(result.Id, result.Version, Actor, Admin, default);
        Assert.That(_db.PaymentInformationBundles.Any(), Is.False);
        var noImage = await Create(new());
        Assert.That(noImage.QrUrl, Is.Null); Assert.That(noImage.CanEnable, Is.False);
    }

    [Test]
    public async Task SelectedAndObservedTokensRejectStaleWritesAndSelectionChanges()
    {
        var one = await Create(); var two = await Create();
        Reject(async () => await _service.EnableAsync(one.Id, new(Guid.NewGuid(), null), Actor, Admin, default), "payment_bundle_update_conflict");
        Reject(async () => await _service.EnableAsync(one.Id, new(null, null), Actor, Admin, default), "invalid_payment_bundle_version", 400);
        Reject(async () => await _service.DeleteAsync(one.Id, Guid.Empty, Actor, Admin, default), "invalid_payment_bundle_version", 400);
        var active = await Enable(one);
        Reject(async () => await Enable(two), "payment_bundle_update_conflict");
        Reject(async () => await _service.EnableAsync(two.Id, new(two.Version, new(active.Id, Guid.NewGuid())), Actor, Admin, default), "payment_bundle_update_conflict");
        Reject(async () => await _service.EnableAsync(two.Id, new(two.Version, new(999, active.Version)), Actor, Admin, default), "payment_bundle_update_conflict");
        Reject(async () => await _service.DeleteAsync(two.Id, Guid.NewGuid(), Actor, Admin, default), "payment_bundle_update_conflict");
        Reject(async () => await _service.CopyAsync(999, two.Version, Actor, Admin, default), "resource_not_found", 404);
        var before = await _service.GetAsync(two.Id, Admin, default);
        var request = Complete(false); request.Version = before.Version; request.BankName = "Другой банк";
        await _service.UpdateAsync(two.Id, request, Actor, Admin, default);
        Reject(async () => await _service.UpdateAsync(two.Id, request, Actor, Admin, default), "payment_bundle_update_conflict");
        Reject(async () => await _service.CreateAsync(new(), 999, Admin, default), "backoffice_user_not_found", 404);
        Assert.That((await _service.CurrentAsync(default)).PaymentInformation!.BundleId, Is.EqualTo(active.Id));
    }

    [TestCase("operator")]
    [TestCase("shift-manager")]
    [TestCase("senior-operator")]
    [TestCase("unknown")]
    public async Task EveryManagementServiceBoundaryDeniesOtherRoles(string role)
    {
        var row = await Create();
        string[] roles = [role];
        Assert.Throws<ServiceException>(() => _service.Operations(roles));
        Reject(async () => await _service.ListAsync(roles, 1, 10, "id", "desc", null, null, default), "access_denied", 403);
        Reject(async () => await _service.GetAsync(row.Id, roles, default), "access_denied", 403);
        Reject(async () => await _service.QrAsync(row.Id, row.QrUrl!.Split("v=")[1], roles, default), "access_denied", 403);
        Reject(async () => await _service.CreateAsync(new(), Actor, roles, default), "access_denied", 403);
        Reject(async () => await _service.UpdateAsync(row.Id, new(), Actor, roles, default), "access_denied", 403);
        Reject(async () => await _service.CopyAsync(row.Id, row.Version, Actor, roles, default), "access_denied", 403);
        Reject(async () => await _service.EnableAsync(row.Id, new(row.Version, null), Actor, roles, default), "access_denied", 403);
        Reject(async () => await _service.DisableAsync(row.Id, row.Version, Actor, roles, default), "access_denied", 403);
        Reject(async () => await _service.DeleteAsync(row.Id, row.Version, Actor, roles, default), "access_denied", 403);
    }

    [Test]
    public async Task PagingSearchSortingAndFiltersUseDisplayedFields()
    {
        var first = await Enable(await Create());
        await Create(new());
        await Create(Complete());
        var active = await Enable(await Create(), first);
        var ops = _service.Operations(Admin);
        Assert.That(ops.States.Select(item => item.Name), Is.EqualTo(new[] { "Черновик", "Включён", "Отключён" }));
        foreach (var sort in new[] { "id", "recipientName", "inn", "bankName", "state", "createdAt" })
            foreach (var direction in new[] { "asc", "desc" })
            {
                var page = await _service.ListAsync(Admin, 1, 10, sort, direction, null, null, default);
                Assert.That(page.Items.Length, Is.EqualTo(4)); Assert.That(page.Sorting.SortBy, Is.EqualTo(sort));
                Assert.That(page.EnabledBundle, Is.EqualTo(new EnabledPaymentBundle(active.Id, active.Version)));
            }
        foreach (var state in new[] { "draft", "enabled", "disabled" })
        {
            var page = await _service.ListAsync(Admin, 1, 25, "id", "desc", null, state, default);
            Assert.That(page.Items.All(row => row.State == state), Is.True);
        }
        foreach (var search in new[] { "включён", "черновик", "отключён", "Получатель", "0012345678", "БАНК", "10.10.2026", "—", "  " })
        {
            var page = await _service.ListAsync(Admin, 1, 50, "id", "desc", search, null, default);
            Assert.That(page.Items, Is.Not.Empty, search);
        }
        var empty = await _service.ListAsync(Admin, 2, 100, "id", "desc", "unmatched%", null, default);
        Assert.That(empty.Items, Is.Empty); Assert.That(empty.Pagination.TotalPages, Is.Zero);
        Assert.That(empty.Pagination.HasPreviousPage, Is.True);
        foreach (var item in new[] { (0,10,"id","desc", (string?)null,(string?)null),
            (1000001,10,"id","desc",null,null), (1,11,"id","desc",null,null), (1,10,"unknown","desc",null,null),
            (1,10,"id","DESC",null,null), (1,10,"id","desc",new string('x',201),null), (1,10,"id","desc",null,"unknown") })
            Reject(async () => await _service.ListAsync(Admin, item.Item1, item.Item2, item.Item3, item.Item4, item.Item5, item.Item6, default), "invalid_payment_bundle_filter", 400);
    }

    [TestCase("recipientType", "99")]
    [TestCase("recipientName", "Na\nme")]
    [TestCase("bankName", "Ba\tnk")]
    [TestCase("inn", "123")]
    [TestCase("inn", "123456789x")]
    [TestCase("kpp", "123")]
    [TestCase("settlementAccount", "123")]
    [TestCase("bik", "123")]
    [TestCase("correspondentAccount", "123")]
    [TestCase("paymentLink", "http://bank.example/pay")]
    [TestCase("paymentLink", "https://user:password@bank.example/pay")]
    [TestCase("paymentLink", "javascript:alert(1)")]
    [TestCase("paymentLink", "https://")]
    [TestCase("paymentLink", "https://bank.example/\\path")]
    [TestCase("paymentLink", "https://bank.example/ab\ncd")]
    public void SuppliedDraftValuesAreValidated(string field, string value)
    {
        var request = Complete();
        var property = typeof(PaymentInformationWriteRequest).GetProperties().Single(item => item.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        property.SetValue(request, field == "recipientType" ? (PaymentRecipientType)99 : value);
        var failure = Reject(async () => await Create(request), "validation_failed", 400);
        Assert.That(failure.Errors!.ContainsKey(field), Is.True);
        Assert.That(_db.PaymentInformationBundles.Any(), Is.False);
    }

    [Test]
    public async Task RequiredFieldsAndRecipientRulesAreEnforcedOnlyAtEnablement()
    {
        var request = Complete(false);
        foreach (var property in typeof(PaymentInformationWriteRequest).GetProperties().Where(item => item.PropertyType == typeof(string) && item.Name != "Kpp"))
        {
            var incomplete = Complete(); property.SetValue(incomplete, null);
            var draft = await Create(incomplete);
            Assert.That(draft.CanEnable, Is.False);
            Reject(async () => await Enable(draft), "validation_failed", 400);
        }
        var withoutQr = await Create(request);
        Reject(async () => await Enable(withoutQr), "validation_failed", 400);
        request = Complete(); request.RecipientType = null;
        var missingType = await Create(request);
        Reject(async () => await Enable(missingType), "validation_failed", 400);
        request = Complete(); request.Kpp = null;
        Reject(async () => await Enable(await Create(request)), "validation_failed", 400);
        request = Complete(); request.RecipientType = PaymentRecipientType.IndividualEntrepreneur; request.Inn = "001234567890";
        Reject(async () => await Create(request), "validation_failed", 400);
        request.Kpp = null;
        var ip = await Enable(await Create(request));
        Assert.That(ip.Information.Kpp, Is.Null);
        foreach (var field in new[] { "RecipientName", "BankName", "PaymentLink" })
        {
            request = Complete();
            typeof(PaymentInformationWriteRequest).GetProperty(field)!.SetValue(request, new string('a', field == "PaymentLink" ? 2049 : 201));
            Reject(async () => await Create(request), "validation_failed", 400);
        }
        var unknown = Complete(); unknown.RecipientType = null; unknown.Kpp = null;
        Assert.That((await Create(unknown)).CanEnable, Is.False);
    }

    [Test]
    public async Task ImageLimitsAndStaticFormatsAreSharedWithStores()
    {
        foreach (var (bytes, type) in new[] { (StoreServiceTests.Png, "image/png"), (StoreImageFixtures.Webp, "image/webp"), (StoreImageFixtures.Jpeg, "image/jpeg") })
        {
            var request = Complete(); request.Qr = StoreServiceTests.Upload(bytes, type);
            var row = await Create(request);
            Assert.That((await Enable(row)).Enabled, Is.True);
            var active = await _service.GetAsync(row.Id, Admin, default);
            await _service.DisableAsync(row.Id, active.Version, Actor, Admin, default);
        }
        foreach (var file in new[] {
            StoreServiceTests.Upload([], "image/png"), StoreServiceTests.Upload([1,2,3], "image/png"),
            StoreServiceTests.Upload(StoreImageFixtures.WebpAnimated, "image/webp"),
            StoreServiceTests.Upload(StoreServiceTests.Png,"image/gif"),
            StoreServiceTests.Upload(StoreServiceTests.Png, length:ImageUpload.MaxBytes+1),
            StoreServiceTests.Upload(StoreServiceTests.Png, length:StoreServiceTests.Png.Length-1),
            StoreServiceTests.Upload(StoreServiceTests.Png, length:StoreServiceTests.Png.Length+1)
        })
        {
            var request = Complete(); request.Qr = file;
            Assert.That(Reject(async () => await Create(request), "validation_failed", 400).Errors!.ContainsKey("qr"), Is.True);
        }
        Assert.That(PaymentInformationRules.InvalidQr("size").Errors!["qr"][0], Does.Contain("байт"));
        Assert.That(PaymentInformationRules.InvalidQr("type").Errors!["qr"][0], Does.Contain("PNG"));
    }

    [Test]
    public async Task ModelCopiesBytesAndEnforcesFrozenContentEvenWithoutService()
    {
        var row = await Create();
        var model = _db.PaymentInformationBundles.Single();
        Assert.That(model.Published, Is.False);
        var original = model.QrContent!.ToArray();
        var copy = model.Copy(Actor, _clock.GetUtcNow());
        Assert.That(copy.Published, Is.False);
        copy.QrContent![0] ^= 1;
        Assert.That(model.QrContent, Is.EqualTo(original));
        var missing = new PaymentInformationBundle(new(null, null, null, null, null, null, null, null, null), Actor, _clock.GetUtcNow());
        Assert.That(missing.Copy(Actor, _clock.GetUtcNow()).QrContent, Is.Null);
        await Enable(row);
        Assert.Throws<InvalidOperationException>(() => model.Update(model.Fields(), Actor, _clock.GetUtcNow(), null));
        model.SetEnabled(false, Actor, _clock.GetUtcNow().AddDays(1));
        Assert.That(model.UpdatedAt, Is.EqualTo(_clock.GetUtcNow().AddDays(1)));
        await _db.SaveChangesAsync();
        await using var fresh = new AppDbContext(_options);
        var persisted = await fresh.PaymentInformationBundles.SingleAsync();
        Assert.That(persisted.Published, Is.True);
        Assert.That(persisted.Enabled, Is.False);
        Assert.Throws<InvalidOperationException>(() => persisted.Update(persisted.Fields(), Actor, _clock.GetUtcNow(), null));
    }

    [Test]
    public async Task QrReadRejectsBytesThatDoNotMatchTheStoredDigest()
    {
        var row = await Enable(await Create());
        var digest = row.QrUrl!.Split("v=")[1];
        var image = _db.PaymentInformationBundles.Single();
        var altered = image.QrContent!.ToArray();
        altered[0] ^= 1;
        var property = _db.Entry(image).Property(item => item.QrContent);
        property.CurrentValue = altered;
        property.IsModified = true;
        await _db.SaveChangesAsync();
        Reject(async () => await _service.QrAsync(null, digest, null, default), "resource_not_found", 404);
        Reject(async () => await _service.QrAsync(row.Id, digest, Admin, default), "resource_not_found", 404);
    }

    [Test]
    public async Task EfInMemoryConcurrencyFailureBecomesConflict()
    {
        var row = await Create();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>(_options)
            .AddInterceptors(new FailSave()).Options);
        var request = Complete(false); request.Version = row.Version;
        Reject(async () => await Service(db).UpdateAsync(row.Id, request, Actor, Admin, default), "payment_bundle_update_conflict");
    }

    [Test]
    public async Task PaymentBoundariesLogOnlySafeSummaries()
    {
        var logger = new PaymentLogger();
        var service = new PaymentInformationService(_db, _clock, logger);
        service.Operations(Admin);
        Reject(async () => await service.CreateAsync(Complete(), Actor, ["operator"], default), "access_denied", 403);
        var draft = await service.CreateAsync(Complete(), Actor, Admin, default);
        var request = Complete(false); request.Version = draft.Version;
        var updated = await service.UpdateAsync(draft.Id, request, Actor, Admin, default);
        var enabled = await service.EnableAsync(updated.Id, new(updated.Version, null), Actor, Admin, default);
        await service.GetAsync(enabled.Id, Admin, default);
        await service.ListAsync(Admin, 1, 25, "createdAt", "desc", null, null, default);
        await service.CurrentAsync(default);
        var digest = enabled.QrUrl!.Split("v=")[1];
        await service.QrAsync(enabled.Id, digest, Admin, default);
        var copied = await service.CopyAsync(enabled.Id, enabled.Version, Actor, Admin, default);
        var disabled = await service.DisableAsync(enabled.Id, enabled.Version, Actor, Admin, default);
        await service.DeleteAsync(copied.Id, copied.Version, Actor, Admin, default);
        await service.DeleteAsync(disabled.Id, disabled.Version, Actor, Admin, default);
        var text = string.Join("\n", logger.Records);
        Assert.That(text, Does.Contain(nameof(PaymentInformationService) + ".Operations"));
        Assert.That(text, Does.Contain(nameof(PaymentInformationService) + ".CreateAsync"));
        Assert.That(text, Does.Contain("[redacted]"));
        foreach (var kind in new[] { "PaymentBundleOpsDto", "PaymentBundleDto", "CurrentPaymentInformationDto", "StoreLogoDto" })
            Assert.That(text, Does.Contain(kind));
        foreach (var secret in new[] { "Получатель", "0012345678", Complete().PaymentLink!, Complete().Qr!.FileName,
                     digest, enabled.Version.ToString(), Convert.ToBase64String(StoreServiceTests.Png) })
            Assert.That(text, Does.Not.Contain(secret));
        Assert.That(logger.Warnings, Is.Zero);
    }

    private sealed class PaymentLogger : ILogger<PaymentInformationService>
    {
        public List<string> Records { get; } = [];
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add(formatter(state, exception));
            if (logLevel >= LogLevel.Warning) Warnings++;
        }
    }

    private sealed class PaymentClock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero); }
    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new DbUpdateConcurrencyException();
    }
}
