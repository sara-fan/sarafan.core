// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class BackofficeOrderTests
{
    private WebApplicationFactory<Program>? _app;
    private HttpClient? _client;

    [SetUp]
    public async Task SetUp()
    {
        await IntegrationTestEnvironment.ResetAsync();
        await DemoteDemoBackofficeUsersAsync();
        _app = CreateApp();
        _client = _app.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false
        });
        await SeedOrdersAsync(_app.Services);
    }

    [TearDown]
    public void TearDown()
    {
        _client?.Dispose();
        _app?.Dispose();
    }

    [Test]
    public async Task Endpoints_AuthorizeAllStaffRolesAndRejectCustomerAndAnonymousTokens()
    {
        foreach (var role in BackofficeRoles.Codes)
        {
            var token = await CreateStaffTokenAsync(_app!.Services, role);
            using var ops = await SendAsync("/api/v1/backoffice/orders/ops", token);
            using var list = await SendAsync("/api/v1/backoffice/orders", token);
            Assert.That(ops.StatusCode, Is.EqualTo(HttpStatusCode.OK), role);
            Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK), role);
            Assert.That(ops.Headers.CacheControl?.NoStore, Is.True, role);
            Assert.That(list.Headers.CacheControl?.NoStore, Is.True, role);
        }

        using var anonymous = await _client!.GetAsync("/api/v1/backoffice/orders");
        var customerToken = await CreateCustomerTokenAsync(_app!.Services);
        using var customer = await SendAsync("/api/v1/backoffice/orders", customerToken);
        Assert.That(anonymous.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(customer.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Operations_ReturnsCoreCataloguesAndExactAggregateGroups()
    {
        using var response = await SendAsAdministratorAsync("/api/v1/backoffice/orders/ops");
        var body = await response.Content.ReadFromJsonAsync<BackofficeOrderOpsDto>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Statuses.Select(item => item.Value), Is.EqualTo(Enum.GetValues<OrderStatus>().Select(item => (int)item)));
        Assert.That(body.Currencies.Select(item => item.Value), Is.EqualTo(new[] { 643, 840, 978 }));
        Assert.That(body.StatusGroups.Select(item => (item.RouteAlias, item.Name)), Is.EqualTo(new[]
        {
            ("work", "В работе"),
            ("in_progress", "Выполняется")
        }));
        Assert.That(body.StatusGroups[0].Statuses.Select(item => (int)item),
            Is.EqualTo(new[] { 0, 100, 200, 300, 310, 320, 330, 340, 360, 380 }));
        Assert.That(body.StatusGroups[1].Statuses.Select(item => (int)item),
            Is.EqualTo(new[] { 300, 310, 320, 330, 340, 360, 380 }));
        Assert.That(body.StatusGroups.SelectMany(item => item.Statuses),
            Does.Not.Contain(OrderStatus.Received).And.Not.Contain(OrderStatus.Cancelled));
    }

    [Test]
    public async Task List_PaginatesProjectsPublicFieldsAndUsesDeterministicCreatedSort()
    {
        using var response = await SendAsAdministratorAsync(
            "/api/v1/backoffice/orders?page=2&pageSize=5&sortBy=createdAt&sortOrder=desc");
        var body = await response.Content.ReadFromJsonAsync<BackofficeOrderPageDto>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body!.Items, Has.Length.EqualTo(5));
            Assert.That(body.Pagination.CurrentPage, Is.EqualTo(2));
            Assert.That(body.Pagination.PageSize, Is.EqualTo(5));
            Assert.That(body.Pagination.TotalCount, Is.EqualTo(14));
            Assert.That(body.Pagination.TotalPages, Is.EqualTo(3));
            Assert.That(body.Pagination.HasNextPage, Is.True);
            Assert.That(body.Pagination.HasPreviousPage, Is.True);
            Assert.That(body.Sorting.SortBy, Is.EqualTo("createdAt"));
            Assert.That(body.Sorting.SortOrder, Is.EqualTo("desc"));
            Assert.That(body.Items, Is.Ordered.Descending.By(nameof(BackofficeOrderListItemDto.CreatedAt)));
            Assert.That(body.Items, Has.All.Property(nameof(BackofficeOrderListItemDto.OrderNumber)).Matches("^[0-9]{8}-[1-9][0-9]*$"));
        }

        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Not.Contain("customerId").And.Not.Contain("creationIdempotencyKey"));
    }

    [TestCase("11111111-1", "11111111-1")]
    [TestCase("blue widget", "11111111-2")]
    [TestCase("rare shop", "11111111-3")]
    [TestCase("source-token", "11111111-4")]
    public async Task List_SearchesEveryAllowlistedFieldCaseInsensitively(string search, string expected)
    {
        using var response = await SendAsAdministratorAsync(
            $"/api/v1/backoffice/orders?search={Uri.EscapeDataString(search.ToUpperInvariant())}&sortBy=orderNumber&sortOrder=asc");
        var body = await response.Content.ReadFromJsonAsync<BackofficeOrderPageDto>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body!.Items.Select(item => item.OrderNumber), Does.Contain(expected));
        Assert.That(body.Search, Is.EqualTo(search.ToUpperInvariant()));
    }

    [Test]
    public async Task List_AppliesExactAggregateDateAndCombinedFilters()
    {
        var cases = new[]
        {
            (Query:"status=300", Count:1, Allowed:new[] { 300 }),
            (Query:"statusGroup=work", Count:10, Allowed:new[] { 0, 100, 200, 300, 310, 320, 330, 340, 360, 380 }),
            (Query:"statusGroup=in_progress", Count:7, Allowed:new[] { 300, 310, 320, 330, 340, 360, 380 }),
            (Query:"createdFrom=2026-09-13&createdTo=2026-09-13", Count:12, Allowed:Enum.GetValues<OrderStatus>().Select(item => (int)item).ToArray()),
            (Query:"statusGroup=in_progress&createdFrom=2026-09-13&createdTo=2026-09-13&search=product", Count:7, Allowed:new[] { 300, 310, 320, 330, 340, 360, 380 })
        };
        foreach (var item in cases)
        {
            using var response = await SendAsAdministratorAsync(
                $"/api/v1/backoffice/orders?pageSize=100&sortBy=status&sortOrder=asc&{item.Query}");
            var body = await response.Content.ReadFromJsonAsync<BackofficeOrderPageDto>();
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), item.Query);
            Assert.That(body!.Pagination.TotalCount, Is.EqualTo(item.Count), item.Query);
            Assert.That(body.Items.Select(row => (int)row.Status), Is.All.InRange(item.Allowed.Min(), item.Allowed.Max()), item.Query);
            Assert.That(body.Items.Select(row => (int)row.Status).Except(item.Allowed), Is.Empty, item.Query);
        }
    }

    [Test]
    public async Task List_SupportsEverySortAndKeepsNullableValuesLast()
    {
        foreach (var sort in new[]
                 {
                     "orderNumber", "status", "productName", "storeName",
                     "sellerPrice", "quantity", "createdAt", "updatedAt"
                 })
        {
            foreach (var order in new[] { "asc", "desc" })
            {
                using var response = await SendAsAdministratorAsync(
                    $"/api/v1/backoffice/orders?pageSize=100&sortBy={sort}&sortOrder={order}");
                var body = await response.Content.ReadFromJsonAsync<BackofficeOrderPageDto>();
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"{sort} {order}");
                Assert.That(body!.Items, Has.Length.EqualTo(14), $"{sort} {order}");
                Assert.That(body.Sorting.SortBy, Is.EqualTo(sort));
                Assert.That(body.Sorting.SortOrder, Is.EqualTo(order));
                if (sort == "productName") Assert.That(body.Items[^1].ProductName, Is.Null, order);
                if (sort == "storeName") Assert.That(body.Items[^1].StoreName, Is.Null, order);
                if (sort == "sellerPrice") Assert.That(body.Items[^1].SellerPrice, Is.Null, order);
            }
        }
    }

    [TestCase("createdat", "createdAt")]
    [TestCase("CREATEDAT", "createdAt")]
    [TestCase(" orderNUMBER ", "orderNumber")]
    public async Task List_NormalizesSortKeysAndReturnsTheirCanonicalNames(string requested, string expected)
    {
        using var response = await SendAsAdministratorAsync(
            $"/api/v1/backoffice/orders?sortBy={Uri.EscapeDataString(requested)}&sortOrder=ASC");
        var body = await response.Content.ReadFromJsonAsync<BackofficeOrderPageDto>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body!.Sorting.SortBy, Is.EqualTo(expected));
        Assert.That(body.Sorting.SortOrder, Is.EqualTo("asc"));
    }

    [TestCase("page=0")]
    [TestCase("page=abc")]
    [TestCase("page=")]
    [TestCase("page=1&page=2")]
    [TestCase("pageSize=101")]
    [TestCase("pageSize=abc")]
    [TestCase("pageSize=")]
    [TestCase("sortBy=id")]
    [TestCase("sortOrder=sideways")]
    [TestCase("sortBy=orderNumber&sortBy=createdAt")]
    [TestCase("sortOrder=asc&sortOrder=desc")]
    [TestCase("status=999")]
    [TestCase("statusGroup=unknown")]
    [TestCase("statusGroup=")]
    [TestCase("statusGroup=%20")]
    [TestCase("status=300&statusGroup=work")]
    [TestCase("createdFrom=2026-09-14&createdTo=2026-09-13")]
    [TestCase("createdFrom=0001-01-01")]
    [TestCase("createdFrom=not-a-date")]
    [TestCase("createdTo=2026-02-30")]
    public async Task List_RejectsInvalidFiltersWithStableProblem(string query)
    {
        using var response = await SendAsAdministratorAsync($"/api/v1/backoffice/orders?{query}");
        var problem = await response.Content.ReadFromJsonAsync<SarafanProblemDetails>();
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), query);
        Assert.That(problem!.Code, Is.EqualTo("invalid_order_list_filter"), query);
    }

    [Test]
    public async Task List_RejectsOversizedSearchAndAllowsEmptyAndOutOfRangePages()
    {
        using var invalid = await SendAsAdministratorAsync(
            $"/api/v1/backoffice/orders?search={new string('x', 2049)}");
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using var empty = await SendAsAdministratorAsync("/api/v1/backoffice/orders?search=not-found");
        var emptyBody = await empty.Content.ReadFromJsonAsync<BackofficeOrderPageDto>();
        Assert.That(emptyBody!.Items, Is.Empty);
        Assert.That(emptyBody.Pagination.TotalCount, Is.Zero);
        Assert.That(emptyBody.Pagination.TotalPages, Is.Zero);

        using var outOfRange = await SendAsAdministratorAsync("/api/v1/backoffice/orders?page=99&pageSize=10");
        var outOfRangeBody = await outOfRange.Content.ReadFromJsonAsync<BackofficeOrderPageDto>();
        Assert.That(outOfRangeBody!.Items, Is.Empty);
        Assert.That(outOfRangeBody.Pagination.CurrentPage, Is.EqualTo(99));
        Assert.That(outOfRangeBody.Pagination.TotalCount, Is.EqualTo(14));
    }

    [Test]
    public async Task DemoPhoneSuffixAuthorization_DoesNotHideOrderRoutesOrListValidation()
    {
        Assert.That(_app!.Services.GetRequiredService<IVerificationCodeProvider>(),
            Is.TypeOf<PhoneSuffixVerificationCodeProvider>());
        using var ops = await SendAsAdministratorAsync("/api/v1/backoffice/orders/ops");
        using var invalid = await SendAsAdministratorAsync(
            "/api/v1/backoffice/orders?page=abc&pageSize=&statusGroup=unknown");
        var problem = await invalid.Content.ReadFromJsonAsync<SarafanProblemDetails>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ops.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(problem!.Code, Is.EqualTo("invalid_order_list_filter"));
        }
    }

    private static WebApplicationFactory<Program> CreateApp()
        => IntegrationTestEnvironment.Factory.WithWebHostBuilder(_ => { });

    private static async Task DemoteDemoBackofficeUsersAsync()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var user in await database.BackofficeUsers.Where(item => item.IsDemo).ToArrayAsync())
        {
            user.IsDemo = false;
        }
        await database.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> SendAsAdministratorAsync(string path)
        => await SendAsync(path, await AdministratorTokenAsync(_app!.Services));

    private async Task<HttpResponseMessage> SendAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client!.SendAsync(request);
    }

    private static async Task<string> AdministratorTokenAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<AppDbContext>().BackofficeUsers
            .Include(item => item.UserRoles)
            .SingleAsync(item => item.NormalizedEmail == IntegrationTestEnvironment.BackofficeEmail);
        return scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(user).Token;
    }

    private static async Task<string> CreateStaffTokenAsync(IServiceProvider services, string role)
    {
        if (role == BackofficeRoles.Administrator)
        {
            return await AdministratorTokenAsync(services);
        }

        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var user = new BackofficeUser
        {
            Email = $"{role}@sarafan.test",
            NormalizedEmail = $"{role}@sarafan.test",
            FirstName = role,
            LastName = "Orders",
            PasswordHash = "not-used",
            CreatedAt = now,
            UpdatedAt = now,
            UserRoles = [new BackofficeUserRole { RoleCode = role }]
        };
        database.BackofficeUsers.Add(user);
        await database.SaveChangesAsync();
        return scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(user).Token;
    }

    private static async Task<string> CreateCustomerTokenAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var customer = new Customer { Phone = "+79990009999", Profile = new(), CreatedAt = now, UpdatedAt = now };
        database.Customers.Add(customer);
        await database.SaveChangesAsync();
        return scope.ServiceProvider.GetRequiredService<JwtTokenService>().CreateAccessToken(customer).Token;
    }

    private static async Task SeedOrdersAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customers = new[]
        {
            new Customer { Phone = "+79990000001", Profile = new() },
            new Customer { Phone = "+79990000002", Profile = new() }
        };
        database.Customers.AddRange(customers);
        await database.SaveChangesAsync();
        var statuses = Enum.GetValues<OrderStatus>();
        for (var index = 0; index < statuses.Length; index++)
        {
            var customer = index < statuses.Length - 1 ? customers[0] : customers[1];
            var number = customer.AllocateOrderNumber(customer.OrderCode ?? (customer == customers[0] ? "11111111" : "22222222"));
            var created = new DateTimeOffset(2026, 9, 12, 21, index, 0, TimeSpan.Zero);
            var order = new Order(
                customer.Id,
                number,
                index == 3 ? "https://shop.example.com/source-token" : $"https://shop.example.com/item-{index}",
                index + 1,
                null,
                Guid.NewGuid(),
                created);
            order.SetProductSnapshot(
                index == 1 ? "Blue Widget" : index == statuses.Length - 1 ? null : $"Product {index}",
                index == 2 ? "Rare Shop" : index == statuses.Length - 1 ? null : $"Store {index}",
                null,
                index == statuses.Length - 1 ? null : 10m + index,
                index == statuses.Length - 1 ? null : index % 2 == 0 ? Currency.Rub : Currency.Usd,
                null,
                null,
                null,
                null,
                null,
                created.AddMinutes(index));
            database.Orders.Add(order);
            database.Entry(order).Property(item => item.Status).CurrentValue = statuses[index];
        }

        var beforeMoscowDate = new Order(
            customers[1].Id,
            customers[1].AllocateOrderNumber("22222222"),
            "https://shop.example.com/before-date",
            1,
            null,
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 12, 20, 59, 59, TimeSpan.Zero));
        var afterMoscowDate = new Order(
            customers[1].Id,
            customers[1].AllocateOrderNumber("22222222"),
            "https://shop.example.com/after-date",
            1,
            null,
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 13, 21, 0, 0, TimeSpan.Zero));
        database.Orders.AddRange(beforeMoscowDate, afterMoscowDate);
        database.Entry(beforeMoscowDate).Property(item => item.Status).CurrentValue = OrderStatus.Received;
        database.Entry(afterMoscowDate).Property(item => item.Status).CurrentValue = OrderStatus.Cancelled;
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
    }

}
