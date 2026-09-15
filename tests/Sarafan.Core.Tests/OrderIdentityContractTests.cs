// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Services;

namespace Sarafan.Core.ModelTests;

[TestFixture]
public sealed class OrderIdentityContractTests
{
    [Test]
    public void Customer_AllocatesStableCodeAndMonotonicNumbers()
    {
        var customer = new Customer { Phone = "+79991234567" };

        var first = customer.AllocateOrderNumber("00000000");
        var second = customer.AllocateOrderNumber("99999999");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(1));
            Assert.That(second, Is.EqualTo(2));
            Assert.That(customer.OrderCode, Is.EqualTo("00000000"));
            Assert.That(customer.NextOrderNumber, Is.EqualTo(3));
        }
    }

    [TestCase("")]
    [TestCase("1234567")]
    [TestCase("123456789")]
    [TestCase("1234567x")]
    public void Customer_RejectsInvalidOrderCode(string value)
    {
        var customer = new Customer { Phone = "+79991234567" };

        Assert.Throws<ArgumentException>(() => customer.AllocateOrderNumber(value));
        Assert.That(customer.OrderCode, Is.Null);
        Assert.That(customer.NextOrderNumber, Is.EqualTo(1));
    }

    [Test]
    public void CryptographicGenerator_ProducesEightDigits()
    {
        var generator = new CustomerOrderCodeGenerator();

        Assert.That(
            Enumerable.Range(0, 100).Select(_ => generator.Generate()),
            Is.All.Match("^[0-9]{8}$"));
    }

    [Test]
    public void Model_IsNormalizedAndProtectsOrderIdentityAfterSave()
    {
        using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=metadata;Username=metadata;Password=metadata")
            .Options);
        var customer = database.Model.FindEntityType(typeof(Customer))!;
        var order = database.Model.FindEntityType(typeof(Order))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(typeof(Order).GetProperty("OrderNumber"), Is.Null);
            Assert.That(customer.FindProperty(nameof(Customer.OrderCode))!.GetAfterSaveBehavior(),
                Is.EqualTo(PropertySaveBehavior.Save));
            Assert.That(order.GetTableName(), Is.EqualTo("orders"));
            Assert.That(order.FindProperty(nameof(Order.CustomerId))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(order.FindProperty(nameof(Order.CustomerOrderNumber))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(order.FindProperty(nameof(Order.SourceUrl))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(order.FindProperty(nameof(Order.Quantity))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Save));
            Assert.That(order.FindProperty(nameof(Order.Comment))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Save));
            Assert.That(order.FindProperty(nameof(Order.SellerPrice))!.GetPrecision(), Is.EqualTo(10));
            Assert.That(order.FindProperty(nameof(Order.SellerPrice))!.GetScale(), Is.EqualTo(2));
            Assert.That(order.FindProperty(nameof(Order.LengthCm))!.GetPrecision(), Is.EqualTo(10));
            Assert.That(order.FindProperty(nameof(Order.LengthCm))!.GetScale(), Is.EqualTo(2));
            Assert.That(order.FindProperty(nameof(Order.Characteristics))!.GetColumnType(), Is.EqualTo("jsonb"));
            Assert.That(order.FindProperty(nameof(Order.CreationIdempotencyKey))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(order.FindProperty(nameof(Order.CreatedAt))!.GetColumnName(), Is.EqualTo("created_at"));
            Assert.That(order.FindProperty(nameof(Order.CreatedAt))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(order.FindProperty(nameof(Order.UpdatedAt))!.GetColumnName(), Is.EqualTo("updated_at"));
            Assert.That(order.FindProperty(nameof(Order.UpdatedAt))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Save));
            Assert.That(order.GetIndexes().Count(index => index.IsUnique), Is.EqualTo(2));
            Assert.That(order.GetIndexes().Select(index => index.GetDatabaseName()), Does.Contain("ix_orders_created_at_id"));
            Assert.That(order.GetIndexes().Select(index => index.GetDatabaseName()), Does.Contain("ix_orders_status_created_at_id"));
            Assert.That(order.GetIndexes().Select(index => index.GetDatabaseName()), Does.Contain("ix_orders_updated_at_id"));
            Assert.That(order.GetForeignKeys(), Has.Count.EqualTo(2));
            Assert.That(order.GetForeignKeys(), Has.All.Property(nameof(IMutableForeignKey.DeleteBehavior)).EqualTo(DeleteBehavior.Restrict));
        }
    }

    [Test]
    public void CharacteristicsMapping_RoundTripsAndComparesDictionaryValues()
    {
        using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=metadata;Username=unused;Password=unused")
            .Options);
        var property = database.Model.FindEntityType(typeof(Order))!
            .FindProperty(nameof(Order.Characteristics))!;
        var converter = property.GetValueConverter()!;
        var comparer = property.GetValueComparer()!;
        var characteristics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["size"] = "M",
            ["color"] = "blue"
        };
        var equivalent = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["color"] = "blue",
            ["size"] = "M"
        };

        var json = (string)converter.ConvertToProvider(characteristics)!;
        var roundTrip = (Dictionary<string, string>)converter.ConvertFromProvider(json)!;
        var snapshot = (Dictionary<string, string>)comparer.Snapshot(characteristics)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTrip, Is.EqualTo(characteristics));
            Assert.That(converter.ConvertToProvider(null), Is.Null);
            Assert.That(comparer.Equals(characteristics, equivalent), Is.True);
            Assert.That(comparer.Equals(characteristics, characteristics), Is.True);
            Assert.That(comparer.Equals(characteristics, new Dictionary<string, string> { ["size"] = "L" }), Is.False);
            Assert.That(comparer.Equals(characteristics, new Dictionary<string, string>()), Is.False);
            Assert.That(comparer.Equals(characteristics, null), Is.False);
            Assert.That(comparer.GetHashCode(characteristics), Is.EqualTo(comparer.GetHashCode(equivalent)));
            Assert.That(comparer.GetHashCode(null), Is.Zero);
            Assert.That(snapshot, Is.EqualTo(characteristics));
            Assert.That(snapshot, Is.Not.SameAs(characteristics));
            Assert.That(comparer.Snapshot(null), Is.Null);
        }
    }

    [Test]
    public async Task Persistence_AllowsInitialOrderCodeAssignmentAndRejectsReplacement()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var customer = new Customer { Phone = "+79991234567" };
        database.Customers.Add(customer);
        await database.SaveChangesAsync();

        customer.AllocateOrderNumber("00000000");
        await database.SaveChangesAsync();
        database.Entry(customer).Property(item => item.OrderCode).CurrentValue = "99999999";

        var exception = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await database.SaveChangesAsync());

        Assert.That(exception!.Message, Is.EqualTo("A customer's assigned order code is immutable."));
    }

    [Test]
    public void Persistence_SynchronousSaveAlsoRejectsOrderCodeReplacement()
    {
        using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var customer = new Customer { Phone = "+79991234567" };
        database.Customers.Add(customer);
        database.SaveChanges();
        customer.AllocateOrderNumber("00000000");
        database.SaveChanges();
        database.Entry(customer).Property(item => item.OrderCode).CurrentValue = "99999999";

        var exception = Assert.Throws<InvalidOperationException>(() => database.SaveChanges());

        Assert.That(exception!.Message, Is.EqualTo("A customer's assigned order code is immutable."));
    }

    [Test]
    public void InMemoryCollisionDetector_DoesNotClassifyUpdateExceptionsAsCodeCollisions()
    {
        using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var detector = new CustomerOrderCodeCollisionDetector(database);

        Assert.That(detector.IsCollision(new DbUpdateException()), Is.False);
    }
}
