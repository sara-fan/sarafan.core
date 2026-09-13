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
        var order = database.Model.FindEntityType(typeof(Order))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(typeof(Order).GetProperty("OrderNumber"), Is.Null);
            Assert.That(order.GetTableName(), Is.EqualTo("orders"));
            Assert.That(order.FindProperty(nameof(Order.CustomerId))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(order.FindProperty(nameof(Order.CustomerOrderNumber))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(order.FindProperty(nameof(Order.SourceUrl))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(order.FindProperty(nameof(Order.CreationIdempotencyKey))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(order.GetIndexes().Count(index => index.IsUnique), Is.EqualTo(2));
            Assert.That(order.GetForeignKeys().Single().DeleteBehavior, Is.EqualTo(DeleteBehavior.Restrict));
        }
    }
}
