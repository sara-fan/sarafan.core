// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.StoreTests;

[TestFixture]
public sealed class StoreServiceTests
{
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEklEQVR4nGM8kWLEwMDAxAAGABEoAWLadcqhAAAAAElFTkSuQmCC");
    private static readonly string[] Admin = [BackofficeRoles.Administrator];
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private AppDbContext _database = null!;
    private StoreService _service = null!;
    private static readonly IanaTldCatalogSnapshot Tlds = new("2026091400", ["COM", "RU", "XN--P1AI"]);
    private static IanaTldCatalogService Catalogue(AppDbContext db) => new(db, null!, new FixedClock(), NullLogger<IanaTldCatalogService>.Instance);

    [SetUp]
    public void Setup()
    {
        _database = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _database.IanaTldCatalog.Add(Sarafan.Core.Tests.IntegrationTestEnvironment.CreateIanaTldCatalog());
        _database.SaveChanges();
        _service = new(_database, new FixedClock(), NullLogger<StoreService>.Instance, Catalogue(_database));
    }

    [TearDown] public void TearDown() => _database.Dispose();

    [TestCase("shop.example.com", "https://shop.example.com/")]
    [TestCase("//shop.example.com/path", "https://shop.example.com/path")]
    [TestCase("http://shop.example.com/path", "http://shop.example.com/path")]
    [TestCase("магазин.рф", "https://xn--80aairftm.xn--p1ai/")]
    public async Task WritesNormalizeWebsiteAddresses(string input, string expected)
    {
        var request = Request(); request.OfficialUrl = input;
        var created = await _service.CreateAsync(request, Admin, default);
        Assert.That(created.OfficialUrl, Is.EqualTo(expected));
        request.Version = created.Version;
        request.OfficialUrl = "http://example.com";
        Assert.That((await _service.UpdateAsync(created.Id, request, Admin, default)).OfficialUrl, Is.EqualTo("http://example.com/"));
    }

    [TestCase("example.invalid")]
    [TestCase("127.0.0.1")]
    [TestCase("https://[::1]")]
    [TestCase("https://user:password@example.com")]
    [TestCase("javascript:alert(1)")]
    public void WritesRejectInvalidWebsiteAddresses(string input)
    {
        var request = Request(); request.OfficialUrl = input;
        Rejected(async () => await _service.CreateAsync(request, Admin, default), "invalid_store_url");
        Assert.That(_database.Stores.Any(), Is.False);
    }

    [Test]
    public void IpRejectionDoesNotDependOnCatalogueContents()
    {
        var request = Request(); request.OfficialUrl = "https://127.0.0.1/";
        var tlds = new IanaTldCatalogSnapshot("test", ["1"]);
        Assert.That(Assert.Throws<ServiceException>(() => StoreRules.Normalize(request, tlds))!.Code, Is.EqualTo("invalid_store_url"));
    }

    [Test]
    public async Task MissingTldsBlockWritesButLeaveStoreReadsAvailable()
    {
        var created = await _service.CreateAsync(Request(), Admin, default);
        _database.IanaTldCatalog.RemoveRange(_database.IanaTldCatalog);
        await _database.SaveChangesAsync();
        Rejected(async () => await _service.CreateAsync(Request(), Admin, default), "tld_catalog_unavailable", 503);
        var update = Request(); update.Version = created.Version;
        Rejected(async () => await _service.UpdateAsync(created.Id, update, Admin, default), "tld_catalog_unavailable", 503);
        Assert.That((await _service.GetStaffAsync(created.Id, Admin, default)).Version, Is.EqualTo(created.Version));
        Assert.That((await _service.GetLogoAsync(created.Id, Admin, default)).Content, Is.EqualTo(Png));
    }

    internal static StoreWriteRequest Request(StoreStatus status = StoreStatus.Hidden, bool logo = true) => new()
    {
        Name = " Store ",
        Description = " Description ",
        OfficialUrl = " https://shop.example.com/path ",
        Status = status,
        Logo = logo ? Upload(Png) : null,
        DisplayOrder = 4
    };

    internal static FormFile Upload(byte[] bytes, string type = "image/png", long? length = null) => new(new MemoryStream(bytes), 0, length ?? bytes.Length, "logo", "logo")
    { Headers = new HeaderDictionary(), ContentType = type };

