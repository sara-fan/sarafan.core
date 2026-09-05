// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.Extensions.Options;

using Sarafan.Core.Observability;

namespace Sarafan.Core.Authentication;

public interface IBackofficePasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public sealed class BCryptBackofficePasswordHasher(
    IOptions<BackofficeAuthenticationOptions> options,
    ILogger<BCryptBackofficePasswordHasher> logger) : IBackofficePasswordHasher
{
    private readonly int _workFactor = options.Value.BCryptWorkFactor;

    public string Hash(string password)
        => OperationLogging.Run(
            logger,
            $"{typeof(BCryptBackofficePasswordHasher).FullName}.{nameof(Hash)}",
            () => LogValueSummary.Inputs((nameof(password), password)),
            () => BCrypt.Net.BCrypt.HashPassword(password, _workFactor));

    public bool Verify(string password, string hash)
        => OperationLogging.Run(
            logger,
            $"{typeof(BCryptBackofficePasswordHasher).FullName}.{nameof(Verify)}",
            () => LogValueSummary.Inputs((nameof(password), password), (nameof(hash), hash)),
            () => BCrypt.Net.BCrypt.Verify(password, hash));
}
