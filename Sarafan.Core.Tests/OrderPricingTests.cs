// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

public sealed partial class OrderPricingTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Test]
    public void ManualAmountsUseNumericKeysWithoutChangingOtherEnums()
    {
        var inputs = OrderPricingInputs.Empty with
        {
            ManualAmounts = Enum.GetValues<ServiceKind>().ToDictionary(kind => kind, _ => 3.25m),
            SelectedServices = [ServiceKind.WarehousePhoto]
        };
        var json = JsonSerializer.Serialize(inputs, WebJson);
        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("manualAmounts").EnumerateObject().Select(item => item.Name),
            Is.EqualTo(new[] { "0", "100", "200", "300", "400", "500", "600", "700", "800" }));
        Assert.That(document.RootElement.GetProperty("selectedServices")[0].GetInt32(), Is.EqualTo(500));
        Assert.That(JsonSerializer.Deserialize<OrderPricingInputs>(json, WebJson)!.ManualAmounts,
            Is.EquivalentTo(inputs.ManualAmounts));
        Assert.That(JsonSerializer.Serialize(OrderPricingInputs.Empty, WebJson), Does.Contain("\"manualAmounts\":{}"));
    }

    [TestCase("{\"unknown\":1}")]
    [TestCase("{\"100\":\"invalid\"}")]
    [TestCase("[]")]
    public void ManualAmountsRejectMalformedJson(string amounts)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OrderPricingInputs>(
            "{\"manualAmounts\":" + amounts + ",\"selectedServices\":[]}", WebJson));

    [Test]
    public async Task ManualPricingRoundTripsRequestsSnapshotsAndRetainedHistory()
    {
        db.ServiceCatalogueEntries.RemoveRange(db.ServiceCatalogueEntries.Where(item => item.Service == ServiceKind.UsWarehouseExpenses));
        db.Add(Tariff(ServiceKind.UsWarehouseExpenses, PriceMethod.Manual, Currency.Usd));
        await db.SaveChangesAsync();
        var request = JsonSerializer.Deserialize<OrderPricingWriteRequest>(
            "{\"expectedUpdatedAt\":\"2026-09-24T10:00:00Z\",\"inputs\":{\"manualAmounts\":{\"100\":3.25},\"selectedServices\":[]}}", WebJson)!;
        var saved = await service.UpdatePricingAsync("12345678-1", request, actorId, Shift, default);
        AssertNumericManualAmounts(saved);
        var snapshot = await db.OrderPricingSnapshots.SingleAsync();
        using (var stored = JsonDocument.Parse(snapshot.Payload))
            Assert.That(stored.RootElement.GetProperty("inputs").GetProperty("manualAmounts").GetProperty("100").GetDecimal(), Is.EqualTo(3.25m));

        // Simulate the previous serializer's immutable persisted representation in disposable test storage.
        var historical = new OrderPricingSnapshot
        {
            Order = order,
            At = Now,
            ActorId = actorId,
            ActorName = "Иванов Иван",
            Payload = snapshot.Payload.Replace("\"100\":", "\"UsWarehouseExpenses\":", StringComparison.Ordinal)
        };
        db.Add(historical);
        await db.SaveChangesAsync();
        var historicalPayload = historical.Payload;
        var read = await service.GetPricingAsync("12345678-1", Shift, default);
        AssertNumericManualAmounts(read);
        var confirmed = await service.ConfirmPricingAsync("12345678-1", new(read.UpdatedAt), actorId, Shift, default);
        AssertNumericManualAmounts(confirmed);
        Assert.That(confirmed.Calculation.TotalRub, Is.EqualTo(saved.Calculation.TotalRub));
        Assert.That(await db.OrderPricingSnapshots.CountAsync(), Is.EqualTo(3));
        Assert.That((await db.OrderPricingSnapshots.SingleAsync(item => item.Id == historical.Id)).Payload, Is.EqualTo(historicalPayload));
    }

    private static void AssertNumericManualAmounts(OrderPricingDto value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, WebJson));
        var root = document.RootElement;
        foreach (var calculation in new[] { root.GetProperty("calculation") })
        {
            var amounts = calculation.GetProperty("inputs").GetProperty("manualAmounts");
            Assert.That(amounts.EnumerateObject().Select(item => item.Name), Is.EqualTo(new[] { "100" }));
            Assert.That(amounts.GetProperty("100").GetDecimal(), Is.EqualTo(3.25m));
        }
    }

    [TestCase("{}")]
    [TestCase("{\"from\":0,\"by\":null}")]
    [TestCase("{\"from\":0,\"amount\":0}")]
    [TestCase("{\"by\":null,\"amount\":0}")]
    public void BandJsonRequiresExplicitBoundsAndAmount(string json)
        => Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<PriceBand>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

    [Test]
    public void BandJsonAcceptsExplicitUnboundedFirstLowerValue()
    {
        var band = System.Text.Json.JsonSerializer.Deserialize<PriceBand>("{\"from\":null,\"by\":100,\"amount\":2}",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.That(band, Is.EqualTo(new PriceBand(null, 100, 2)));
    }

    [Test]
    public void EveryServiceAllowsAllMethodsWithUsdPercentCharges()
    {
        foreach (var kind in Enum.GetValues<ServiceKind>())
        {
            var metadata = ServiceCatalogueRules.Operations(Admin).Services.Single(item => item.Value == (int)kind);
            Assert.That(metadata.AllowedCurrencies, Is.EqualTo(new[] { Currency.Rub, Currency.Usd }));
            Assert.That(metadata.AllowedPriceMethods, Is.EqualTo(Enum.GetValues<PriceMethod>()));
            foreach (var currency in new[] { Currency.Rub, Currency.Usd })
                foreach (var method in Enum.GetValues<PriceMethod>())
                {
                    var request = new ServiceCatalogueWriteRequest
                    {
                        Service = kind,
                        PriceMethod = method,
                        Currency = currency,
                        AvailableFrom = new(2026, 1, 1),
                        Percentage = method == PriceMethod.Percent ? 10 : null,
                        Amount = method == PriceMethod.Fixed ? 2 : null,
                        IntervalCurrency = method == PriceMethod.Stepped ? Currency.Usd : null,
                        Bands = method == PriceMethod.Stepped ? [new(null, null, 2)] : []
                    };
                    if (method == PriceMethod.Percent && currency == Currency.Rub)
                        Assert.That(Assert.Throws<ServiceException>(() => ServiceCatalogueRules.Prepare(request, false))!.Code,
                            Is.EqualTo("invalid_service_catalogue_currency"));
                    else
                        Assert.DoesNotThrow(() => ServiceCatalogueRules.Prepare(request, false), $"{kind}/{method}/{currency}");
                    request.Currency = Currency.Eur;
                    Assert.That(Assert.Throws<ServiceException>(() => ServiceCatalogueRules.Prepare(request, false))!.Code,
                        Is.EqualTo("invalid_service_catalogue_currency"));
                }
        }
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T10:00:00Z");
    private static readonly string[] Admin = [BackofficeRoles.Administrator];
    private static readonly string[] Shift = [BackofficeRoles.ShiftManager];
    private AppDbContext db = null!;
    private Order order = null!;
    private int actorId;
    private OrderService service = null!;

    [SetUp]
    public async Task Setup()
    {
        db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var customer = new Customer { Phone = "+79990001234", CreatedAt = Now, UpdatedAt = Now };
        customer.AllocateOrderNumber("12345678");
        var actor = new BackofficeUser { Email = "pricing@test.invalid", NormalizedEmail = "pricing@test.invalid", FirstName = "Иван", LastName = "Иванов", PasswordHash = "unused", CreatedAt = Now, UpdatedAt = Now };
        db.AddRange(customer, actor);
        await db.SaveChangesAsync();
        actorId = actor.Id;
        order = new(customer.Id, 1, "https://example.com/product", 2, null, Guid.NewGuid(), Now);
        order.SetProduct(new("Товар", new(50, Currency.Usd), 2, null, null, null));
        db.Add(order);
        db.ExchangeRateHistory.Add(OrderProductTestData.Rate(Currency.Usd, 80));
        db.ServiceCatalogueEntries.AddRange(Tariff(ServiceKind.UsWarehouseExpenses, PriceMethod.Fixed, Currency.Usd, amount: 0),
            Tariff(ServiceKind.InternationalDelivery, PriceMethod.Fixed, Currency.Usd, amount: 1.23m),
            Tariff(ServiceKind.ServiceCommission, PriceMethod.Percent, Currency.Usd, percentage: 10, minimum: 12));
        await db.SaveChangesAsync();
        service = new(db, null!, null!, null!, null!, null!, new Clock(), NullLogger<OrderService>.Instance);
    }

    [TearDown] public void TearDown() => db.Dispose();

    private static ServiceCatalogueEntry Tariff(ServiceKind kind, PriceMethod method, Currency currency,
        decimal? amount = null, decimal? percentage = null, decimal? minimum = null)
        => new(kind, method, percentage, minimum, null, amount, currency, new(2026, 1, 1), null, Now);

    [TestCase(null)]
    [TestCase(0)]
    [TestCase(500)]
    public async Task ComponentsUseQuantityMinimumAndSeparateExtras(int? extra)
    {
        db.AddRange(Tariff(ServiceKind.DomesticDelivery, PriceMethod.Manual, Currency.Rub),
            Tariff(ServiceKind.CustomsPayments, PriceMethod.Manual, Currency.Rub));
        await db.SaveChangesAsync();
        var inputs = OrderPricingInputs.Empty with
        {
            ManualAmounts = extra is { } amount
                ? new() { [ServiceKind.DomesticDelivery] = amount, [ServiceKind.CustomsPayments] = amount }
                : new()
        };
        OrderPriceCalculator.ValidateInputs(inputs, await OrderPriceCalculator.TariffsAsync(db, Now, default));
        var result = await OrderPriceCalculator.CalculateAsync(db, order, Now, inputs, null, default);
        Assert.That(result.Components.Single(row => row.Service == ServiceKind.Product).Amount, Is.EqualTo(100));
        Assert.That(result.Components.Single(row => row.Service == ServiceKind.ServiceCommission).AmountRub, Is.EqualTo(960));
        Assert.That(result.Components.Single(row => row.Service == ServiceKind.UsWarehouseExpenses).State, Is.EqualTo(PriceComponentState.Calculated));
        Assert.That(result.Components.Single(row => row.Service == ServiceKind.WarehousePhoto).State, Is.EqualTo(PriceComponentState.NotApplicable));
        Assert.That(result.TotalRub, Is.EqualTo(9058.40m));
        foreach (var kind in new[] { ServiceKind.DomesticDelivery, ServiceKind.CustomsPayments })
        {
            var component = result.Components.Single(item => item.Service == kind);
            Assert.That(component.AmountRub, Is.EqualTo(extra));
            Assert.That(component.State, Is.EqualTo(extra is null ? PriceComponentState.NotCalculated : PriceComponentState.Calculated));
            Assert.That(component.Tariff, Is.Not.Null);
        }
    }

    [TestCase(PriceMethod.Percent)]
    [TestCase(PriceMethod.Fixed)]
    [TestCase(PriceMethod.Manual)]
    [TestCase(PriceMethod.Auto)]
    [TestCase(PriceMethod.Stepped)]
    public async Task TariffDrivenComponentsCalculateEveryMethodInEitherCurrency(PriceMethod method)
    {
        foreach (var kind in new[] { ServiceKind.UsWarehouseExpenses, ServiceKind.InternationalDelivery,
            ServiceKind.ServiceCommission, ServiceKind.WarehousePhoto, ServiceKind.ProductInspection, ServiceKind.ShipmentInsurance })
            foreach (var currency in new[] { Currency.Rub, Currency.Usd })
            {
                if (method == PriceMethod.Percent && currency == Currency.Rub) continue;
                db.ServiceCatalogueEntries.RemoveRange(db.ServiceCatalogueEntries.Where(item => item.Service == kind));
                var tariff = new ServiceCatalogueEntry(kind, method, method == PriceMethod.Percent ? 10 : null,
                    method == PriceMethod.Percent ? 12 : null, null, method == PriceMethod.Fixed ? 3.25m : null,
                    currency, new(2026, 1, 1), null, Now,
                    method == PriceMethod.Stepped ? Currency.Usd : null,
                    method == PriceMethod.Stepped ? [new(null, 100, 1), new(100, null, 3.25m)] : []);
                db.Add(tariff);
                await db.SaveChangesAsync();
                var inputs = OrderPricingInputs.Empty with
                {
                    ManualAmounts = method == PriceMethod.Manual ? new() { [kind] = 3.25m } : new(),
                    SelectedServices = [ServiceKind.WarehousePhoto, ServiceKind.ProductInspection, ServiceKind.ShipmentInsurance]
                };
                OrderPriceCalculator.ValidateInputs(inputs, await OrderPriceCalculator.TariffsAsync(db, Now, default));
                var imports = new ImportedAmount();
                var calculation = await OrderPriceCalculator.CalculateAsync(db, order, Now, inputs, imports, default);
                var component = calculation.Components.Single(item => item.Service == kind);
                var expected = method == PriceMethod.Percent ? 12m
                    : method == PriceMethod.Stepped ? 1m : 3.25m;
                Assert.That(component.State, Is.EqualTo(PriceComponentState.Calculated), $"{kind}/{method}/{currency}");
                Assert.That(component.Currency, Is.EqualTo(currency));
                Assert.That(component.Amount, Is.EqualTo(expected));
                Assert.That(component.AmountRub, Is.EqualTo(currency == Currency.Usd ? expected * 80 : expected));
                Assert.That(component.Tariff!.Currency, Is.EqualTo(currency));
                if (method == PriceMethod.Auto) Assert.That(imports.Requests, Does.Contain((kind, currency)));
            }
    }

    private sealed class ImportedAmount : IAutomaticPriceSource
    {
        public List<(ServiceKind, Currency)> Requests { get; } = [];
        public Task<decimal?> GetAmountAsync(Order order, ServiceKind service, Currency currency, CancellationToken token)
        {
            Requests.Add((service, currency));
            return Task.FromResult<decimal?>(3.25m);
        }
    }

    [TestCase("1.235", "1.24")]
    [TestCase("1.225", "1.23")]
    [TestCase("1.234", "1.23")]
    public void RoundsHalfUp(string input, string expected)
        => Assert.That(OrderPriceCalculator.Round(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)),
            Is.EqualTo(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)));

    [TestCase(0, 2)]
    [TestCase(99.99, 2)]
    [TestCase(100, 2)]
    [TestCase(100.001, 3)]
    [TestCase(999.99, 3)]
    [TestCase(1000, 3)]
    [TestCase(1000.001, 4)]
    public void StepUsesOpenLowerAndInclusiveUpperBoundary(decimal basis, decimal expected)
        => Assert.That(OrderPriceCalculator.Step(basis, [new(null, 100, 2), new(100, 1000, 3), new(1000, null, 4)]), Is.EqualTo(expected));

    [Test]
    public async Task UndatedTariffAppliesBeforeAnyConfiguredStartDate()
    {
        var dated = db.ServiceCatalogueEntries.Single(item => item.Service == ServiceKind.InternationalDelivery);
        db.ServiceCatalogueEntries.Remove(dated);
        db.ServiceCatalogueEntries.Add(new ServiceCatalogueEntry(ServiceKind.InternationalDelivery, PriceMethod.Fixed,
            null, null, null, 2m, Currency.Usd, null, new DateOnly(2026, 12, 31), Now));
        await db.SaveChangesAsync();
        var result = await OrderPriceCalculator.CalculateAsync(db, order, Now, OrderPricingInputs.Empty, null, default);
        Assert.That(result.Components.Single(item => item.Service == ServiceKind.InternationalDelivery).Amount, Is.EqualTo(2m));
    }

    [Test]
    public async Task MissingRateIsUnknownNotZeroAndNoEurRequired()
    {
        db.ExchangeRateHistory.RemoveRange(db.ExchangeRateHistory);
        await db.SaveChangesAsync();
        var result = await OrderPriceCalculator.CalculateAsync(db, order, Now, OrderPricingInputs.Empty, null, default);
        Assert.That(result.TotalRub, Is.Null);
        Assert.That(result.ExchangeRate, Is.Null);
        Assert.That(result.Components.Single(row => row.Service == ServiceKind.Product).State, Is.EqualTo(PriceComponentState.NotCalculated));
    }

    [Test]
    public async Task StepUsesUsdMerchandiseAndAuditKeepsBands()
    {
        var catalogue = new ServiceCatalogueService(db, new Clock(), NullLogger<ServiceCatalogueService>.Instance);
        var request = new ServiceCatalogueWriteRequest
        {
            Service = ServiceKind.WarehousePhoto,
            PriceMethod = PriceMethod.Stepped,
            Currency = Currency.Usd,
            IntervalCurrency = Currency.Usd,
            Bands = [new(null, 99, 2), new(99, null, 3)],
            AvailableFrom = new(2026, 1, 1)
        };
        var created = await catalogue.CreateAsync(request, actorId, Admin, default);
        var result = await OrderPriceCalculator.CalculateAsync(db, order, Now, OrderPricingInputs.Empty with { SelectedServices = [ServiceKind.WarehousePhoto] }, null, default);
        Assert.That(result.Components.Single(row => row.Service == ServiceKind.WarehousePhoto).AmountRub, Is.EqualTo(240));
        await catalogue.DeleteAsync(created.Id, created.Version, actorId, Admin, default);
        var audit = await catalogue.AuditAsync(null, null, created.Id, null, 1, 10, "timestamp", "asc", Shift, default);
        Assert.That(audit.Items[0].After!.Bands, Is.EqualTo(request.Bands));
        Assert.That(audit.Items[1].Before!.IntervalCurrency, Is.EqualTo(Currency.Usd));
    }

    [Test]
    public void BandsRejectGapsOverlapsScalesAndNonMerchandiseCurrencies()
    {
        var request = new ServiceCatalogueWriteRequest
        {
            Service = ServiceKind.Product,
            PriceMethod = PriceMethod.Stepped,
            Currency = Currency.Usd,
            IntervalCurrency = Currency.Usd,
            AvailableFrom = new(2026, 1, 1)
        };
        foreach (var bands in new PriceBand[][] { [], [new(1, null, 2)], [new(0, 100, 2)], [new(null, null, -1)],
            [new(null, 100, 2), new(99, null, 3)], [new(null, 100, 2), new(101, null, 3)], [new(null, null, 1.001m)] })
        {
            request.Bands = bands;
            Assert.That(Assert.Throws<ServiceException>(() => ServiceCatalogueRules.Prepare(request, false))!.Code, Is.EqualTo("invalid_service_catalogue_bands"));
        }
        request.Bands = [new(null, null, 0)];
        foreach (var currency in new[] { Currency.Rub, Currency.Eur })
        {
            request.IntervalCurrency = currency;
            Assert.That(Assert.Throws<ServiceException>(() => ServiceCatalogueRules.Prepare(request, false))!.Code, Is.EqualTo("invalid_service_catalogue_currency"));
        }
    }

    [Test]
    public async Task ConfirmationFreezesSavedValuesAndRejectsFurtherWrites()
    {
        var saved = await service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, OrderPricingInputs.Empty), actorId, Shift, default);
        db.ExchangeRateHistory.Add(OrderProductTestData.Rate(Currency.Usd, 90, date: new(2026, 9, 24)));
        await db.SaveChangesAsync();
        var confirmed = await service.ConfirmPricingAsync("12345678-1", new(saved.UpdatedAt), actorId, Shift, default);
        Assert.That(confirmed.Calculation.TotalRub, Is.EqualTo(saved.Calculation.TotalRub));
        Assert.That(confirmed.ValidUntil, Is.EqualTo(Now.AddHours(24)));
        Assert.That((await db.OrderPricingSnapshots.OrderByDescending(item => item.Id).FirstAsync()).ActorName, Is.EqualTo("Иванов Иван"));
        Assert.That(order.Status, Is.EqualTo(OrderStatus.QuoteReady));
        var ex = Assert.ThrowsAsync<ServiceException>(() => service.UpdatePricingAsync("12345678-1", new(confirmed.UpdatedAt, OrderPricingInputs.Empty), actorId, Admin, default));
        Assert.That(ex!.Code, Is.EqualTo("order_not_editable"));
    }

    [Test]
    public async Task VersionAndRoleGuardsAreIndependentFromCatalogue()
    {
        Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.UpdatePricingAsync("12345678-1", new(Now.AddSeconds(-1), OrderPricingInputs.Empty), actorId, Shift, default))!.Code, Is.EqualTo("order_update_conflict"));
        Assert.That(Assert.ThrowsAsync<ServiceException>(() => service.UpdatePricingAsync("12345678-1", new(order.UpdatedAt, OrderPricingInputs.Empty), actorId, [BackofficeRoles.Operator], default))!.Code, Is.EqualTo("access_denied"));
        Assert.That((await service.GetPricingAsync("12345678-1", [BackofficeRoles.Operator], default)).CanEdit, Is.False);
        Assert.That(BackofficeAuthorization.IsAllowed(Shift, BackofficeAction.ManageServiceCatalogue), Is.False);
    }

    [Test]
    public async Task AutoCannotBeOverriddenAndMissingImportRemainsUnknown()
    {
        var tariff = Tariff(ServiceKind.WarehousePhoto, PriceMethod.Auto, Currency.Usd);
        db.Add(tariff); await db.SaveChangesAsync();
        var inputs = OrderPricingInputs.Empty with { SelectedServices = [ServiceKind.WarehousePhoto] };
        var result = await OrderPriceCalculator.CalculateAsync(db, order, Now, inputs, null, default);
        Assert.That(result.TotalRub, Is.Null);
        Assert.Throws<ServiceException>(() => OrderPriceCalculator.ValidateInputs(inputs with { ManualAmounts = new() { [ServiceKind.WarehousePhoto] = 1 } }, [tariff]));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StaffCannotChangeCustomerOptionalServices(bool existingSelection)
    {
        var selected = existingSelection ? new[] { ServiceKind.WarehousePhoto, ServiceKind.ProductInspection } : [];
        if (existingSelection)
        {
            var calculation = await OrderPriceCalculator.CalculateAsync(db, order, Now,
                OrderPricingInputs.Empty with { SelectedServices = selected }, null, default);
            db.OrderPricingSnapshots.Add(new() { Order = order, At = Now, Payload = JsonSerializer.Serialize(calculation, WebJson) });
            await db.SaveChangesAsync();
        }
        var originalVersion = order.UpdatedAt;
        var count = await db.OrderPricingSnapshots.CountAsync();
        foreach (var roles in new[] { Admin, Shift })
        {
            var changed = OrderPricingInputs.Empty with { SelectedServices = existingSelection ? [] : [ServiceKind.WarehousePhoto] };
            var error = Assert.ThrowsAsync<ServiceException>(() => service.UpdatePricingAsync("12345678-1",
                new(originalVersion, changed), actorId, roles, default));
            Assert.That(error!.Code, Is.EqualTo("invalid_order_pricing"));
            Assert.That(order.UpdatedAt, Is.EqualTo(originalVersion));
            Assert.That(await db.OrderPricingSnapshots.CountAsync(), Is.EqualTo(count));
        }
        var saved = await service.UpdatePricingAsync("12345678-1", new(originalVersion,
            OrderPricingInputs.Empty with { SelectedServices = selected.Reverse().ToArray() }), actorId, Shift, default);
        Assert.That(saved.Calculation.Inputs.SelectedServices, Is.EquivalentTo(selected));
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
