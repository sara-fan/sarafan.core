// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;

namespace Sarafan.Core.Services;

public sealed class BackofficeBootstrapService(
    AppDbContext database,
    IBackofficePasswordHasher passwordHasher,
    IOptions<BackofficeBootstrapOptions> options,
    TimeProvider timeProvider,
    ILogger<BackofficeBootstrapService> logger)
{
    private readonly BackofficeBootstrapOptions _options = options.Value;

    public Task ProvisionAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeBootstrapService).FullName}.{nameof(ProvisionAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)),
            () => ProvisionCoreAsync(cancellationToken),
            cancellationToken);

    public Task EnsureReleaseGateAsync(CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(BackofficeBootstrapService).FullName}.{nameof(EnsureReleaseGateAsync)}",
            () => LogValueSummary.Inputs((nameof(cancellationToken), cancellationToken)),
            () => EnsureReleaseGateCoreAsync(cancellationToken),
            cancellationToken);

    private async Task ProvisionCoreAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        _options.Validate();
        var email = _options.Email.Trim();
        var normalizedEmail = email.ToLowerInvariant();
        if (await database.BackofficeUsers.AnyAsync(
                item => item.NormalizedEmail == normalizedEmail,
                cancellationToken))
        {
            return;
        }

        if (await database.BackofficeUsers.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "Back-office bootstrap can only create the first back-office user; disable it after provisioning");
        }

        var now = timeProvider.GetUtcNow();
        database.BackofficeUsers.Add(new BackofficeUser
        {
            Email = email,
            NormalizedEmail = normalizedEmail,
            FirstName = _options.FirstName.Trim(),
            LastName = _options.LastName.Trim(),
            PasswordHash = passwordHasher.Hash(_options.Password),
            IsDemo = true,
            CreatedAt = now,
            UpdatedAt = now,
            UserRoles =
            [
                new BackofficeUserRole { RoleCode = BackofficeRoles.Administrator }
            ]
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureReleaseGateCoreAsync(CancellationToken cancellationToken)
    {
        if (!_options.RealOrdersEnabled && !_options.RealPaymentIntegrationEnabled)
        {
            return;
        }

        if (await database.BackofficeUsers.AnyAsync(
                item => item.IsDemo && item.IsActive,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "An active demo back-office account is forbidden with real orders or real payment integration");
        }
    }
}
