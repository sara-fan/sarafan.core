// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[AllowAnonymous]
[Route("api/v1/stores")]
[ResponseCache(Location = ResponseCacheLocation.None)]
public sealed class StoresController(StoreService stores, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet]
    public async Task<ActionResult<StoreListDto<PublicStoreDto>>> List(CancellationToken token)
    {
        var sort = "recommended";
        string? search = null;
        if (Request.Query.TryGetValue("sort", out var values))
        {
            if (values.Count != 1) throw new ServiceException(400, "invalid_store_sort");
            sort = values[0] ?? "";
        }
        if (Request.Query.TryGetValue("search", out values))
        {
            if (values.Count != 1) throw new ServiceException(400, "invalid_store_search");
            search = values[0];
        }
        return Ok(await stores.ListPublicAsync(sort, search, false, token));
    }

    [HttpGet("featured")]
    public async Task<ActionResult<StoreListDto<PublicStoreDto>>> Featured(CancellationToken token)
    {
        if (Request.Query.ContainsKey("search")) throw new ServiceException(400, "invalid_store_search");
        return Ok(await stores.ListPublicAsync("recommended", null, true, token));
    }

    [HttpGet("{id:int}/logo")]
    public async Task<ActionResult> Logo(int id, CancellationToken token)
    {
        // Always check current visibility before File evaluates conditional request headers.
        var logo = await stores.GetLogoAsync(id, null, token);
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(logo.Content, logo.ContentType, lastModified: null,
            entityTag: new EntityTagHeaderValue($"\"{logo.ContentSha256}\""));
    }
}
