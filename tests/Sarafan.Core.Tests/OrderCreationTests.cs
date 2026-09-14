// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class OrderCreationTests
{
    private static int _phoneSequence = 100;
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;
    private AuthenticationSessionDto _session = null!;

    [SetUp]
    public async Task SetUp()
    {
        await IntegrationTestEnvironment.ResetAsync();
        await DemoteDemoBackofficeUsers();
        _app = IsolatedApp();
        _client = CreateClient(_app);
        _session = await Register(_client);
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Test]
    public async Task Create_AllocatesNormalizedIdentityAndReplaysExactRequest()
    {
        Assert.That(_session.Customer.OrderCode, Is.Null);
        var firstKey = Guid.NewGuid();
        var beforeCreate = DateTimeOffset.UtcNow;
        using var firstResponse = await Create(
            _client,
            "  https://shop.example/product?id=1  ",
            firstKey,
            quantity: 2,
            comment: "  Упаковать бережно  ");
        var afterCreate = DateTimeOffset.UtcNow;
        var first = await firstResponse.Content.ReadFromJsonAsync<OrderDto>();
        using var replayResponse = await Create(
            _client,
            "https://shop.example/product?id=1",
            firstKey,
            quantity: 2,
            comment: "Упаковать бережно");
        var replay = await replayResponse.Content.ReadFromJsonAsync<OrderDto>();
        using var conflictResponse = await Create(_client, "https://shop.example/product?id=2", firstKey);
        var conflict = await conflictResponse.Content.ReadFromJsonAsync<SarafanProblemDetails>();
        using var quantityConflictResponse = await Create(
            _client,
            "https://shop.example/product?id=1",
            firstKey,
            quantity: 3,
            comment: "Упаковать бережно");
        using var commentConflictResponse = await Create(
            _client,
            "https://shop.example/product?id=1",
            firstKey,
            quantity: 2,
            comment: "Другой комментарий");
        using var secondResponse = await Create(_client, "https://shop.example/product?id=1", Guid.NewGuid());
        var second = await secondResponse.Content.ReadFromJsonAsync<OrderDto>();
        using var getByLocation = await _client.GetAsync(firstResponse.Headers.Location!);
        var getByLocationResponse = await getByLocation.Content.ReadFromJsonAsync<OrderDto>();
        var customer = await _client.GetFromJsonAsync<CustomerDto>("/api/v1/customers/me");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(firstResponse.Headers.Location, Is.Not.Null);
            Assert.That(first, Is.Not.Null);
            Assert.That(first!.Id, Is.Positive);
            Assert.That(first.OrderNumber, Does.Match("^[0-9]{8}-1$"));
            Assert.That(first.Status, Is.EqualTo(OrderStatus.UnderReview));
            Assert.That(first.SourceUrl, Is.EqualTo("https://shop.example/product?id=1"));
            Assert.That(first.Quantity, Is.EqualTo(2));
            Assert.That(first.Comment, Is.EqualTo("Упаковать бережно"));
            Assert.That(first.ProductName, Is.Null);
            Assert.That(first.StoreName, Is.Null);
            Assert.That(first.ImageUrl, Is.Null);
            Assert.That(first.SellerPrice, Is.Null);
            Assert.That(first.Dimensions, Is.Null);
            Assert.That(first.Characteristics, Is.Null);
            Assert.That(first.AppliedExchangeRate, Is.Null);
            Assert.That(getByLocation.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(getByLocationResponse, Is.EqualTo(first));
            Assert.That(replayResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(replay, Is.EqualTo(first));
            Assert.That(conflictResponse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(conflict?.Code, Is.EqualTo("order_creation_conflict"));
            Assert.That(quantityConflictResponse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(commentConflictResponse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(second!.OrderNumber, Is.EqualTo(first.OrderNumber[..^1] + "2"));
            Assert.That(second.Id, Is.Not.EqualTo(first.Id));
            Assert.That(customer?.OrderCode, Is.EqualTo(first.OrderNumber[..8]));
        }

        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedCustomer = await database.Customers.AsNoTracking().SingleAsync(item => item.Id == _session.Customer.Id);
        var storedFirstOrder = await database.Orders.AsNoTracking().SingleAsync(item => item.Id == first!.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedCustomer.NextOrderNumber, Is.EqualTo(3));
            Assert.That(await database.Orders.CountAsync(item => item.CustomerId == storedCustomer.Id), Is.EqualTo(2));
            Assert.That(storedFirstOrder.CreatedAt, Is.InRange(beforeCreate, afterCreate));
            Assert.That(storedFirstOrder.UpdatedAt, Is.EqualTo(storedFirstOrder.CreatedAt));
            Assert.That(storedFirstOrder.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
        }
    }

    [Test]
    public async Task List_IsOwnerScopedNewestFirstAndReturnsCardFields()
    {
        using var emptyResponse = await _client.GetAsync("/api/v1/orders");
        var emptyItems = await emptyResponse.Content.ReadFromJsonAsync<CustomerOrderListItemDto[]>();
        using var firstResponse = await Create(_client, "https://shop.example/first", Guid.NewGuid(), quantity: 2);
        var first = (await firstResponse.Content.ReadFromJsonAsync<OrderDto>())!;
        using var secondResponse = await Create(_client, "https://shop.example/second", Guid.NewGuid());
        var second = (await secondResponse.Content.ReadFromJsonAsync<OrderDto>())!;

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var firstOrder = await database.Orders.SingleAsync(item => item.Id == first.Id);
            firstOrder.SetProductSnapshot(
                "Первый товар",
                "Магазин",
                "https://images.example/first.jpg",
                12.34m,
                Currency.Usd,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }

        using var otherClient = CreateClient(_app);
        await Register(otherClient);
        using var otherCreate = await Create(otherClient, "https://other.example/product", Guid.NewGuid());
        var other = (await otherCreate.Content.ReadFromJsonAsync<OrderDto>())!;

        using var response = await _client.GetAsync("/api/v1/orders");
        var items = await response.Content.ReadFromJsonAsync<CustomerOrderListItemDto[]>();
        using var otherResponse = await otherClient.GetAsync("/api/v1/orders");
        var otherItems = await otherResponse.Content.ReadFromJsonAsync<CustomerOrderListItemDto[]>();
        using var anonymousClient = CreateClient(_app);
        await ConsentTestData.AcceptMandatoryCookies(anonymousClient);
        using var anonymousResponse = await anonymousClient.GetAsync("/api/v1/orders");

        response.EnsureSuccessStatusCode();
        otherResponse.EnsureSuccessStatusCode();
        emptyResponse.EnsureSuccessStatusCode();
        Assert.That(items, Is.Not.Null);
        var customerItems = items!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(emptyItems, Is.Empty);
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(customerItems.Select(item => item.Id), Is.EqualTo(new[] { second.Id, first.Id }));
            Assert.That(customerItems, Has.None.Property(nameof(CustomerOrderListItemDto.Id)).EqualTo(other.Id));
            Assert.That(customerItems[0].OrderNumber, Is.EqualTo(second.OrderNumber));
            Assert.That(customerItems[0].CreatedAt, Is.Not.EqualTo(default(DateTimeOffset)));
            Assert.That(customerItems[1].ProductName, Is.EqualTo("Первый товар"));
            Assert.That(customerItems[1].StoreName, Is.EqualTo("Магазин"));
            Assert.That(customerItems[1].ImageUrl, Is.EqualTo("https://images.example/first.jpg"));
            Assert.That(customerItems[1].SellerPrice, Is.EqualTo(new OrderSellerPriceDto(12.34m, Currency.Usd)));
            Assert.That(customerItems[1].Quantity, Is.EqualTo(2));
            Assert.That(otherItems, Has.One.Property(nameof(CustomerOrderListItemDto.Id)).EqualTo(other.Id));
            Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
    }

    [Test]
    public async Task Get_UnknownOrder_ReturnsNotFound()
    {
        using var response = await _client.GetAsync("/api/v1/orders/9223372036854775807");
        var body = await response.Content.ReadFromJsonAsync<SarafanProblemDetails>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(body?.Code, Is.EqualTo("resource_not_found"));
    }

    [Test]
    public async Task GetAndIdempotentReplay_ReturnTheServerOwnedProductSnapshotAndAppliedRate()
    {
        var idempotencyKey = Guid.NewGuid();
        using var createdResponse = await Create(_client, "https://shop.example/product", idempotencyKey);
        var created = (await createdResponse.Content.ReadFromJsonAsync<OrderDto>())!;

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rate = new ExchangeRateHistory
            {
                Provider = "CBR",
                Source = "test",
                BaseCurrency = Currency.Usd,
                QuoteCurrency = Currency.Rub,
                Nominal = 1,
                OfficialRate = 81.123456m,
                SourceEffectiveDate = new DateOnly(2026, 9, 13),
                RetrievedAt = DateTimeOffset.Parse("2026-09-13T00:00:00Z")
            };
            database.ExchangeRateHistory.Add(rate);
            await database.SaveChangesAsync();
            var order = await database.Orders.SingleAsync(item => item.Id == created.Id);
            order.SetProductSnapshot(
                "Товар",
                "Магазин",
                "https://images.example/product.jpg",
                12.34m,
                Currency.Usd,
                10.25m,
                20.50m,
                30.75m,
                new Dictionary<string, string> { ["Цвет"] = "Синий" },
                rate,
                DateTimeOffset.UtcNow);
            await database.SaveChangesAsync();
        }

        using var response = await _client.GetAsync($"/api/v1/orders/{created.Id}");
        var orderDto = await response.Content.ReadFromJsonAsync<OrderDto>();
        using var replayResponse = await Create(_client, "https://shop.example/product", idempotencyKey);
        var replayDto = await replayResponse.Content.ReadFromJsonAsync<OrderDto>();
        var expectedRate = new OrderAppliedExchangeRateDto(
            1,
            "CBR",
            Currency.Usd,
            Currency.Rub,
            1,
            81.123456m,
            new DateOnly(2026, 9, 13));

        response.EnsureSuccessStatusCode();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(orderDto?.ProductName, Is.EqualTo("Товар"));
            Assert.That(orderDto?.StoreName, Is.EqualTo("Магазин"));
            Assert.That(orderDto?.ImageUrl, Is.EqualTo("https://images.example/product.jpg"));
            Assert.That(orderDto?.SellerPrice, Is.EqualTo(new OrderSellerPriceDto(12.34m, Currency.Usd)));
            Assert.That(orderDto?.Dimensions, Is.EqualTo(new OrderDimensionsDto(10.25m, 20.50m, 30.75m)));
            Assert.That(orderDto?.Characteristics, Is.EqualTo(new Dictionary<string, string> { ["Цвет"] = "Синий" }));
            Assert.That(orderDto?.AppliedExchangeRate, Is.EqualTo(expectedRate));
            Assert.That(replayResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(replayDto?.ProductName, Is.EqualTo(orderDto?.ProductName));
            Assert.That(replayDto?.SellerPrice, Is.EqualTo(orderDto?.SellerPrice));
            Assert.That(replayDto?.Dimensions, Is.EqualTo(orderDto?.Dimensions));
            Assert.That(replayDto?.Characteristics, Is.EqualTo(orderDto?.Characteristics));
            Assert.That(replayDto?.AppliedExchangeRate, Is.EqualTo(expectedRate));
        }
    }

    [Test]
    public async Task DemoPhoneSuffixAuthorization_AllowsOrderOperationsWhenPaymentsAreDisabled()
    {
        Assert.That(_app.Services.GetRequiredService<IVerificationCodeProvider>(),
            Is.TypeOf<PhoneSuffixVerificationCodeProvider>());
        using var create = await Create(_client, "https://shop.example/product", Guid.NewGuid());
        var order = await create.Content.ReadFromJsonAsync<OrderDto>();
        using var get = await _client.GetAsync($"/api/v1/orders/{order!.Id}");
        var stored = await get.Content.ReadFromJsonAsync<OrderDto>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(stored, Is.EqualTo(order));
        }
    }

    [Test]
    public async Task CodeCollision_RetriesAndEventuallyCreatesOrder()
    {
        var collisions = new CollisionHarness();
        using var app = IsolatedApp(collisions: collisions);
        using var client = CreateClient(app);
        await Register(client);
        collisions.FailNext(2);

        using var response = await Create(client, "https://shop.example/product", Guid.NewGuid());
        var order = await response.Content.ReadFromJsonAsync<OrderDto>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(order?.OrderNumber, Does.Match("^[0-9]{8}-1$"));
            Assert.That(collisions.FailuresThrown, Is.EqualTo(2));
            Assert.That(collisions.CollisionsClassified, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task CodeCollision_ExhaustionReturnsServiceUnavailableWithoutPersistingIdentity()
    {
        var collisions = new CollisionHarness();
        using var app = IsolatedApp(collisions: collisions);
        using var client = CreateClient(app);
        var session = await Register(client);
        collisions.FailNext(10);

        using var response = await Create(client, "https://shop.example/product", Guid.NewGuid());
        var problem = await response.Content.ReadFromJsonAsync<SarafanProblemDetails>();

        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await database.Customers.AsNoTracking().SingleAsync(item => item.Id == session.Customer.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(problem?.Code, Is.EqualTo("order_number_allocation_failed"));
            Assert.That(collisions.FailuresThrown, Is.EqualTo(10));
            Assert.That(collisions.CollisionsClassified, Is.EqualTo(10));
            Assert.That(customer.OrderCode, Is.Null);
            Assert.That(customer.NextOrderNumber, Is.EqualTo(1));
            Assert.That(await database.Orders.AnyAsync(item => item.CustomerId == customer.Id), Is.False);
        }
    }

    [Test]
    public async Task InvalidKeyAndUrl_DoNotAssignIdentityOrConsumeNumber()
    {
        using var missingKey = await Create(_client, "https://shop.example/product", null);
        using var emptyKey = await Create(_client, "https://shop.example/product", Guid.Empty);
        using var invalidUrl = await Create(_client, "ftp://shop.example/product", Guid.NewGuid());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(missingKey.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await missingKey.Content.ReadFromJsonAsync<SarafanProblemDetails>())?.Code,
                Is.EqualTo("invalid_order_idempotency_key"));
            Assert.That(emptyKey.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await emptyKey.Content.ReadFromJsonAsync<SarafanProblemDetails>())?.Code,
                Is.EqualTo("invalid_order_idempotency_key"));
            Assert.That(invalidUrl.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await invalidUrl.Content.ReadFromJsonAsync<SarafanProblemDetails>())?.Code,
                Is.EqualTo("invalid_order_url"));
        }

        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
        var serviceException = Assert.ThrowsAsync<ServiceException>(() => orders.CreateAsync(
            _session.Customer.Id,
            "https://shop.example/product",
            1,
            null,
            Guid.Empty,
            default));
        var customer = await database.Customers.AsNoTracking().SingleAsync(item => item.Id == _session.Customer.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serviceException?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(serviceException?.Code, Is.EqualTo("invalid_order_idempotency_key"));
            Assert.That(customer.OrderCode, Is.Null);
            Assert.That(customer.NextOrderNumber, Is.EqualTo(1));
            Assert.That(await database.Orders.AnyAsync(item => item.CustomerId == customer.Id), Is.False);
        }
    }

    [Test]
    public async Task InvalidQuantityAndComment_ReturnValidationProblemsWithoutCreatingAnOrder()
    {
        using var missingQuantity = await CreateRaw(
            _client,
            "https://shop.example/product",
            Guid.NewGuid().ToString("D"),
            quantity: null);
        using var zeroQuantity = await Create(
            _client,
            "https://shop.example/product",
            Guid.NewGuid(),
            quantity: 0);
        using var longComment = await Create(
            _client,
            "https://shop.example/product",
            Guid.NewGuid(),
            comment: new string('x', 2001));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(missingQuantity.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await missingQuantity.Content.ReadFromJsonAsync<SarafanProblemDetails>())?.Code,
                Is.EqualTo("validation_failed"));
            Assert.That(zeroQuantity.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await zeroQuantity.Content.ReadFromJsonAsync<SarafanProblemDetails>())?.Code,
                Is.EqualTo("validation_failed"));
            Assert.That(longComment.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await longComment.Content.ReadFromJsonAsync<SarafanProblemDetails>())?.Code,
                Is.EqualTo("validation_failed"));
        }

        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orders = scope.ServiceProvider.GetRequiredService<OrderService>();
        var missingQuantityException = Assert.ThrowsAsync<ServiceException>(() => orders.CreateAsync(
            _session.Customer.Id,
            "https://shop.example/product",
            null,
            null,
            Guid.NewGuid(),
            default));
        var longCommentException = Assert.ThrowsAsync<ServiceException>(() => orders.CreateAsync(
            _session.Customer.Id,
            "https://shop.example/product",
            1,
            new string('x', 2001),
            Guid.NewGuid(),
            default));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(missingQuantityException?.Code, Is.EqualTo("invalid_order_quantity"));
            Assert.That(longCommentException?.Code, Is.EqualTo("invalid_order_comment"));
            Assert.That(await database.Orders.AnyAsync(item => item.CustomerId == _session.Customer.Id), Is.False);
        }
    }

    [Test]
    public async Task Creation_RequiresAuthenticationCookieAndPersonalDataConsent()
    {
        using var anonymous = CreateClient(_app);
        await ConsentTestData.AcceptMandatoryCookies(anonymous);
        using var anonymousResponse = await Create(anonymous, "https://shop.example/product", Guid.NewGuid());

        using var noCookie = CreateClient(_app);
        noCookie.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _session.AccessToken);
        using var noCookieResponse = await Create(noCookie, "https://shop.example/product", Guid.NewGuid());

        var document = (await _client.GetFromJsonAsync<CurrentDocumentDto>(
            $"/api/v1/legal/current/{(int)LegalDocumentKind.PersonalDataConsent}"))!.Document!;
        using var withdrawal = await _client.PostAsJsonAsync("/api/v1/consents/me/personal-data", new ConsentDecisionRequest
        {
            DocumentId = document.Id,
            ContentHash = document.ContentHash,
            Decision = "refuse",
            IdempotencyKey = Guid.NewGuid()
        });
        withdrawal.EnsureSuccessStatusCode();
        using var noConsentResponse = await Create(_client, "https://shop.example/product", Guid.NewGuid());
        var noConsent = await noConsentResponse.Content.ReadFromJsonAsync<SarafanProblemDetails>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anonymousResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(noCookieResponse.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(noConsentResponse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(noConsent?.Code, Is.EqualTo("personal_data_consent_required"));
        }
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> app) => app.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    private static async Task<AuthenticationSessionDto> Register(HttpClient client)
    {
        await ConsentTestData.AcceptMandatoryCookies(client);
        var sequence = Interlocked.Increment(ref _phoneSequence);
        var phone = $"+79996{sequence:D6}";
        var onboarding = await ConsentTestData.Onboarding(client, phone);
        using var response = await client.PostAsJsonAsync("/api/v1/auth/code/verify", new
        {
            phone,
            code = phone[^4..],
            onboardingToken = onboarding
        });
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<AuthenticationSessionDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return session;
    }

    private static Task<HttpResponseMessage> Create(
        HttpClient client,
        string? sourceUrl,
        Guid? idempotencyKey,
        int? quantity = 1,
        string? comment = null)
        => CreateRaw(client, sourceUrl, idempotencyKey?.ToString("D"), quantity, comment);

    private static async Task<HttpResponseMessage> CreateRaw(
        HttpClient client,
        string? sourceUrl,
        string? idempotencyKey,
        int? quantity = 1,
        string? comment = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest
            {
                SourceUrl = sourceUrl,
                Quantity = quantity,
                Comment = comment
            })
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendMalformedCreate(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "not-a-guid");
        return await client.SendAsync(request);
    }

    private static WebApplicationFactory<Program> IsolatedApp(CollisionHarness? collisions = null)
        => IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<VerificationAttemptStore>();
            services.AddSingleton<VerificationAttemptStore>();
            if (collisions is not null)
            {
                services.AddDbContext<AppDbContext>(options => options.AddInterceptors(collisions));
                services.RemoveAll<ICustomerOrderCodeCollisionDetector>();
                services.AddSingleton<ICustomerOrderCodeCollisionDetector>(collisions);
            }
        }));

    private static async Task DemoteDemoBackofficeUsers()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var demoUsers = await database.BackofficeUsers
            .Where(item => item.IsDemo)
            .ToListAsync();
        foreach (var user in demoUsers)
        {
            user.IsDemo = false;
        }
        await database.SaveChangesAsync();
    }

    private sealed class CollisionHarness : SaveChangesInterceptor, ICustomerOrderCodeCollisionDetector
    {
        private int _remainingFailures;

        public int FailuresThrown { get; private set; }
        public int CollisionsClassified { get; private set; }

        public void FailNext(int count) => _remainingFailures = count;

        public bool IsCollision(DbUpdateException exception)
        {
            CollisionsClassified++;
            return true;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            eventData.Context?.ChangeTracker.DetectChanges();
            var assignsOrderCode = eventData.Context?.ChangeTracker
                .Entries<Customer>()
                .Any(entry => entry.State == EntityState.Modified
                    && entry.Property(item => item.OrderCode).IsModified
                    && entry.Property(item => item.OrderCode).OriginalValue is null
                    && entry.Property(item => item.OrderCode).CurrentValue is not null) == true;
            if (_remainingFailures > 0 && assignsOrderCode)
            {
                _remainingFailures--;
                FailuresThrown++;
                throw new DbUpdateException("Simulated customer order-code collision.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

}
