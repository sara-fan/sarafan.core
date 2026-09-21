// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;
using Sarafan.Core.Authentication;

namespace Sarafan.Core.Services;

public sealed class ServiceException(int statusCode, string code) : Exception(code)
{
    public int StatusCode { get; } = statusCode;
    public PhoneValidationReason? PhoneValidationReason { get; init; }
    public Guid? RequiredDocumentId { get; init; }
    public LegalDocumentKind? ConsentKind { get; init; }
    public AuthenticationFlowStep? NextStep { get; init; }
    public IReadOnlyList<LegalDocumentKind>? RequiredDocumentKinds { get; init; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
    public string Code { get; } = code;
}
