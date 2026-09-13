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
public sealed class OrdersController(
    OrderService orders,
    SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
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

        var order = await orders.CreateAsync(CurrentCustomerId(), request.SourceUrl, parsedKey, cancellationToken);
        return Created(string.Empty, order);
    }
}
