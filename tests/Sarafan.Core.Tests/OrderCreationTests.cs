// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

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
        using var firstResponse = await Create(_client, "  https://shop.example/product?id=1  ", firstKey);
        var first = await firstResponse.Content.ReadFromJsonAsync<OrderDto>();
        using var replayResponse = await Create(_client, "https://shop.example/product?id=1", firstKey);
        var replay = await replayResponse.Content.ReadFromJsonAsync<OrderDto>();
        using var conflictResponse = await Create(_client, "https://shop.example/product?id=2", firstKey);
        var conflict = await conflictResponse.Content.ReadFromJsonAsync<SarafanProblemDetails>();
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
            Assert.That(getByLocation.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(getByLocationResponse, Is.EqualTo(first));
            Assert.That(replayResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(replay, Is.EqualTo(first));
            Assert.That(conflictResponse.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(conflict?.Code, Is.EqualTo("order_creation_conflict"));
            Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(second!.OrderNumber, Is.EqualTo(first.OrderNumber[..^1] + "2"));
            Assert.That(second.Id, Is.Not.EqualTo(first.Id));
            Assert.That(customer?.OrderCode, Is.EqualTo(first.OrderNumber[..8]));
        }

        await using var scope = _app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedCustomer = await database.Customers.AsNoTracking().SingleAsync(item => item.Id == _session.Customer.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(storedCustomer.NextOrderNumber, Is.EqualTo(3));
            Assert.That(await database.Orders.CountAsync(item => item.CustomerId == storedCustomer.Id), Is.EqualTo(2));
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
    public async Task Operations_AreHiddenWhenRealOperationsAreDisabled()
    {
        using var app = IsolatedApp(realOrdersEnabled: false);
        using var client = CreateClient(app);
        var session = await Register(client);

        using var create = await Create(client, "https://shop.example/product", Guid.NewGuid());
        var createProblem = await create.Content.ReadFromJsonAsync<SarafanProblemDetails>();
        using var get = await client.GetAsync("/api/v1/orders/1");
        var getProblem = await get.Content.ReadFromJsonAsync<SarafanProblemDetails>();

        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = await database.Customers.AsNoTracking().SingleAsync(item => item.Id == session.Customer.Id);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(createProblem?.Code, Is.EqualTo("resource_not_found"));
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(getProblem?.Code, Is.EqualTo("resource_not_found"));
            Assert.That(customer.OrderCode, Is.Null);
            Assert.That(await database.Orders.AnyAsync(item => item.CustomerId == customer.Id), Is.False);
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

    private static async Task<HttpResponseMessage> Create(HttpClient client, string sourceUrl, Guid? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest { SourceUrl = sourceUrl })
        };
        if (idempotencyKey.HasValue)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey.Value.ToString("D"));
        }

        return await client.SendAsync(request);
    }

    private static WebApplicationFactory<Program> IsolatedApp(
        bool realOrdersEnabled = true,
        CollisionHarness? collisions = null)
        => IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<VerificationAttemptStore>();
            services.AddSingleton<VerificationAttemptStore>();
            services.RemoveAll<IVerificationCodeProvider>();
            services.AddSingleton<IVerificationCodeProvider>(new ProductionReadyVerificationCodeProvider());
            services.Configure<BackofficeBootstrapOptions>(options =>
            {
                options.RealOrdersEnabled = realOrdersEnabled;
                options.RealPaymentIntegrationEnabled = false;
            });
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

    private sealed class ProductionReadyVerificationCodeProvider : IVerificationCodeProvider
    {
        public bool IsProductionReady => true;

        public Task RequestCodeAsync(string phone, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> VerifyCodeAsync(string phone, string? code, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedCode = phone.Length >= 4 ? phone[^4..] : string.Empty;
            return Task.FromResult(
                expectedCode.Length == 4
                && string.Equals(code?.Trim(), expectedCode, StringComparison.Ordinal));
        }
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
