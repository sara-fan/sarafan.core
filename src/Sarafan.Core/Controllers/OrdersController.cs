// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Authorize]
[Route("api/v1/orders")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OrdersController(
    OrderService orders,
    SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CustomerOrderListItemDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CustomerOrderListItemDto>>> List(
        CancellationToken cancellationToken)
        => Ok(await orders.ListAsync(CurrentCustomerId(), cancellationToken));

    [HttpGet("{id:long}")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OrderDto>> Get(
        long id,
        CancellationToken cancellationToken)
        => Ok(await orders.GetAsync(CurrentCustomerId(), id, cancellationToken));

    [HttpPost]
    [ServiceFilter(typeof(PersonalDataConsentFilter))]
    [ProducesResponseType<OrderDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<OrderDto>> Create(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CreateOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(idempotencyKey, "D", out var parsedKey) || parsedKey == Guid.Empty)
        {
            return InvalidOrderIdempotencyKeyProblem();
        }

        var order = await orders.CreateAsync(
            CurrentCustomerId(),
            request.SourceUrl,
            request.Quantity,
            request.Comment,
            parsedKey,
            cancellationToken,
            request.SubmittedProduct);
        return CreatedAtAction(nameof(Get), new { id = order.Id }, order);
    }
}