    private static void Rejected(Func<Task> action, string code, int status = 400)
    {
        var exception = Assert.ThrowsAsync<ServiceException>(action)!;
        Assert.That(exception.Code, Is.EqualTo(code));
        Assert.That(exception.StatusCode, Is.EqualTo(status));
    }

    [TestCase(0)]
    [TestCase(3)]
    [TestCase(6)]

    public async Task FeaturedFiltersBeforeLimitAndUsesManualOrderWithStableTies(int count)
    {
        var hidden = Request(); hidden.DisplayOrder = 0;
        await _service.CreateAsync(hidden, Admin, default);
        var notFeatured = Request(StoreStatus.Active); notFeatured.DisplayOrder = 1;
        await _service.CreateAsync(notFeatured, Admin, default);
        for (var i = 0; i < count; i++)
        {
            var request = Request(StoreStatus.Priority); request.DisplayOrder = count - i + 2;
            await _service.CreateAsync(request, Admin, default);
        }
        var expected = await _database.Stores.Where(item => item.Status == StoreStatus.Priority)
            .OrderBy(item => item.DisplayOrder).ThenBy(item => item.Id).Take(6).Select(item => item.Id).ToArrayAsync();
        var featured = await _service.ListPublicAsync("recommended", true, default);
        Assert.That(featured.Items.Select(item => item.Id), Is.EqualTo(expected));
        Assert.That(featured.Items.Length, Is.EqualTo(Math.Min(count, 6)));
        Assert.That((await _service.ListPublicAsync("recommended", false, default)).Items.Length, Is.EqualTo(count + 1));
        Assert.That((await _service.ListStaffAsync(StoreStatus.Hidden, Admin, default)).Items.Length, Is.EqualTo(1));
        Assert.That((await _service.ListStaffAsync(null, Admin, default)).Items.Length, Is.EqualTo(count + 2));
    }

    [Test]
    public async Task CreationInitializesAllStateWithoutInventingAMutationTimestamp()
    {
        foreach (var logo in new[] { false, true })
        {
            var request = Request(logo ? StoreStatus.Active : StoreStatus.Hidden, logo); request.DisplayOrder = logo ? 5 : 4;
            var created = await _service.CreateAsync(request, Admin, default);
            Assert.That(created.CreatedAt, Is.EqualTo(Now));
            Assert.That(created.UpdatedAt, Is.EqualTo(created.CreatedAt));
            Assert.That(created.Version, Is.Not.EqualTo(Guid.Empty));
            Assert.That((created.Status, created.DisplayOrder), Is.EqualTo((request.Status, request.DisplayOrder)));
        }
    }

    [Test]
    public async Task PublicReadsExcludeMissingLogosBeforeFeaturedLimitButStaffCanRepairThem()
    {
        for (var i = 0; i < 7; i++)
            _database.Stores.Add(new Store("Broken", "Description", "https://example.test", Now, StoreStatus.Active, 100 + i));
        await _database.SaveChangesAsync();
        for (var i = 0; i < 6; i++) { var request = Request(StoreStatus.Priority); request.DisplayOrder = i; await _service.CreateAsync(request, Admin, default); }
        _database.ChangeTracker.Clear();
        foreach (var featured in new[] { false, true })
        {
            var result = await _service.ListPublicAsync("recommended", featured, default);
            Assert.That(result.Items, Has.Length.EqualTo(6));
            Assert.That(result.Items.All(item => item.Name == "Store" && item.LogoUrl.Contains("?v=")), Is.True);
        }
        Assert.That((await _service.ListStaffAsync(null, Admin, default)).Items, Has.Length.EqualTo(13));
    }

