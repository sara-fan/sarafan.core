// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[NonParallelizable]
public sealed class OrderProductApiTests
{
    private HttpClient _customer = null!;
    private HttpClient _staff = null!;
    private AuthenticationSessionDto _session = null!;

    [SetUp]
    public async Task Setup()
    {
        await IntegrationTestEnvironment.ResetAsync();
        await OrderProductTestData.SeedRates();
        _customer = IntegrationTestEnvironment.Factory.CreateClient();
        _staff = IntegrationTestEnvironment.Factory.CreateClient();
        const string phone = "+79993332211";
        var receipt = await ConsentTestData.Onboarding(_customer, phone);
        using var verified = await _customer.PostAsJsonAsync("/api/v1/auth/code/verify", new { phone, code = "2211", onboardingToken = receipt });
        verified.EnsureSuccessStatusCode();
        _session = (await verified.Content.ReadFromJsonAsync<AuthenticationSessionDto>())!;
        _customer.DefaultRequestHeaders.Authorization = new("Bearer", _session.AccessToken);
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.BackofficeUsers.Include(user => user.UserRoles).SingleAsync();
        _staff.DefaultRequestHeaders.Authorization = new("Bearer", scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(user).Token);
    }

    [TearDown]
    public void Cleanup() { _customer.Dispose(); _staff.Dispose(); }

    [Test]
    public async Task HistoryEndpointsGroupActionsAndProtectEvidence()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.CustomerProfiles.SingleAsync();
        profile.LastName = "Исторический"; profile.FirstName = "Покупатель";
        await db.SaveChangesAsync();
        var key = Guid.NewGuid();
        using var created = await Create(key);
        created.EnsureSuccessStatusCode();
        var original = (await created.Content.ReadFromJsonAsync<OrderDto>())!;
        var path = $"/api/v1/backoffice/orders/{original.OrderNumber}/history";
        var ops = await _staff.GetFromJsonAsync<OrderHistoryOpsDto>(path + "/ops");
        Assert.That(ops!.Areas, Has.Length.EqualTo(4));
        var page = await _staff.GetFromJsonAsync<OrderHistoryPageDto>(path);
        Assert.That(page!.Items, Has.Length.EqualTo(1));
        Assert.That(page.Items[0].Kind, Is.EqualTo(OrderHistoryKind.Created));
        Assert.That(page.Items[0].ActorName, Is.EqualTo("Исторический Покупатель"));
        profile.LastName = "Новое имя"; await db.SaveChangesAsync();
        var creation = await _staff.GetFromJsonAsync<OrderHistoryDetailDto>(path + "/" + page.Items[0].EventKey);
        Assert.That(creation!.Event.ActorName, Is.EqualTo("Исторический Покупатель"));
        Assert.That(creation.ProductAfter, Is.EqualTo(original.Product));
        Assert.That(creation.SourceUrl, Is.EqualTo(original.SourceUrl));
        using var replay = await Create(key);
        replay.EnsureSuccessStatusCode();
        var current = await Details(original.OrderNumber);
        var request = Update(current, "Исправлено", 20, 2);
        using var changed = await _staff.PutAsJsonAsync($"/api/v1/backoffice/orders/{original.OrderNumber}/product", request);
        changed.EnsureSuccessStatusCode();
        page = await _staff.GetFromJsonAsync<OrderHistoryPageDto>(path + "?area=4&actorType=100&sortBy=actor&sortOrder=asc");
        Assert.That(page!.Items, Has.Length.EqualTo(1));
        Assert.That(page.Items[0].Areas, Is.EqualTo(OrderHistoryArea.Product | OrderHistoryArea.Pricing));
        using var eventResponse = await _staff.GetAsync(path + "/" + page.Items[0].EventKey);
        eventResponse.EnsureSuccessStatusCode();
        Assert.That(eventResponse.Headers.CacheControl!.NoStore, Is.True);
        var change = (await eventResponse.Content.ReadFromJsonAsync<OrderHistoryDetailDto>())!;
        Assert.That(change.ProductBefore, Is.EqualTo(original.Product));
        Assert.That(change.ProductAfter!.ProductName, Is.EqualTo("Исправлено"));
        Assert.That(change.PricingBefore, Is.Not.Null);
        Assert.That(change.PricingAfter, Is.Not.Null);
        using var conflict = await _staff.PutAsJsonAsync($"/api/v1/backoffice/orders/{original.OrderNumber}/product", request);
        await Problem(conflict, HttpStatusCode.Conflict, "order_update_conflict");
        Assert.That((await _staff.GetFromJsonAsync<OrderHistoryPageDto>(path))!.Pagination.TotalCount, Is.EqualTo(2));
        using var customerDenied = await _customer.GetAsync(path);
        Assert.That(customerDenied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        using var invalid = await _staff.GetAsync(path + "?page=1&page=2");
        await Problem(invalid, HttpStatusCode.BadRequest, "invalid_order_list_filter");
        using var malformed = await _staff.GetAsync(path + "?from=bad");
        await Problem(malformed, HttpStatusCode.BadRequest, "invalid_order_list_filter");
        using var otherCreated = await Create(Guid.NewGuid());
        var other = (await otherCreated.Content.ReadFromJsonAsync<OrderDto>())!;
        using var foreign = await _staff.GetAsync($"/api/v1/backoffice/orders/{other.OrderNumber}/history/{page.Items[0].EventKey}");
        await Problem(foreign, HttpStatusCode.NotFound, "resource_not_found");
    }

