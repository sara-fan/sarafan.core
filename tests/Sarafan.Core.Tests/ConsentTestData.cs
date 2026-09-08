// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application
using System.Net.Http.Json;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Tests;

internal static class ConsentTestData
{
    private static readonly SemaphoreSlim CookieLock = new(1, 1);
    private static string? _mandatoryCookie;

    internal static async Task AcceptMandatoryCookies(HttpClient client)
    {
        await CookieLock.WaitAsync();
        try
        {
            if (_mandatoryCookie is null)
            {
                var document = (await client.GetFromJsonAsync<CurrentDocumentDto>(
                    $"/api/v1/legal/current/{(int)LegalDocumentKind.CookieConsent}"))!.Document!;
                using var response = await client.PostAsJsonAsync("/api/v1/consents/cookies", new ConsentDecisionRequest
                {
                    DocumentId = document.Id,
                    ContentHash = document.ContentHash,
                    Decision = "grant",
                    Categories = [CookieCategory.Mandatory],
                    IdempotencyKey = Guid.NewGuid()
                });
                response.EnsureSuccessStatusCode();
                _mandatoryCookie = response.Headers.GetValues("Set-Cookie")
                    .Single(value => value.StartsWith("sarafan.consent-browser=", StringComparison.Ordinal))
                    .Split(';', 2)[0];
            }

            SetCookie(client, _mandatoryCookie!);
        }
        finally
        {
            CookieLock.Release();
        }
    }

    private static void SetCookie(HttpClient client, string browserCookie)
    {
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", browserCookie);
    }

    internal static async Task<RequestCodeRequest> Request(HttpClient client, string phone)
    {
        var terms = (await client.GetFromJsonAsync<CurrentDocumentDto>($"/api/v1/legal/current/{(int)LegalDocumentKind.UserAgreement}"))!.Document!;
        var pd = (await client.GetFromJsonAsync<CurrentDocumentDto>($"/api/v1/legal/current/{(int)LegalDocumentKind.PersonalDataConsent}"))!.Document!;
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
