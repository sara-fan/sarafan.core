// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;

namespace Sarafan.Core.Services;

public sealed class ServiceException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public Guid? RequiredDocumentId { get; init; }
    public LegalDocumentKind? ConsentKind { get; init; }
    public string Code { get; } = code;
}
