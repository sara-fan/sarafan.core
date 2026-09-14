// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;

using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Sarafan.Core.Controllers;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[TestFixture]
[NonParallelizable]
public sealed class OrderPreviewTests
{
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        await IntegrationTestEnvironment.ResetAsync();
        _app = IntegrationTestEnvironment.Factory;
        _client = _app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

    [TearDown]
    public void TearDown() => _client.Dispose();

    [TestCase("shop.example.com/product", "https://shop.example.com/product")]
    [TestCase("//shop.example.com/product", "https://shop.example.com/product")]
    [TestCase("http://shop.example.com/product", "http://shop.example.com/product")]
    [TestCase("HTTPS://SHOP.Example.COM/Product?x=1#part", "https://shop.example.com/Product?x=1#part")]
    [TestCase("shop.example.com:8443/product", "https://shop.example.com:8443/product")]
    [TestCase("shop.example.com.:8443/product", "https://shop.example.com.:8443/product")]
    [TestCase("https://shop.example.com", "https://shop.example.com/")]
    [TestCase("https://shop.example.com./product", "https://shop.example.com./product")]
    [TestCase("магазин.рф/товар", "https://xn--80aairftm.xn--p1ai/%D1%82%D0%BE%D0%B2%D0%B0%D1%80")]
    public async Task Preview_NormalizesWebAddressesAndAlwaysReturnsManualReview(
        string sourceUrl,
        string expected)
    {
        var before = await CountOrders();

        using var response = await _client.PostAsJsonAsync(
            "/api/v1/orders/preview",
            new ProductPreviewRequest { SourceUrl = $"  {sourceUrl}  " });
        var preview = await response.Content.ReadFromJsonAsync<ProductPreviewDto>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(preview, Is.EqualTo(new ProductPreviewDto(expected, ProductPreviewDto.ManualReviewOutcome)));
            Assert.That(await CountOrders(), Is.EqualTo(before));
        }
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("ftp://shop.example.com/product")]
    [TestCase("file:///tmp/product")]
    [TestCase("javascript:alert(1)")]
    [TestCase("ftp:21")]
    [TestCase("javascript:123")]
    [TestCase("https://")]
    [TestCase("https:///path")]
    [TestCase("shop example/product")]
    [TestCase("xxxx")]
    [TestCase("http://xxxx")]
    [TestCase("shop.invalid/product")]
    [TestCase("https://127.0.0.1/product")]
    [TestCase("https://[::1]/product")]
    [TestCase("https://alice:secret@shop.example.com/product")]
    [TestCase("https://alice@shop.example.com/product")]
    [TestCase("https://:secret@shop.example.com/product")]
    [TestCase("https://bad_label.com/product")]
    [TestCase("https://-shop.com/product")]
    [TestCase("https://shop-.com/product")]
    [TestCase("https://shop..com/product")]
    [TestCase("https://shop.example.com../product")]
    [TestCase("shop.example.com../product")]
    [TestCase("https://a\u200D.com/product")]
    public async Task Preview_RejectsInvalidOrUnsupportedAddresses(string? sourceUrl)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/orders/preview",
            new ProductPreviewRequest { SourceUrl = sourceUrl });
        var problem = await response.Content.ReadFromJsonAsync<SarafanProblemDetails>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(response.Content.Headers.ContentType?.MediaType,
                Is.EqualTo(SarafanProblemDetailsFactory.MediaType));
            Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
            Assert.That(problem?.Code, Is.EqualTo("invalid_order_url"));
            Assert.That(problem?.Type, Is.EqualTo("https://sarafan.sw.consulting/problems/invalid-order-url"));
            Assert.That(problem?.Detail, Is.EqualTo("Проверьте ссылку на товар и попробуйте ещё раз"));
        }
    }

    [Test]
    public async Task Preview_RejectsAnAddressWhoseCanonicalValueExceedsStorageLimit()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/orders/preview",
            new ProductPreviewRequest { SourceUrl = $"shop.example.com/{new string('я', 400)}" });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public void Preview_BoundsTheAnonymousRequestBody()
    {
        var action = typeof(OrderPreviewController).GetMethod(nameof(OrderPreviewController.Preview));
        var limit = action?.GetCustomAttribute<RequestSizeLimitAttribute>();

        Assert.That(((IRequestSizeLimitMetadata?)limit)?.MaxRequestBodySize, Is.EqualTo(32 * 1024));
    }

    [Test]
    public async Task OrderCreation_UsesTheSameCanonicalAddressAsPreview()
    {
        var session = await Register("+79993124567");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var idempotencyKey = Guid.NewGuid().ToString("D");

        using var previewResponse = await _client.PostAsJsonAsync(
            "/api/v1/orders/preview",
            new ProductPreviewRequest { SourceUrl = "shop.example.com/product" });
        var preview = (await previewResponse.Content.ReadFromJsonAsync<ProductPreviewDto>())!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest
            {
                SourceUrl = "shop.example.com/product",
                Quantity = 1
            })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await _client.SendAsync(request);
        var order = await response.Content.ReadFromJsonAsync<OrderDto>();
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest
            {
                SourceUrl = "https://shop.example.com/product",
                Quantity = 1
            })
        };
        replayRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        using var replayResponse = await _client.SendAsync(replayRequest);
        var replay = await replayResponse.Content.ReadFromJsonAsync<OrderDto>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(order?.SourceUrl, Is.EqualTo(preview.SourceUrl));
            Assert.That(order?.Status, Is.EqualTo(Sarafan.Core.Models.OrderStatus.UnderReview));
            Assert.That(order?.ProductName, Is.Null);
            Assert.That(order?.StoreName, Is.Null);
            Assert.That(order?.SellerPrice, Is.Null);
            Assert.That(replay?.Id, Is.EqualTo(order?.Id));
            Assert.That(await CountOrders(), Is.EqualTo(1));
        }
    }

    [TestCaseSource(nameof(LegacySourceUrls))]
    public async Task OrderCreation_ReplaysPreCanonicalizationRows(
        string legacySourceUrl,
        string canonicalSourceUrl)
    {
        var session = await Register("+79993124568");
        var idempotencyKey = Guid.NewGuid();
        long legacyOrderId;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = await database.Customers.SingleAsync(item => item.Id == session.Customer.Id);
            var orderNumber = customer.AllocateOrderNumber("87654321");
            var legacyOrder = new Order(
                customer.Id,
                orderNumber,
                legacySourceUrl,
                1,
                null,
                idempotencyKey,
                DateTimeOffset.UtcNow);
            database.Orders.Add(legacyOrder);
            await database.SaveChangesAsync();
            legacyOrderId = legacyOrder.Id;
        }

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest
            {
                SourceUrl = canonicalSourceUrl,
                Quantity = 1
            })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
        using var response = await _client.SendAsync(request);
        var replay = await response.Content.ReadFromJsonAsync<OrderDto>();
        using var getResponse = await _client.GetAsync($"/api/v1/orders/{legacyOrderId}");
        var read = await getResponse.Content.ReadFromJsonAsync<OrderDto>();
        var list = await _client.GetFromJsonAsync<CustomerOrderListItemDto[]>("/api/v1/orders");

        await using var verificationScope = _app.Services.CreateAsyncScope();
        var verificationDatabase = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedSourceUrl = await verificationDatabase.Orders
            .Where(item => item.Id == legacyOrderId)
            .Select(item => item.SourceUrl)
            .SingleAsync();
        var backoffice = await verificationScope.ServiceProvider
            .GetRequiredService<OrderService>()
            .ListForBackofficeAsync(
                1,
                10,
                "createdAt",
                "desc",
                null,
                null,
                null,
                null,
                null,
                default);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
            Assert.That(replay?.Id, Is.EqualTo(legacyOrderId));
            Assert.That(replay?.SourceUrl, Is.EqualTo(canonicalSourceUrl));
            Assert.That(read?.SourceUrl, Is.EqualTo(canonicalSourceUrl));
            Assert.That(list?.Single().SourceUrl, Is.EqualTo(canonicalSourceUrl));
            Assert.That(backoffice.Items.Single().SourceUrl, Is.EqualTo(canonicalSourceUrl));
            Assert.That(storedSourceUrl, Is.EqualTo(legacySourceUrl));
            Assert.That(await verificationDatabase.Orders.CountAsync(), Is.EqualTo(1));
        }
    }

    [Test]
    public void StoredNormalization_FallsBackWithoutThrowingForNonCanonicalLegacyValues()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                ProductSourceUrl.NormalizeStored("shop.example.com/product"),
                Is.EqualTo("shop.example.com/product"));
            Assert.That(
                ProductSourceUrl.NormalizeStored("https://shop.example.com../product"),
                Is.EqualTo("https://shop.example.com../product"));
        }
    }

    private static IEnumerable<TestCaseData> LegacySourceUrls()
    {
        yield return new TestCaseData(
            "https://SHOP.Example.COM/product",
            "https://shop.example.com/product");
        yield return new TestCaseData(
            "https://shop.example.com",
            "https://shop.example.com/");
        yield return new TestCaseData(
            "https://SHOP.Example/product",
            "https://shop.example/product");
        var unicodeUrl = $"https://shop.example.com/{new string('я', 400)}";
        yield return new TestCaseData(unicodeUrl, unicodeUrl);
    }

    private async Task<AuthenticationSessionDto> Register(string phone)
    {
        var onboarding = await ConsentTestData.Onboarding(_client, phone);
        using var response = await _client.PostAsJsonAsync("/api/v1/auth/code/verify", new
        {
            phone,
            code = phone[^4..],
            onboardingToken = onboarding
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthenticationSessionDto>())!;
    }

    private async Task<int> CountOrders()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders.CountAsync();
    }
}
