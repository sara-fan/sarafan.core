// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;

namespace Sarafan.Core.Services;

public sealed record IanaTldDownload(
    string Version,
    DateTimeOffset SourceUpdatedAt,
    string ContentSha256,
    IReadOnlyList<string> TopLevelDomains);

public sealed class IanaTldCatalogSnapshot
{
    internal IanaTldCatalogSnapshot(string version, string[] topLevelDomains)
    {
        Version = version;
        TopLevelDomains = Array.AsReadOnly(topLevelDomains);
        Values = topLevelDomains.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public string Version { get; }
    public IReadOnlyList<string> TopLevelDomains { get; }
    internal FrozenSet<string> Values { get; }
}

public enum IanaTldUpdateResult
{
    Updated,
    Unchanged,
    OlderIgnored
}

public interface IIanaTldClient
{
    Task<IanaTldDownload> GetAsync(CancellationToken cancellationToken);
}

public interface IIanaTldSynchronizer
{
    Task<IanaTldUpdateResult> SynchronizeAsync(CancellationToken cancellationToken);
}

public sealed partial class IanaTldClient(HttpClient httpClient, ILogger<IanaTldClient> logger) : IIanaTldClient
{
    public const string Endpoint = "https://data.iana.org/TLD/tlds-alpha-by-domain.txt";
    internal const int MaximumResponseBytes = 1_048_576;
    internal const int MinimumEntryCount = 1_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<IanaTldDownload> GetAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(IanaTldClient).FullName}.{nameof(GetAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)),
            async () => Parse(await httpClient.GetByteArrayAsync(Endpoint, cancellationToken)),
            cancellationToken);

    internal static IanaTldDownload Parse(byte[] data)
    {
        if (data.Length == 0 || data.Length > MaximumResponseBytes)
        {
            throw new InvalidDataException("IANA TLD response size is invalid.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("IANA TLD response is not valid UTF-8.", exception);
        }

        if (!text.EndsWith('\n') || text.StartsWith('\uFEFF'))
        {
            throw new InvalidDataException("IANA TLD response is incomplete or has a byte-order mark.");
        }

        var lines = text.Split('\n');
        var header = lines[0].TrimEnd('\r');
        var match = HeaderPattern().Match(header);
        if (!match.Success || !DateTimeOffset.TryParseExact(
                match.Groups["updated"].Value,
                "ddd MMM dd HH:mm:ss yyyy 'UTC'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var sourceUpdatedAt))
        {
            throw new InvalidDataException("IANA TLD response header is invalid.");
        }

        var values = new List<string>(lines.Length - 2);
        string? previous = null;
        foreach (var rawLine in lines.Skip(1).Take(lines.Length - 2))
        {
            var value = rawLine.TrimEnd('\r');
            if (!TopLevelDomainPattern().IsMatch(value)
                || previous is not null && string.CompareOrdinal(previous, value) >= 0)
            {
                throw new InvalidDataException("IANA TLD response contains invalid or unordered entries.");
            }

            values.Add(value);
            previous = value;
        }

        if (values.Count < MinimumEntryCount || !values.Contains("COM", StringComparer.Ordinal))
        {
            throw new InvalidDataException("IANA TLD response appears truncated.");
        }

        return new IanaTldDownload(
            match.Groups["version"].Value,
            sourceUpdatedAt,
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            values.AsReadOnly());
    }

    [GeneratedRegex("\\A# Version (?<version>[0-9]{10}), Last Updated (?<updated>[A-Z][a-z]{2} [A-Z][a-z]{2} [0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} [0-9]{4} UTC)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex("\\A[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TopLevelDomainPattern();
}

public sealed class IanaTldCatalogService(
    AppDbContext database,
    IIanaTldClient client,
    TimeProvider timeProvider,
    ILogger<IanaTldCatalogService> logger) : IIanaTldSynchronizer
{
    public Task<IanaTldCatalogSnapshot> GetRequiredAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(IanaTldCatalogService).FullName}.{nameof(GetRequiredAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)),
            async () =>
            {
                var current = await database.IanaTldCatalog.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == IanaTldCatalog.SingletonId, cancellationToken);
                return current is null
                    ? throw new ServiceException(StatusCodes.Status503ServiceUnavailable, "tld_catalog_unavailable")
                    : new IanaTldCatalogSnapshot(current.Version, [.. current.TopLevelDomains]);
            },
            cancellationToken);

    public Task<IanaTldUpdateResult> SynchronizeAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(IanaTldCatalogService).FullName}.{nameof(SynchronizeAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)),
            () => SynchronizeCoreAsync(cancellationToken),
            cancellationToken);

    private async Task<IanaTldUpdateResult> SynchronizeCoreAsync(CancellationToken cancellationToken)
    {
        SarafanEvents.IanaTldUpdateStarted(logger);
        var download = await client.GetAsync(cancellationToken);
        var retrievedAt = timeProvider.GetUtcNow();
        var operations = AppDatabaseOperations.For(database);
        await using var transaction = await operations.BeginTransactionAsync(database, cancellationToken);
        try
        {
            await operations.LockIanaTldCatalogAsync(database, cancellationToken);
            var current = await database.IanaTldCatalog
                .SingleOrDefaultAsync(item => item.Id == IanaTldCatalog.SingletonId, cancellationToken);
            var result = Compare(current, download);
            if (result == IanaTldUpdateResult.Updated)
            {
                if (current is null)
                {
                    current = new IanaTldCatalog
                    {
                        Version = download.Version,
                        Source = IanaTldClient.Endpoint,
                        SourceUpdatedAt = download.SourceUpdatedAt,
                        RetrievedAt = retrievedAt,
                        ContentSha256 = download.ContentSha256,
                        TopLevelDomains = [.. download.TopLevelDomains]
                    };
                    database.IanaTldCatalog.Add(current);
                }
                else
                {
                    current.Version = download.Version;
                    current.Source = IanaTldClient.Endpoint;
                    current.SourceUpdatedAt = download.SourceUpdatedAt;
                    current.RetrievedAt = retrievedAt;
                    current.ContentSha256 = download.ContentSha256;
                    current.TopLevelDomains = [.. download.TopLevelDomains];
                }

                await database.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            SarafanEvents.IanaTldUpdateCompleted(
                logger,
                download.Version,
                download.TopLevelDomains.Count,
                result == IanaTldUpdateResult.Updated);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static IanaTldUpdateResult Compare(IanaTldCatalog? current, IanaTldDownload download)
    {
        if (current is null || string.CompareOrdinal(download.Version, current.Version) > 0)
        {
            return IanaTldUpdateResult.Updated;
        }

        if (string.Equals(download.Version, current.Version, StringComparison.Ordinal))
        {
            if (!string.Equals(download.ContentSha256, current.ContentSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("IANA returned different content for the current version.");
            }

            return IanaTldUpdateResult.Unchanged;
        }

        return IanaTldUpdateResult.OlderIgnored;
    }
}

internal static class IanaTopLevelDomainRules
{
    internal static bool HasValidSuffix(string host, IReadOnlySet<string> values)
    {
        if (host.EndsWith("..", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = host.EndsWith('.') ? host[..^1] : host;
        var labels = normalized.Split('.');
        return normalized.Length <= 253
            && labels.Length > 1
            && labels.All(IsValidLabel)
            && values.Contains(labels[^1]);
    }

    private static bool IsValidLabel(string label)
        => label.Length is > 0 and <= 63
            && IsAsciiLetterOrDigit(label[0])
            && IsAsciiLetterOrDigit(label[^1])
            && label.All(value => IsAsciiLetterOrDigit(value) || value is '-');

    private static bool IsAsciiLetterOrDigit(char value)
        => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
