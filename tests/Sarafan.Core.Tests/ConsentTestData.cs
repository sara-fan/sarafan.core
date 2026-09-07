// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application
using System.Net.Http.Json;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Tests;

internal static class ConsentTestData
{
    internal static async Task<RequestCodeRequest> Request(HttpClient client, string phone)
    {
        var terms = (await client.GetFromJsonAsync<CurrentDocumentDto>("/api/v1/legal/current/user-agreement"))!.Document!;
        var pd = (await client.GetFromJsonAsync<CurrentDocumentDto>("/api/v1/legal/current/personal-data-consent"))!.Document!;
        return new RequestCodeRequest
        {
            Phone = phone,
            Purpose = "register",
            TermsAccepted = true,
            TermsDocumentId = terms.Id,
            PersonalDataConsent = new() { Decision = "grant", DocumentId = pd.Id, ContentHash = pd.ContentHash, IdempotencyKey = Guid.NewGuid() }
        };
    }
    internal static async Task<string> Onboarding(HttpClient client, string phone)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/code/request", await Request(client, phone));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CodeRequestDto>())!.OnboardingToken!;
    }
}