    [Test]
    public async Task CreationCorrectionAndReplayUseCurrentProductAndPreserveAuditHistory()
    {
        var key = Guid.NewGuid();
        using var response = await Create(key);
        response.EnsureSuccessStatusCode();
        var original = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.That(original.Product.ProductName, Is.EqualTo("Тестовый товар"));
        Assert.That(original.ShowReviewFields, Is.True);
        var details = await Details(original.OrderNumber);
        Assert.That(details.CanEditProduct, Is.True);
        Assert.That(details.Customer.Phone, Is.EqualTo("+79993332211"));
        Assert.That(details.Customer.PassportNumber, Is.Null);
        var request = Update(details, " Исправленный товар ", 281.25m, 4);
        request.Color = " Cherry Blossom "; request.Size = " L "; request.Comment = "  Проверено  ";
        using var changed = await _staff.PutAsJsonAsync($"/api/v1/backoffice/orders/{original.OrderNumber}/product", request);
        changed.EnsureSuccessStatusCode();
        Assert.That(changed.Headers.CacheControl!.NoStore, Is.True);
        var corrected = (await changed.Content.ReadFromJsonAsync<BackofficeOrderDetailsDto>())!;
        Assert.That(corrected.Product, Is.EqualTo(new OrderProductDto("Исправленный товар", new(281.25m, Currency.Usd), 4, "Cherry Blossom", "L", "Проверено")));
        Assert.That(corrected.Status, Is.EqualTo(OrderStatus.UnderReview));
        Assert.That(corrected.UpdatedAt, Is.GreaterThan(details.UpdatedAt));
        Assert.That(corrected.SourceUrl, Is.EqualTo(original.SourceUrl));
        var customerRead = await _customer.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{original.OrderNumber}");
        Assert.That(customerRead!.Product, Is.EqualTo(corrected.Product));
        Assert.That(customerRead.ProductName, Is.EqualTo(corrected.Product.ProductName));
        Assert.That(customerRead.Quantity, Is.EqualTo(4));
        var customerList = await _customer.GetFromJsonAsync<CustomerOrderListItemDto[]>("/api/v1/orders");
        Assert.That(customerList!.Single().SellerPrice, Is.EqualTo(corrected.Product.SellerPrice));
        var staffList = await _staff.GetFromJsonAsync<BackofficeOrderPageDto>("/api/v1/backoffice/orders?search=Исправленный&sortBy=sellerPrice");
        Assert.That(staffList!.Items.Single().Quantity, Is.EqualTo(4));
        var oldName = await _staff.GetFromJsonAsync<BackofficeOrderPageDto>("/api/v1/backoffice/orders?search=Тестовый");
        Assert.That(oldName!.Items, Is.Empty);
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.CustomerProfiles.SingleAsync();
            profile.LastName = "Иванов"; profile.FirstName = "Иван"; profile.Patronymic = "Иванович";
            profile.Email = "test@example.com"; profile.PassportSeries = "1234"; profile.PassportNumber = "123456";
            profile.PassportIssueDate = new(2020, 1, 1); profile.PassportIssuedBy = "Тестовый орган";
            profile.Inn = "123456789012"; profile.PostalCode = "123456"; profile.City = "Москва"; profile.Address = "Тестовая улица";
            db.ExchangeRateHistory.AddRange(OrderProductTestData.Rate(Currency.Usd, 20000, date: new(2026, 9, 2)),
                OrderProductTestData.Rate(Currency.Eur, 100, date: new(2026, 9, 2)));
            await db.SaveChangesAsync();
        }
        var latest = await Details(original.OrderNumber);
        Assert.That(latest.Customer, Is.EqualTo(new BackofficeOrderCustomerDto("Иванов", "Иван", "Иванович",
            "+79993332211", "test@example.com", "1234", "123456", new(2020, 1, 1), "Тестовый орган",
            "123456789012", "123456", "Москва", "Тестовая улица")));
        Assert.That(latest.LimitCheck.MaximumTotalUsd, Is.EqualTo(4.5m));
        Assert.That(latest.SavedLimitSourceEffectiveDate, Is.EqualTo(new DateOnly(2026, 9, 1)));
        Assert.That(latest.LimitCheck.SourceEffectiveDate, Is.EqualTo(new DateOnly(2026, 9, 2)));
        using var changedRatesReplay = await Create(key);
        changedRatesReplay.EnsureSuccessStatusCode();
        using var newAtChangedRate = await Create(Guid.NewGuid());
        await Problem(newAtChangedRate, HttpStatusCode.BadRequest, "order_value_limit_exceeded");
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audits = await db.Set<OrderProductAuditEvent>().OrderBy(item => item.Kind).ToListAsync();
            Assert.That(audits, Has.Count.EqualTo(2));
            var createdAudit = audits.Single(item => item.Kind == OrderProductAuditKind.Created);
            var audit = audits.Single(item => item.Kind == OrderProductAuditKind.StaffCorrected);
            Assert.That(createdAudit.Id, Is.Positive);
            Assert.That(createdAudit.Before, Is.Null);
            Assert.That(createdAudit.ActorId, Is.Null);
            using var createdAfter = JsonDocument.Parse(createdAudit.After);
            Assert.That(createdAfter.RootElement.Deserialize<OrderProductDto>(), Is.EqualTo(original.Product));
            using var before = JsonDocument.Parse(audit.Before!);
            using var after = JsonDocument.Parse(audit.After);
            Assert.That(before.RootElement.Deserialize<OrderProductDto>(), Is.EqualTo(original.Product));
            Assert.That(after.RootElement.Deserialize<OrderProductDto>(), Is.EqualTo(corrected.Product));
            Assert.That(before.RootElement.GetProperty("StoreName").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(after.RootElement.GetProperty("StoreName").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(audit.ActorId, Is.EqualTo((await db.BackofficeUsers.SingleAsync()).Id));
            var stored = await db.Orders.SingleAsync();
            Assert.That(stored.ProductName, Is.EqualTo("Исправленный товар"));
            Assert.That(stored.Quantity, Is.EqualTo(4));
            Assert.That(createdAudit.UsdRateId, Is.Not.Null);
            Assert.That(audit.EurRateId, Is.Not.Null);
            // InMemory simulates unavailable catalogues without exercising provider FK behavior.
            db.ChangeTracker.Clear();
            db.ExchangeRateHistory.RemoveRange(db.ExchangeRateHistory);
            db.IanaTldCatalog.RemoveRange(db.IanaTldCatalog);
            await db.SaveChangesAsync();
        }
        using var replay = await Create(key);
        replay.EnsureSuccessStatusCode();
        var replayed = (await replay.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.That(replayed.Product, Is.EqualTo(corrected.Product));
        using var conflict = await Create(key, name: "Другой товар");
        await Problem(conflict, HttpStatusCode.Conflict, "order_creation_conflict");
        Assert.That((await Details(original.OrderNumber)).LimitCheck.Available, Is.False);
    }

    [Test]
    public async Task StaffDetailsExposeMetadataAllowStoreCorrectionAndKeepSharedLimitMessage()
    {
        using var created = await Create(Guid.NewGuid(), store: "Магазин");
        var order = (await created.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.That((await Details(order.OrderNumber)).Dimensions, Is.Null);
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var recognizedOrder = await db.Orders.SingleAsync();
            recognizedOrder.SetProductMetadata("https://shop.example/image.png",
                1m, 2m, 3m, new Dictionary<string, string> { ["Материал"] = "Сталь" }, null, recognizedOrder.UpdatedAt);
            await db.SaveChangesAsync();
        }
        var details = await Details(order.OrderNumber);
        Assert.That(details.Product.StoreName, Is.EqualTo("Магазин"));
        Assert.That(details.SavedLimitSourceEffectiveDate, Is.EqualTo(details.LimitCheck.SourceEffectiveDate));
        Assert.That(details.ImageUrl, Is.EqualTo("https://shop.example/image.png"));
        Assert.That(details.Dimensions, Is.EqualTo(new OrderDimensionsDto(1, 2, 3)));
        Assert.That(details.Characteristics!["Материал"], Is.EqualTo("Сталь"));
        Assert.That(details.LimitCheck.ExceededMessage, Is.EqualTo(OrderLimitService.ExceededMessage));
        var update = Update(details, "Исправлено", 20, 1);
        update.StoreName = " Новый магазин ";
        using var saved = await Put(order.OrderNumber, update);
        var corrected = (await saved.Content.ReadFromJsonAsync<BackofficeOrderDetailsDto>())!;
        Assert.That(corrected.Characteristics, Is.EqualTo(details.Characteristics));
        Assert.That(corrected.Dimensions, Is.EqualTo(details.Dimensions));
        Assert.That(corrected.Product.StoreName, Is.EqualTo("Новый магазин"));
        Assert.That(corrected.ImageUrl, Is.EqualTo(details.ImageUrl));
        Assert.That(corrected.SavedLimitSourceEffectiveDate, Is.EqualTo(details.SavedLimitSourceEffectiveDate));
        await using var verification = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var verifiedOrder = await verification.ServiceProvider.GetRequiredService<AppDbContext>().Orders.SingleAsync();
        Assert.That(verifiedOrder.StoreName, Is.EqualTo("Новый магазин"));
        var audit = await verification.ServiceProvider.GetRequiredService<AppDbContext>().Set<OrderProductAuditEvent>()
            .SingleAsync(item => item.Kind == OrderProductAuditKind.StaffCorrected);
        using var before = JsonDocument.Parse(audit.Before!);
        using var after = JsonDocument.Parse(audit.After);
        Assert.That(before.RootElement.GetProperty("StoreName").GetString(), Is.EqualTo("Магазин"));
        Assert.That(after.RootElement.GetProperty("StoreName").GetString(), Is.EqualTo("Новый магазин"));
        var customerRead = await _customer.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{order.OrderNumber}");
        Assert.That(customerRead!.StoreName, Is.EqualTo("Новый магазин"));
        var staffList = await _staff.GetFromJsonAsync<BackofficeOrderPageDto>("/api/v1/backoffice/orders?search=Новый%20магазин&sortBy=storeName");
        Assert.That(staffList!.Items.Single().StoreName, Is.EqualTo("Новый магазин"));
    }

    [Test]
    public async Task ReserveLimitAppliesToCreationCorrectionAndMetadata()
    {
        using var response = await Create(Guid.NewGuid(), price: 281.25m, quantity: 4);
        response.EnsureSuccessStatusCode();
        var order = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        var details = await Details(order.OrderNumber);
        var ops = await _customer.GetFromJsonAsync<OrderOpsDto>("/api/v1/orders/ops");
        var staffOps = await _staff.GetFromJsonAsync<BackofficeOrderOpsDto>("/api/v1/backoffice/orders/ops");
        Assert.That(ops!.ProductLimits, Is.EqualTo(staffOps!.ProductLimits));
        Assert.That(details.LimitCheck, Is.EqualTo(ops.ProductLimits!.ValueLimit));
        Assert.That(details.LimitCheck.MaximumAmount, Is.EqualTo(900m));
        Assert.That(details.LimitCheck.MaximumTotalUsd, Is.EqualTo(1125m));
        Assert.That(ops.ProductLimits.StoreNameMaximumLength, Is.EqualTo(200));

        using var rejected = await Put(order.OrderNumber, Update(details, "Выше лимита", 281.26m, 4));
        Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = (await rejected.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        Assert.That(problem.Code, Is.EqualTo("order_value_limit_exceeded"));
        Assert.That(problem.Errors!["sellerPrice"], Is.EqualTo(new[]
        {
            "Максимальная стоимость заказа при экспресс-перевозке 900 евро с учётом резерва 10% на изменение курса"
        }));
        Assert.That((await Details(order.OrderNumber)).Product, Is.EqualTo(details.Product));
    }

    [Test]
    public async Task QuantityMoneyAndUnavailableRatesRejectBeforeAllocatingIdentity()
    {
        using var missing = await Create(Guid.NewGuid(), includeProduct: false);
        await Problem(missing, HttpStatusCode.BadRequest, "invalid_order_product_name");
        using var quantity = await Create(Guid.NewGuid(), quantity: 5);
        await Problem(quantity, HttpStatusCode.BadRequest, "order_quantity_limit_exceeded");
        using var excess = await Create(Guid.NewGuid(), price: 281.26m, quantity: 4);
        await Problem(excess, HttpStatusCode.BadRequest, "order_value_limit_exceeded");
        foreach (var (raw, expectedMessage) in new[]
        {
            ("null", "Поле обязательно для заполнения."),
            ("0", "Количество должно быть положительным числом."),
            ("-1", "Количество должно быть положительным числом."),
            ("1.5", "Количество должно быть целым числом."),
            ("\"abc\"", "Количество должно быть целым числом.")
        })
        {
            using var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
            {
                Content = new StringContent("{\"sourceUrl\":\"shop.example.com\",\"quantity\":" + raw + "}", Encoding.UTF8, "application/json")
            };
            invalid.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            using var result = await _customer.SendAsync(invalid);
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(result.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/problem+json"));
            var problem = (await result.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
            Assert.That(problem.Code, Is.EqualTo("validation_failed"), raw);
            Assert.That(problem.Errors, Does.ContainKey("quantity"), raw);
            Assert.That(problem.Errors!["quantity"], Is.EqualTo(new[] { expectedMessage }), raw);
            Assert.That(problem.Errors, Does.Not.ContainKey("$.quantity"), raw);
        }
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = await db.Customers.SingleAsync();
            Assert.That(customer.NextOrderNumber, Is.EqualTo(1));
            Assert.That(customer.OrderCode, Is.Null);
            Assert.That(await db.Orders.CountAsync(), Is.Zero);
            db.ExchangeRateHistory.RemoveRange(db.ExchangeRateHistory.Where(rate => rate.BaseCurrency == Currency.Eur));
            await db.SaveChangesAsync();
        }
        using var unavailable = await Create(Guid.NewGuid());
        await Problem(unavailable, HttpStatusCode.ServiceUnavailable, "order_limit_rates_unavailable");
        var ops = await _customer.GetFromJsonAsync<OrderOpsDto>("/api/v1/orders/ops");
        var staffOps = await _staff.GetFromJsonAsync<BackofficeOrderOpsDto>("/api/v1/backoffice/orders/ops");
        Assert.That(ops!.ProductLimits, Is.EqualTo(staffOps!.ProductLimits));
        Assert.That(ops.ProductLimits!.ValueLimit.Available, Is.False);
        Assert.That(ops.ProductLimits.ValueLimit.MaximumAmount, Is.EqualTo(900m));
        Assert.That(await _customer.GetFromJsonAsync<CustomerOrderListItemDto[]>("/api/v1/orders"), Is.Empty);
    }

    [Test]
    public async Task EditRejectsStaleTimestampStatusAndInvalidProductWithoutAudit()
    {
        using var created = await Create(Guid.NewGuid());
        var order = (await created.Content.ReadFromJsonAsync<OrderDto>())!;
        var details = await Details(order.OrderNumber);
        var request = Update(details, "Новое имя", 10, 1);
        request.ExpectedUpdatedAt = details.UpdatedAt.AddTicks(1);
        using var stale = await Put(order.OrderNumber, request);
        await Problem(stale, HttpStatusCode.Conflict, "order_update_conflict");
        request.ExpectedUpdatedAt = null;
        using var absent = await Put(order.OrderNumber, request);
        await Problem(absent, HttpStatusCode.Conflict, "order_update_conflict");
        request.ExpectedUpdatedAt = details.UpdatedAt; request.Quantity = 5;
        using var invalid = await Put(order.OrderNumber, request);
        await Problem(invalid, HttpStatusCode.BadRequest, "order_quantity_limit_exceeded");
        request.Quantity = 1; request.StoreName = new string('я', 201);
        using var invalidStore = await Put(order.OrderNumber, request);
        await Problem(invalidStore, HttpStatusCode.BadRequest, "invalid_order_store_name");
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Orders.SingleAsync();
            db.Entry(stored).Property(item => item.Status).CurrentValue = OrderStatus.Cancelled;
            await db.SaveChangesAsync();
            Assert.That(await db.Set<OrderProductAuditEvent>()
                .CountAsync(item => item.Kind == OrderProductAuditKind.StaffCorrected), Is.Zero);
        }
        using var closed = await Put(order.OrderNumber, request);
        await Problem(closed, HttpStatusCode.Conflict, "order_not_editable");
        Assert.That((await Details(order.OrderNumber)).CanEditProduct, Is.False);
        Assert.That((await _customer.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{order.OrderNumber}"))!.ShowReviewFields, Is.False);
        foreach (var number in new[] { "bad", "12345678-0", "12345678-01", "abcdefgh-1", "12345678-99999999999999999999", "12345678-100" })
        {
            using var missing = await _staff.GetAsync($"/api/v1/backoffice/orders/{number}");
            await Problem(missing, HttpStatusCode.NotFound, "resource_not_found");
        }
    }

    [Test]
    public async Task EveryAuthorizedRoleCanEditAndCustomerCannotUseStaffApi()
    {
        using var created = await Create(Guid.NewGuid());
        var order = (await created.Content.ReadFromJsonAsync<OrderDto>())!;
        using var unauthorized = await _customer.GetAsync($"/api/v1/backoffice/orders/{order.OrderNumber}");
        Assert.That(unauthorized.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        foreach (var role in BackofficeRoles.Codes)
        {
            await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = new BackofficeUser
            {
                Email = role + "@test.local",
                NormalizedEmail = role + "@test.local",
                FirstName = "Тест",
                LastName = "Тест",
                PasswordHash = "unused",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                UserRoles = [new() { RoleCode = role }]
            };
            db.BackofficeUsers.Add(user); await db.SaveChangesAsync();
            _staff.DefaultRequestHeaders.Authorization = new("Bearer", scope.ServiceProvider.GetRequiredService<BackofficeJwtTokenService>().CreateAccessToken(user).Token);
            var details = await Details(order.OrderNumber);
            using var saved = await Put(order.OrderNumber, Update(details, role, 1, 1));
            saved.EnsureSuccessStatusCode();
        }
        await using var finalScope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var service = finalScope.ServiceProvider.GetRequiredService<OrderService>();
        Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.GetForBackofficeAsync(order.OrderNumber, ["unknown"], default))!.Code, Is.EqualTo("access_denied"));
        Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.UpdateProductAsync(order.OrderNumber, new(), 1, [], default))!.Code, Is.EqualTo("access_denied"));
    }

    [Test]
    public async Task LegacyReplayWithQuantityAboveFourDoesNotRequireRatesOrNewFields()
    {
        var key = Guid.NewGuid();
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = await db.Customers.SingleAsync();
            db.Orders.Add(new Order(customer.Id, customer.AllocateOrderNumber("01234567"), "https://shop.example.com/", 9, null, key, DateTimeOffset.UtcNow));
            db.ExchangeRateHistory.RemoveRange(db.ExchangeRateHistory);
            await db.SaveChangesAsync();
        }
        using var replay = await Create(key, quantity: 9, includeProduct: false);
        replay.EnsureSuccessStatusCode();
        var order = (await replay.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.That(order.Quantity, Is.EqualTo(9));
        var details = await Details(order.OrderNumber);
        using var cannotSave = await Put(order.OrderNumber, Update(details, "Товар", 10, 9));
        await Problem(cannotSave, HttpStatusCode.BadRequest, "order_quantity_limit_exceeded");
    }

    [Test]
    public async Task ApplicationMapsConcurrencyFailureAndDoesNotPersistCorrectionAudit()
    {
        using var created = await Create(Guid.NewGuid());
        var order = (await created.Content.ReadFromJsonAsync<OrderDto>())!;
        var details = await Details(order.OrderNumber);
        using var app = IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new RejectCorrection()))));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<OrderService>();
            var failure = Assert.ThrowsAsync<ServiceException>(() => service.UpdateProductAsync(order.OrderNumber,
                Update(details, "Конкурирующая правка", 10, 1), 1, [BackofficeRoles.Administrator], default));
            Assert.That(failure!.Code, Is.EqualTo("order_update_conflict"));
        }
        Assert.That((await Details(order.OrderNumber)).Product, Is.EqualTo(details.Product));
        await using var verification = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        Assert.That(await verification.ServiceProvider.GetRequiredService<AppDbContext>().Set<OrderProductAuditEvent>()
            .CountAsync(item => item.Kind == OrderProductAuditKind.StaffCorrected), Is.Zero);
    }

    private sealed class RejectCorrection : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<OrderProductAuditEvent>().Any(entry =>
                    entry.State == EntityState.Added && entry.Entity.Kind == OrderProductAuditKind.StaffCorrected))
                throw new DbUpdateConcurrencyException();
            return ValueTask.FromResult(result);
        }
    }

    private Task<BackofficeOrderDetailsDto> Details(string number)
        => _staff.GetFromJsonAsync<BackofficeOrderDetailsDto>($"/api/v1/backoffice/orders/{number}")!;
    private Task<HttpResponseMessage> Put(string number, UpdateOrderProductRequest request)
        => _staff.PutAsJsonAsync($"/api/v1/backoffice/orders/{number}/product", request);
    private static UpdateOrderProductRequest Update(BackofficeOrderDetailsDto details, string name, decimal price, int quantity)
        => new() { ExpectedUpdatedAt = details.UpdatedAt, StoreName = details.Product.StoreName, ProductName = name, SellerPrice = new(price, Currency.Usd), Quantity = quantity };
    private async Task<HttpResponseMessage> Create(Guid key, string name = "Тестовый товар", decimal price = 10,
        int quantity = 1, bool includeProduct = true, string? store = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest
            {
                SourceUrl = "https://shop.example.com/",
                Quantity = quantity,
                Product = includeProduct ? new() { ProductName = name, StoreName = store, SellerPrice = new(price, Currency.Usd) } : null
            })
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return await _customer.SendAsync(request);
    }
    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.That(response.StatusCode, Is.EqualTo(status));
        var body = (await response.Content.ReadFromJsonAsync<SarafanProblemDetails>())!;
        Assert.That(body.Code, Is.EqualTo(code));
        Assert.That(response.Content.Headers.ContentLanguage, Does.Contain("ru"));
    }
}
