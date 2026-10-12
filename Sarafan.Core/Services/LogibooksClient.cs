// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public interface ILogibooksClient
{
    Task<LogibooksRemoteReceipt> CreateAsync(string target, string payload, Func<Task> authenticationRetry, CancellationToken token);
}

public sealed class LogibooksClient(HttpClient http, IOptionsMonitor<LogibooksOptions> options,
    ILogger<LogibooksClient> logger) : ILogibooksClient
{
    public Task<LogibooksRemoteReceipt> CreateAsync(string target, string payload, Func<Task> authenticationRetry, CancellationToken token)
        => OperationLogging.RunAsync(logger, $"{typeof(LogibooksClient).FullName}.{nameof(CreateAsync)}",
            () => LogValueSummary.Inputs((nameof(target), target), (nameof(payload), payload),
                (nameof(authenticationRetry), authenticationRetry), (nameof(token), token)), async () =>
        {
            try
            {
                var configuration = options.CurrentValue;
                var goods = LogibooksMapping.Read<LogibooksPayload>(payload).Parcels;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var jwt = await LoginAsync(target, configuration, token);
                    if (jwt is null) return new(null, true, "logibooks_auth_failed");
                    using var request = new HttpRequestMessage(HttpMethod.Post, target + "/api/registers/gtc");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
                    request.Headers.Accept.Add(new("application/json"));
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    var body = await ReadAsync(response, configuration.ResponseBytes, token);
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    if (response.StatusCode == HttpStatusCode.Created)
                    {
                        var result = LogibooksMapping.Read<LogibooksRemoteResult>(body);
                        return Valid(result, goods, true) ? new(result, true, null) : Unknown();
                    }
                    if (!Envelope(root)) return Unknown();
                    if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                    {
                        await authenticationRetry();
                        continue;
                    }
                    if ((int)response.StatusCode == 422 && root.TryGetProperty("details", out var details))
                    {
                        var result = details.Deserialize<LogibooksRemoteResult>(LogibooksMapping.Json);
                        return result is not null && Valid(result, goods, false)
                            ? new(result, true, "logibooks_rejected") : Unknown();
                    }
                    if ((int)response.StatusCode is 400 or 401 or 403 or 404 or 409 or 415)
                        return new(null, true, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                            ? "logibooks_auth_failed" : "logibooks_rejected");
                    return Unknown();
                }
                return Unknown();
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException or FormatException)
            {
                return Unknown();
            }
        }, token);

    private async Task<string?> LoginAsync(string target, LogibooksOptions configuration, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, target + "/api/auth/login")
        {
            Content = new StringContent(LogibooksMapping.Serialize(new { email = configuration.Identifier, password = configuration.Password }),
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        var body = await ReadAsync(response, configuration.ResponseBytes, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("token", out var jwt) || jwt.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(jwt.GetString())) throw new InvalidDataException();
        return jwt.GetString();
    }

    private static bool Envelope(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(msg.GetString())
        && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object;

    internal static bool Valid(LogibooksRemoteResult result, LogibooksGoods[] goods, bool success)
    {
        if (result.Rows is null || result.Processed != goods.Length || result.Rows.Length != goods.Length
            || result.Created < 0 || result.Failed < 0 || result.Created + result.Failed != goods.Length
            || (success ? result.RegisterId is not > 0 || result.Created == 0 : result.RegisterId is not null || result.Created != 0))
            return false;
        var parcels = new HashSet<int>();
        var created = 0;
        for (var i = 0; i < goods.Length; i++)
        {
            var row = result.Rows[i];
            if (row is null || row.RowNumber != i + 1 || row.PostingNumber is not null && row.PostingNumber != goods[i].PostingNumber
                || row.Issues is null || row.Issues.Any(x => x is null || string.IsNullOrWhiteSpace(x.Code) || string.IsNullOrWhiteSpace(x.Msg)))
                return false;
            if (row.Outcome == "Created")
            {
                if (row.ParcelId is not > 0 || row.StatusId is null || row.CheckStatus is null || !parcels.Add(row.ParcelId.Value)) return false;
                created++;
            }
            else if (row.Outcome is not ("Invalid" or "NotCreated") || row.ParcelId is not null || row.StatusId is not null || row.CheckStatus is not null)
                return false;
        }
        return created == result.Created;
    }

    private static LogibooksRemoteReceipt Unknown() => new(null, false, "logibooks_unknown");
    private static async Task<string> ReadAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json"
            || response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException();
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await source.ReadAsync(buffer, token)) != 0)
        {
            if (memory.Length + length > maximumBytes) throw new InvalidDataException();
            memory.Write(buffer, 0, length);
        }
        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
    }
}
