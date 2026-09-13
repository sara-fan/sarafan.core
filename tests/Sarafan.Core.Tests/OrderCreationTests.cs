// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
        var customer = await _client.GetFromJsonAsync<CustomerDto>("/api/v1/customers/me");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(first, Is.Not.Null);
            Assert.That(first!.Id, Is.Positive);
            Assert.That(first.OrderNumber, Does.Match("^[0-9]{8}-1$"));
            Assert.That(first.Status, Is.EqualTo(OrderStatus.UnderReview));
            Assert.That(first.SourceUrl, Is.EqualTo("https://shop.example/product?id=1"));
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
        var customer = await database.Customers.AsNoTracking().SingleAsync(item => item.Id == _session.Customer.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(customer.OrderCode, Is.Null);
            Assert.That(customer.NextOrderNumber, Is.EqualTo(1));
            Assert.That(await database.Orders.AnyAsync(item => item.CustomerId == customer.Id), Is.False);
        }
    }

    [Test]
    public async Task ConcurrentCreation_ForOneCustomerAllocatesEveryNumberOnce()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => Create(_client, $"https://shop.example/product/{index}", Guid.NewGuid())));
        try
        {
            var orders = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<OrderDto>()));

            Assert.That(responses.Select(response => response.StatusCode), Is.All.EqualTo(HttpStatusCode.Created));
            Assert.That(
                orders.Select(order => long.Parse(order!.OrderNumber.Split('-')[1])).Order(),
                Is.EqualTo(Enumerable.Range(1, 8).Select(value => (long)value)));
            Assert.That(orders.Select(order => order!.OrderNumber[..8]), Is.All.EqualTo(orders[0]!.OrderNumber[..8]));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Test]
    public async Task CustomerCodeCollision_RetriesAndExhaustionRollsBack()
    {
        var codes = await UnusedCodes(3);
        using (var retryApp = WithCodes(codes[0], codes[0], codes[1]))
        using (var firstClient = CreateClient(retryApp))
        using (var secondClient = CreateClient(retryApp))
        {
            await Register(firstClient);
            await Register(secondClient);
            using var first = await Create(firstClient, "https://shop.example/first", Guid.NewGuid());
            using var second = await Create(secondClient, "https://shop.example/second", Guid.NewGuid());
            var firstOrder = await first.Content.ReadFromJsonAsync<OrderDto>();
            var secondOrder = await second.Content.ReadFromJsonAsync<OrderDto>();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                Assert.That(firstOrder!.OrderNumber, Is.EqualTo($"{codes[0]}-1"));
                Assert.That(secondOrder!.OrderNumber, Is.EqualTo($"{codes[1]}-1"));
            }
        }

        using var exhaustionApp = WithCodes(Enumerable.Repeat(codes[2], 11).ToArray());
        using var ownerClient = CreateClient(exhaustionApp);
        using var rejectedClient = CreateClient(exhaustionApp);
        await Register(ownerClient);
        var rejectedSession = await Register(rejectedClient);
        using var owner = await Create(ownerClient, "https://shop.example/owner", Guid.NewGuid());
        using var rejected = await Create(rejectedClient, "https://shop.example/rejected", Guid.NewGuid());
        var problem = await rejected.Content.ReadFromJsonAsync<SarafanProblemDetails>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(owner.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(problem?.Code, Is.EqualTo("order_number_allocation_failed"));
        }

        await using var scope = exhaustionApp.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rejectedCustomer = await database.Customers.AsNoTracking()
            .SingleAsync(item => item.Id == rejectedSession.Customer.Id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejectedCustomer.OrderCode, Is.Null);
            Assert.That(rejectedCustomer.NextOrderNumber, Is.EqualTo(1));
            Assert.That(await database.Orders.AnyAsync(item => item.CustomerId == rejectedCustomer.Id), Is.False);
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

    private static WebApplicationFactory<Program> WithCodes(params string[] codes)
        => IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<VerificationAttemptStore>();
            services.AddSingleton<VerificationAttemptStore>();
            services.RemoveAll<ICustomerOrderCodeGenerator>();
            services.AddSingleton<ICustomerOrderCodeGenerator>(new SequenceCodeGenerator(codes));
        }));

    private static WebApplicationFactory<Program> IsolatedApp()
        => IntegrationTestEnvironment.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<VerificationAttemptStore>();
            services.AddSingleton<VerificationAttemptStore>();
        }));

    private static async Task<string[]> UnusedCodes(int count)
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var used = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Customers
            .Where(customer => customer.OrderCode != null)
            .Select(customer => customer.OrderCode!)
            .ToHashSetAsync();
        return Enumerable.Range(0, 100_000_000)
            .Select(value => value.ToString("D8"))
            .Where(value => !used.Contains(value))
            .Take(count)
            .ToArray();
    }

    private sealed class SequenceCodeGenerator(IEnumerable<string> codes) : ICustomerOrderCodeGenerator
    {
        private readonly ConcurrentQueue<string> _codes = new(codes);
        private string? _last;

        public string Generate()
        {
            if (_codes.TryDequeue(out var code))
            {
                _last = code;
                return code;
            }

            return _last ?? throw new InvalidOperationException("No test customer order code was configured.");
        }
    }
}
