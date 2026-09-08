// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.Models;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[AllowAnonymous, CookieConsentNotRequired, Route("api/v1/legal")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LegalDocumentsController(LegalDocumentService documents, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet("ops")]
    public ActionResult Operations() => Ok(LegalDocumentService.Operations());
    [HttpGet("current/{kind:int}")]
    public async Task<ActionResult> Current(LegalDocumentKind kind, CancellationToken token) => Ok(await documents.CurrentAsync(kind, token));
    [HttpGet("documents/{id:guid}")]
    public async Task<ActionResult> Read(Guid id, CancellationToken token) => Ok(await documents.ReadAsync(id, false, token));
    [HttpGet("documents/{id:guid}/source")]
    public async Task<ActionResult> Download(Guid id, CancellationToken token)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(await documents.DownloadAsync(id, false, token), "text/markdown; charset=utf-8", $"consent-{id}.md");
    }
}
