// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public sealed class Order
{
    private Order()
    {
    }

    internal Order(int customerId, long customerOrderNumber, string sourceUrl, Guid creationIdempotencyKey)
    {
        CustomerId = customerId;
        CustomerOrderNumber = customerOrderNumber;
        SourceUrl = sourceUrl;
        CreationIdempotencyKey = creationIdempotencyKey;
    }

    public long Id { get; private set; }
    public int CustomerId { get; private set; }
    public long CustomerOrderNumber { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.UnderReview;
    public string SourceUrl { get; private set; } = string.Empty;
    internal Guid CreationIdempotencyKey { get; private set; }

    public Customer Customer { get; private set; } = null!;
}
