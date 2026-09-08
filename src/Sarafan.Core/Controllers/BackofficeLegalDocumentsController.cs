// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[CookieConsentNotRequired, Authorize(Policy = BackofficePolicies.ManageLegalDocuments), Route("api/v1/backoffice/legal-documents")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(400 * 1024)]
public sealed class BackofficeLegalDocumentsController(LegalDocumentService documents, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet]
    public async Task<ActionResult> List([FromQuery] LegalDocumentKind? kind, CancellationToken token) => Ok(await documents.ListAsync(kind, token));
    [HttpGet("ops")]
    public ActionResult Operations() => Ok(LegalDocumentService.Operations());
    [HttpGet("{id:guid}")]
    public async Task<ActionResult> Read(Guid id, CancellationToken token) => Ok(await documents.ReadAsync(id, true, token));
    [HttpPost("preview")]
    public async Task<ActionResult> Preview(LegalDocumentPreviewRequest request, CancellationToken token) => Ok(await documents.PreviewAsync(request, token));
    [HttpPost]
    public async Task<ActionResult> Create(LegalDocumentRequest request, CancellationToken token)
    {
        var document = await documents.CreateAsync(request, CurrentBackofficeUserId(), token);
        return CreatedAtAction(nameof(Read), new { id = document.Id }, document);
    }
    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken token)
    {
        await documents.DeleteAsync(id, CurrentBackofficeUserId(), token);
        return NoContent();
    }
    [HttpGet("audit")]
    public async Task<ActionResult> Audit([FromQuery] LegalDocumentKind? kind, [FromQuery] string? action,
        [FromQuery] string? search, [FromQuery] Guid? documentId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken token = default) => Ok(await documents.AuditAsync(kind, action, search, documentId, page, pageSize, token));
    [HttpGet("{id:guid}/source")]
    public async Task<ActionResult> Download(Guid id, CancellationToken token)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(await documents.DownloadAsync(id, true, token), "text/markdown; charset=utf-8", $"consent-{id}.md");
    }
}