    [Test]
    public async Task EmptyCatalogueAndMixedAlphabetSortingAreIndependentOfOtherData()
    {
        Assert.That((await _service.ListPublicAsync("recommended", false, default)).Items, Is.Empty);
        foreach (var name in new[] { "Zara", "ёж", "Apple", "яблоко", "apple", "Альфа", "ЁЖ" })
        {
            var request = Request(StoreStatus.Active); request.Name = name; request.DisplayOrder = await _database.Stores.CountAsync();
            await _service.CreateAsync(request, Admin, default);
        }
        var recommended = (await _service.ListPublicAsync("recommended", false, default)).Items;
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true);
        Assert.That((await _service.ListPublicAsync("name-asc", false, default)).Items,
            Is.EqualTo(recommended.OrderBy(item => item.Name, comparer).ThenBy(item => item.Id)));
        Assert.That((await _service.ListPublicAsync("name-desc", false, default)).Items,
            Is.EqualTo(recommended.OrderByDescending(item => item.Name, comparer).ThenBy(item => item.Id)));
        Assert.That((await _service.ListPublicAsync("name-desc", true, default)).Items, Is.Empty);
        Rejected(async () => await _service.ListPublicAsync("random", false, default), "invalid_store_sort");
        Rejected(async () => await _service.ListStaffAsync((StoreStatus)7, Admin, default), "invalid_store_status");
    }

    [Test]
    public async Task LifecycleTrimsFieldsRetainsLogoAndSelectionAndRejectsStaleWrites()
    {
        var created = await _service.CreateAsync(Request(), Admin, default);
        Assert.That((created.Name, created.Description, created.OfficialUrl), Is.EqualTo(("Store", "Description", "https://shop.example.com/path")));
        Assert.That(created.Status, Is.EqualTo(StoreStatus.Hidden));
        Assert.That(created.LogoUrl, Does.Contain("/api/v1/backoffice/stores/").And.Contain("?v="));
        Rejected(async () => await _service.GetLogoAsync(created.Id, null, default), "resource_not_found", 404);
        Assert.That((await _service.GetLogoAsync(created.Id, [BackofficeRoles.Operator], default)).Content, Is.EqualTo(Png));

        var update = Request(StoreStatus.Active, false); update.Version = created.Version;
        var active = await _service.UpdateAsync(created.Id, update, [BackofficeRoles.ShiftManager], default);
        Assert.That(active.Version, Is.Not.EqualTo(created.Version));
        Assert.That(active.UpdatedAt, Is.GreaterThan(created.UpdatedAt));
        Assert.That(active.CreatedAt, Is.EqualTo(created.CreatedAt));
        Assert.That((await _service.ListPublicAsync("recommended", false, default)).Items.Single().LogoUrl,
            Does.StartWith($"/api/v1/stores/{created.Id}/logo?v="));
        Assert.That((await _service.GetLogoAsync(created.Id, null, default)).Content, Is.EqualTo(Png));
        Rejected(async () => await _service.UpdateAsync(created.Id, update, Admin, default), "store_update_conflict", 409);
        Rejected(() => _service.DeleteAsync(created.Id, created.Version, Admin, default), "store_update_conflict", 409);

        update.Status = StoreStatus.Hidden; update.Version = active.Version;
        var hidden = await _service.UpdateAsync(created.Id, update, Admin, default);
        Assert.That((hidden.DisplayOrder, hidden.LogoUrl), Is.EqualTo((active.DisplayOrder, active.LogoUrl)));
        Assert.That((await _service.ListPublicAsync("recommended", false, default)).Items, Is.Empty);
        update.Status = StoreStatus.Active; update.Version = hidden.Version;
        var reactivated = await _service.UpdateAsync(created.Id, update, Admin, default);
        await _service.DeleteAsync(created.Id, reactivated.Version, Admin, default);
        Assert.That(await _database.StoreLogos.CountAsync(), Is.Zero);
        Rejected(async () => await _service.GetStaffAsync(created.Id, Admin, default), "resource_not_found", 404);
        Rejected(async () => await _service.GetLogoAsync(created.Id, null, default), "resource_not_found", 404);
        Rejected(async () => await _service.UpdateAsync(created.Id, update, Admin, default), "resource_not_found", 404);
    }

    [Test]
    public async Task HiddenDraftWithoutLogoCannotActivateUntilUploadAndFailedValidationDoesNotMutate()
    {
        var created = await _service.CreateAsync(Request(logo: false), Admin, default);
        Assert.That(created.LogoUrl, Is.Null);
        var update = Request(StoreStatus.Active, false); update.Version = created.Version;
        Rejected(async () => await _service.UpdateAsync(created.Id, update, Admin, default), "store_logo_required");
        Rejected(async () => await _service.CreateAsync(Request(StoreStatus.Active, false), Admin, default), "store_logo_required");
        update.Logo = Upload(Png);
        var active = await _service.UpdateAsync(created.Id, update, Admin, default);
        update.Version = active.Version; update.Name = "Changed"; update.Logo = Upload([1, 2]);
        Rejected(async () => await _service.UpdateAsync(created.Id, update, Admin, default), "invalid_store_logo_content");
        Assert.That((await _service.GetStaffAsync(created.Id, Admin, default)).Name, Is.EqualTo(active.Name));
        Assert.That((await _service.GetStaffAsync(created.Id, Admin, default)).Version, Is.EqualTo(active.Version));
        update.Logo = Upload(Png, "IMAGE/PNG");
        var replaced = await _service.UpdateAsync(created.Id, update, Admin, default);
        Assert.That(replaced.Version, Is.Not.EqualTo(active.Version));
        Assert.That(await _database.StoreLogos.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task RoleMatrixIsEnforcedInServiceAndPublishedInOperations()
    {
        var created = await _service.CreateAsync(Request(), Admin, default);
        foreach (var role in BackofficeRoles.Codes)
        {
            var ops = StoreRules.Operations([role], Tlds);
            Assert.That(ops.Actions, Is.EqualTo(new StoreActionsDto(true, role == BackofficeRoles.Administrator,
                role is BackofficeRoles.Administrator or BackofficeRoles.ShiftManager, role == BackofficeRoles.Administrator)));
            Assert.That(ops.Statuses.Select(item => item.Value), Is.EqualTo(new[] { StoreStatus.Hidden, StoreStatus.Active, StoreStatus.Priority }));
            Assert.That(ops.Limits.DescriptionRecommendedLength, Is.EqualTo(140));
            Assert.That((await _service.GetStaffAsync(created.Id, [role], default)).Id, Is.EqualTo(created.Id));
            if (!ops.Actions.Create) Rejected(async () => await _service.CreateAsync(Request(), [role], default), "access_denied", 403);
            if (!ops.Actions.Edit) Rejected(async () => await _service.UpdateAsync(created.Id, Request(), [role], default), "access_denied", 403);
            if (!ops.Actions.Delete) Rejected(() => _service.DeleteAsync(created.Id, created.Version, [role], default), "access_denied", 403);
        }
        Rejected(async () => await _service.ListStaffAsync(null, ["unknown"], default), "access_denied", 403);
        Rejected(async () => await _service.GetLogoAsync(created.Id, [], default), "access_denied", 403);
    }

    [Test]
    public void ValidationRejectsInvalidFieldsAndVersionsAndPreservesInternalWhitespace()
    {
        foreach (var (change, code) in new (Action<StoreWriteRequest>, string)[]
        {
            (r => r.Name = null, "invalid_store_name"), (r => r.Name = new string('x', 201), "invalid_store_name"),
            (r => r.Description = null, "invalid_store_description"), (r => r.Description = new string('x', 161), "invalid_store_description"),
            (r => r.OfficialUrl = null, "invalid_store_url"), (r => r.OfficialUrl = new string('x', 2049), "invalid_store_url"),
            (r => r.OfficialUrl = "https://shop.example/ab\ncd", "invalid_store_url"),
            (r => r.OfficialUrl = "https://shop.example\\path", "invalid_store_url"),
            (r => r.OfficialUrl = "not a URL", "invalid_store_url"), (r => r.OfficialUrl = "ftp://shop.example", "invalid_store_url"),
            (r => r.OfficialUrl = "https://user:pass@shop.example", "invalid_store_url"),
            (r => r.Status = (StoreStatus)5, "invalid_store_status"), (r => r.DisplayOrder = -1, "invalid_store_display_order")
        })
        {
            var request = Request(); change(request);
            Assert.That(Assert.Throws<ServiceException>(() => StoreRules.Normalize(request, Tlds))!.Code, Is.EqualTo(code));
        }
        var valid = Request(); valid.Name = "  Shop  NAME  "; valid.Description = new string('я', 160); valid.OfficialUrl = "http://shop.example.com";
        Assert.That(StoreRules.Normalize(valid, Tlds).Name, Is.EqualTo("Shop  NAME"));
        foreach (var version in new Guid?[] { null, Guid.Empty })
        {
            Rejected(() => _service.DeleteAsync(1, version, Admin, default), "invalid_store_version");
            valid.Version = version;
            Rejected(async () => await _service.UpdateAsync(1, valid, Admin, default), "invalid_store_version");
        }
    }

    [Test]
    public async Task UploadValidationBoundsLengthAndChecksTypeAndContents()
    {
        Assert.That(await StoreRules.ReadLogoAsync(null, default), Is.Null);
        foreach (var (file, code) in new[]
        {
            (Upload([], length: 0), "invalid_store_logo_size"), (Upload([], length: StoreRules.LogoMaxBytes + 1), "invalid_store_logo_size"),
            (Upload(Png, "image/svg+xml"), "invalid_store_logo_type"),
            (Upload(Png, length: Png.Length + 1), "invalid_store_logo_size"),
            (Upload([0, 1], "image/png"), "invalid_store_logo_content"),
            (Upload(Png, "image/jpeg"), "invalid_store_logo_content"),
            (Upload(Png, "image/webp"), "invalid_store_logo_content")
        }) Rejected(async () => await StoreRules.ReadLogoAsync(file, default), code);
        Assert.That((await StoreRules.ReadLogoAsync(Upload(Png), default))!.Value.Content, Is.EqualTo(Png));
        foreach (var index in new[] { 0, 8, 12, 19, 23, Png.Length - 1 })
        {
            var invalid = Png.ToArray(); invalid[index] ^= 1;
            Rejected(async () => await StoreRules.ReadLogoAsync(Upload(invalid), default), "invalid_store_logo_content");
        }
        var jpeg = StoreImageFixtures.Jpeg;
        Assert.That(await StoreRules.ReadLogoAsync(Upload(jpeg, "image/jpeg"), default), Is.Not.Null);
        foreach (var bytes in new[] { new byte[] { 0 }, new byte[] { 255, 0, 255, 217 }, new byte[] { 255, 216, 0, 217 }, new byte[] { 255, 216, 255, 0 }, new byte[] { 255, 216, 255, 217 } })
            Rejected(async () => await StoreRules.ReadLogoAsync(Upload(bytes, "image/jpeg"), default), "invalid_store_logo_content");
        foreach (var bytes in new[] { StoreImageFixtures.Webp, StoreImageFixtures.WebpLossless, StoreImageFixtures.WebpExtended, StoreImageFixtures.WebpAnimated })
        {
            Assert.That(await StoreRules.ReadLogoAsync(Upload(bytes, "image/webp"), default), Is.Not.Null);
            foreach (var index in new[] { 0, 4, 8 })
            {
                var invalid = bytes.ToArray(); invalid[index] ^= 1;
                Rejected(async () => await StoreRules.ReadLogoAsync(Upload(invalid, "image/webp"), default), "invalid_store_logo_content");
            }
        }
        foreach (var kind in new[] { "VP8 ", "VP8L", "VP8X" })
        {
            var emptyFrame = "RIFF\f\0\0\0WEBP"u8.ToArray().Concat(System.Text.Encoding.ASCII.GetBytes(kind)).Concat(new byte[4]).ToArray();
            Rejected(async () => await StoreRules.ReadLogoAsync(Upload(emptyFrame, "image/webp"), default), "invalid_store_logo_content");
        }
    }

    [Test]
    public async Task SaveConcurrencyFailuresAreTranslatedAndSensitiveLogValuesAreRedacted()
    {
        var fault = new ConcurrencyFailure();
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(fault).Options);
        database.IanaTldCatalog.Add(Sarafan.Core.Tests.IntegrationTestEnvironment.CreateIanaTldCatalog());
        await database.SaveChangesAsync();
        var logger = new CaptureLogger();
        var service = new StoreService(database, new FixedClock(), logger, Catalogue(database));
        var created = await service.CreateAsync(Request(), Admin, default);
        var update = Request(); update.Version = created.Version;
        fault.Enabled = true;
        Rejected(async () => await service.UpdateAsync(created.Id, update, Admin, default), "store_update_conflict", 409);
        Assert.That(database.ChangeTracker.Entries(), Is.Empty);
        Rejected(() => service.DeleteAsync(created.Id, created.Version, Admin, default), "store_update_conflict", 409);
        Assert.That(logger.Messages.Any(item => item.Contains("operation.entered")), Is.True);
        Assert.That(string.Join(" ", logger.Messages), Does.Not.Contain("shop.example.com").And.Not.Contain(created.Version.ToString()));
        foreach (var value in new object[] { Request(), new DeleteStoreRequest(created.Version), created,
            new StoreLogoDto(Png, "image/png", "secret"), new StoreListDto<PublicStoreDto>([]),
            new StoreListDto<StaffStoreDto>([]), StoreRules.Operations(Admin, Tlds) })
            Assert.That(LogValueSummary.Describe(value), Does.Not.Contain("secret").And.Not.Contain("shop.example.com"));
    }

    [TestCase("invalid_store_sort", 400)]
    [TestCase("invalid_store_name", 400)]
    [TestCase("invalid_store_description", 400)]
    [TestCase("invalid_store_url", 400)]
    [TestCase("invalid_store_status", 400)]
    [TestCase("invalid_store_display_order", 400)]
    [TestCase("invalid_store_version", 400)]
    [TestCase("store_update_conflict", 409)]
    [TestCase("store_logo_required", 400)]
    [TestCase("invalid_store_logo_size", 400)]
    [TestCase("invalid_store_logo_type", 400)]
    [TestCase("invalid_store_logo_content", 400)]
    public void StoreProblemsHaveStableRussianContracts(string code, int status)
    {
        var problem = new SarafanProblemDetailsFactory().Create(new DefaultHttpContext(), status, code);
        Assert.That(problem.Status, Is.EqualTo(status));
        Assert.That(problem.Code, Is.EqualTo(code));
        Assert.That(problem.Type, Is.EqualTo(SarafanProblemDetailsFactory.TypeBase + code.Replace('_', '-')));
        Assert.That(problem.Title, Does.Match("[А-Яа-я]"));
        Assert.That(problem.Detail, Does.Match("[А-Яа-я]"));
    }

    [TestCase(StoreStatus.Hidden)]
    [TestCase(StoreStatus.Active)]
    [TestCase(StoreStatus.Priority)]
    public async Task OrderNumbersAreUniqueAcrossStatusesButCurrentNumberCanBeRetained(StoreStatus status)
    {
        var created = await _service.CreateAsync(Request(status), Admin, default);
        Rejected(async () => await _service.CreateAsync(Request(), Admin, default), "store_display_order_conflict", 409);
        var update = Request(status); update.Version = created.Version;
        var same = await _service.UpdateAsync(created.Id, update, Admin, default);
        Assert.That(same.DisplayOrder, Is.EqualTo(created.DisplayOrder));
        var other = Request(); other.DisplayOrder = 5;
        var second = await _service.CreateAsync(other, Admin, default);
        update.Version = second.Version;
        Rejected(async () => await _service.UpdateAsync(second.Id, update, Admin, default), "store_display_order_conflict", 409);
        Assert.That((await _service.GetStaffAsync(second.Id, Admin, default)).Version, Is.EqualTo(second.Version));
    }

    [Test]
    public async Task PriorityLimitAllowsEditsAndFreesCapacityOnDemotion()
    {
        StaffStoreDto? first = null;
        for (var i = 0; i < StoreRules.MaxPriorityStores; i++)
        {
            var request = Request(StoreStatus.Priority); request.DisplayOrder = i;
            first ??= await _service.CreateAsync(request, Admin, default);
            if (i > 0) await _service.CreateAsync(request, Admin, default);
        }
        var extra = Request(StoreStatus.Priority); extra.DisplayOrder = 6;
        Rejected(async () => await _service.CreateAsync(extra, Admin, default), "store_priority_limit_exceeded", 409);
        extra.Status = StoreStatus.Active;
        var active = await _service.CreateAsync(extra, Admin, default);
        extra.Status = StoreStatus.Priority; extra.Version = active.Version;
        Rejected(async () => await _service.UpdateAsync(active.Id, extra, Admin, default), "store_priority_limit_exceeded", 409);
        var edit = Request(StoreStatus.Priority); edit.DisplayOrder = first!.DisplayOrder; edit.Version = first.Version;
        var edited = await _service.UpdateAsync(first.Id, edit, Admin, default);
        Assert.That((await _service.GetLogoAsync(edited.Id, null, default)).Content, Is.EqualTo(Png));
        edit.Status = StoreStatus.Hidden; edit.Version = edited.Version;
        await _service.UpdateAsync(edited.Id, edit, Admin, default);
        await _service.UpdateAsync(active.Id, extra, Admin, default);
        Assert.That((await _service.ListPublicAsync("recommended", true, default)).Items, Has.Length.EqualTo(6));
    }

    [TestCase("store_display_order_conflict", "displayOrder")]
    [TestCase("store_priority_limit_exceeded", "status")]
    public void PlacementConflictsAreInline(string code, string field)
    {
        var problem = new SarafanProblemDetailsFactory().Create(new DefaultHttpContext(), 409, code);
        Assert.That(problem.Status, Is.EqualTo(409));
        Assert.That(problem.Errors![field], Is.EqualTo(new[] { problem.Detail }));
    }

    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class ConcurrencyFailure : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => Enabled ? throw new DbUpdateConcurrencyException() : ValueTask.FromResult(result);
    }
    private sealed class CaptureLogger : ILogger<StoreService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add($"{eventId.Name} {formatter(state, exception)}");
    }
}
