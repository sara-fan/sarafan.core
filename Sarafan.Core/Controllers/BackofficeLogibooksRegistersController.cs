// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;
namespace Sarafan.Core.Controllers;

[Authorize(Policy = BackofficePolicies.ManageLogibooksRegisters)]
[Route("api/v1/backoffice/logibooks-registers")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BackofficeLogibooksRegistersController(LogibooksRegisterService registers,
    SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    private string[] Roles => User.FindAll("role").Select(x => x.Value).ToArray();

    [HttpGet("ops")]
    public async Task<ActionResult<LogibooksOpsDto>> Operations(CancellationToken token)
        => Ok(await registers.OperationsAsync(Roles, token));

    [HttpPost("preview")]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<ActionResult<LogibooksPreviewDto>> Preview(LogibooksPreviewRequest request, CancellationToken token)
        => Ok(await registers.PreviewAsync(request, Roles, token));

    [HttpPost]
    [RequestSizeLimit(1024 * 1024)]
    [ProducesResponseType<LogibooksOperationDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<LogibooksOperationDto>> Create(LogibooksCreateRequest request, CancellationToken token)
    {
        var operation = await registers.CreateAsync(request, CurrentBackofficeUserId(), Roles, token);
        return AcceptedAtAction(nameof(Get), new { id = operation.Id }, operation);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<LogibooksOperationDto>>> List([FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, [FromQuery] string? search = null, [FromQuery] string? state = null,
        [FromQuery] string sortBy = "createdAt", [FromQuery] string sortOrder = "desc", CancellationToken token = default)
        => Ok(await registers.ListAsync(page, pageSize, search, state, sortBy, sortOrder, Roles, token));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<LogibooksOperationDto>> Get(Guid id, CancellationToken token)
        => Ok(await registers.GetAsync(id, Roles, token));

    [HttpGet("{id:guid}/rows")]
    public async Task<ActionResult<PagedResult<LogibooksRowDto>>> Rows(Guid id, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, [FromQuery] string? search = null, CancellationToken token = default)
        => Ok(await registers.RowsAsync(id, page, pageSize, search, Roles, token));

    [HttpGet("{id:guid}/details")]
    public async Task<ActionResult<LogibooksMetadata>> Details(Guid id, CancellationToken token)
        => Ok(await registers.DetailsAsync(id, Roles, token));

    [HttpPost("{id:guid}/reconcile")]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<ActionResult<LogibooksOperationDto>> Reconcile(Guid id, LogibooksReconcileRequest request, CancellationToken token)
        => Ok(await registers.ReconcileAsync(id, request, CurrentBackofficeUserId(), Roles, token));
}
