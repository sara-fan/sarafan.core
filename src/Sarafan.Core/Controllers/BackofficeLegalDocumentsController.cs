// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sarafan.Core.Authentication;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Authorize(Policy = BackofficePolicies.ManageLegalDocuments), Route("api/v1/backoffice/legal-documents")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(400 * 1024)]
public sealed class BackofficeLegalDocumentsController(LegalDocumentService documents, SarafanProblemDetailsFactory problems) : SarafanControllerBase(problems)
{
    [HttpGet]
    public async Task<ActionResult> List([FromQuery] string? kind, CancellationToken token) => Ok(await documents.ListAsync(kind, token));
    [HttpGet("{id:guid}")]
    public async Task<ActionResult> Read(Guid id, CancellationToken token) => Ok(await documents.ReadAsync(id, true, token));
    [HttpPost]
    public async Task<ActionResult> Create(LegalDocumentRequest request, CancellationToken token) => Ok(await documents.SaveAsync(null, request, CurrentBackofficeUserId(), token));
    [HttpPut("{id:guid}")]
    public async Task<ActionResult> Update(Guid id, LegalDocumentRequest request, CancellationToken token) => Ok(await documents.SaveAsync(id, request, CurrentBackofficeUserId(), token));
    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult> Publish(Guid id, PublishDocumentRequest request, CancellationToken token) => Ok(await documents.PublishAsync(id, request, CurrentBackofficeUserId(), token));
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult> Cancel(Guid id, DocumentRevisionRequest request, CancellationToken token) => Ok(await documents.CancelAsync(id, request.Revision, CurrentBackofficeUserId(), token));
    [HttpGet("{id:guid}/audit")]
    public async Task<ActionResult> Audit(Guid id, CancellationToken token) => Ok(await documents.AuditAsync(id, token));
    [HttpGet("{id:guid}/source")]
    public async Task<ActionResult> Download(Guid id, CancellationToken token)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(await documents.DownloadAsync(id, true, token), "text/markdown; charset=utf-8", $"consent-{id}.md");
    }
}
