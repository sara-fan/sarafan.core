// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Tests;

[NonParallelizable]
public sealed class BackofficeSecurityTests
{
    [Test]
    public void RoleCatalogAndAuthorizationMatrix_AreFixedAndFailClosed()
    {
        Assert.That(
            BackofficeRoles.Definitions.Select(item => (item.Code, item.DisplayName)),
            Is.EquivalentTo(new[]
            {
                (BackofficeRoles.Administrator, "Administrator"),
                (BackofficeRoles.ShiftManager, "Shift manager"),
                (BackofficeRoles.SeniorOperator, "Senior operator"),
                (BackofficeRoles.Operator, "Operator")
            }));

        foreach (var role in BackofficeRoles.Codes)
        {
            Assert.That(BackofficeAuthorization.IsAllowed([role], BackofficeAction.Access), Is.True, role);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                BackofficeAuthorization.IsAllowed(
                    [BackofficeRoles.Administrator],
                    BackofficeAction.ManageUsers),
                Is.True);
            Assert.That(
                BackofficeAuthorization.IsAllowed(
                    [BackofficeRoles.Administrator],
                    BackofficeAction.ManageRoles),
                Is.True);
            Assert.That(
                BackofficeAuthorization.IsAllowed([BackofficeRoles.Operator], BackofficeAction.ManageUsers),
                Is.False);
            Assert.That(
                BackofficeAuthorization.IsAllowed(
                    [BackofficeRoles.ShiftManager],
                    BackofficeAction.OperationalQueue),
                Is.True);
            Assert.That(
                BackofficeAuthorization.IsAllowed(
                    [BackofficeRoles.Operator],
                    BackofficeAction.OperationalQueue),
                Is.False);
            Assert.That(
                BackofficeAuthorization.IsAllowed(
                    [BackofficeRoles.Operator],
                    BackofficeAction.ManualQuotes),
                Is.True);
            Assert.That(
                BackofficeAuthorization.IsAllowed(
                    [BackofficeRoles.Administrator],
                    BackofficeAction.ManageLegalDocuments),
                Is.True);
            Assert.That(
                BackofficeAuthorization.IsAllowed(["unknown"], BackofficeAction.Access),
                Is.False);
            Assert.That(
                BackofficeAuthorization.IsAllowed([], BackofficeAction.Access),
                Is.False);
            Assert.That(
                BackofficeAuthorization.IsAllowed(
                    [BackofficeRoles.Administrator],
                    (BackofficeAction)int.MaxValue),
                Is.False);
        }

        var options = new AuthorizationOptions();
        BackofficeAuthorization.Configure(options);
        var policyNames = new[]
        {
            BackofficePolicies.Access,
            BackofficePolicies.ManageUsers,
            BackofficePolicies.ManageRoles,
            BackofficePolicies.OperationalQueue,
            BackofficePolicies.ManualQuotes,
            BackofficePolicies.ManageLegalDocuments,
            BackofficePolicies.Administrator,
            BackofficePolicies.ShiftManager,
            BackofficePolicies.SeniorOperator,
            BackofficePolicies.Operator
        };
        Assert.That(policyNames.Select(options.GetPolicy), Is.All.Not.Null);
    }

    [Test]
    public void PasswordRules_EnforceCharacterLengthBoundary()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BackofficePasswordRules.IsValid(null), Is.False);
            Assert.That(BackofficePasswordRules.IsValid("        "), Is.False);
            Assert.That(BackofficePasswordRules.IsValid(new string('a', 7)), Is.False);
            Assert.That(BackofficePasswordRules.IsValid(new string('a', 8)), Is.True);
            Assert.That(BackofficePasswordRules.IsValid(new string('a', 18)), Is.True);
            Assert.That(BackofficePasswordRules.IsValid(new string('a', 19)), Is.False);
            Assert.That(BackofficePasswordRules.IsValid(new string('я', 18)), Is.True);
            Assert.That(BackofficePasswordRules.IsValid(new string('я', 19)), Is.False);
            Assert.That(
                Assert.Throws<InvalidOperationException>(() =>
                    BackofficePasswordRules.ValidateConfigurationPassword("short", "Test:Password"))?.Message,
                Does.StartWith("Test:Password"));
            Assert.DoesNotThrow(() => BackofficePasswordRules.ValidateConfigurationPassword(
                "Config_pass_13",
                "Test:Password"));
        }
    }

    [Test]
    public void BackofficeOptions_ValidateOnlyEnabledBootstrapAndRequiredSigningKey()
    {
        var authentication = new BackofficeAuthenticationOptions();
        Assert.Throws<InvalidOperationException>(authentication.Validate);
        authentication.SigningKey = new string('k', 32);
        Assert.DoesNotThrow(authentication.Validate);
        var customerAuthentication = new Sarafan.Core.Authentication.AuthenticationOptions();
        customerAuthentication.Issuer = authentication.Issuer;
        Assert.Throws<InvalidOperationException>(() =>
            authentication.ValidateDistinctFrom(customerAuthentication));
        customerAuthentication.Issuer = "customer-issuer";
        customerAuthentication.Audience = authentication.Audience;
        Assert.Throws<InvalidOperationException>(() =>
            authentication.ValidateDistinctFrom(customerAuthentication));
        customerAuthentication.Audience = "customer-audience";
        customerAuthentication.SigningKey = authentication.SigningKey;
        Assert.Throws<InvalidOperationException>(() =>
            authentication.ValidateDistinctFrom(customerAuthentication));
        customerAuthentication.SigningKey = new string('c', 32);
        customerAuthentication.RefreshCookieName = authentication.RefreshCookieName;
        Assert.Throws<InvalidOperationException>(() =>
            authentication.ValidateDistinctFrom(customerAuthentication));
        customerAuthentication.RefreshCookieName = "customer.refresh";
        Assert.DoesNotThrow(() => authentication.ValidateDistinctFrom(customerAuthentication));

        var bootstrap = new BackofficeBootstrapOptions();
        Assert.DoesNotThrow(bootstrap.Validate);
        bootstrap.Enabled = true;
        Assert.Throws<InvalidOperationException>(bootstrap.Validate);
        bootstrap.Email = "not-an-email";
        bootstrap.Password = "Bootstrap_pass_13";
        Assert.Throws<InvalidOperationException>(bootstrap.Validate);
        bootstrap.Email = "admin@sarafan.test";
        bootstrap.FirstName = " ";
        Assert.Throws<InvalidOperationException>(bootstrap.Validate);
        bootstrap.FirstName = "Maxim";
        Assert.DoesNotThrow(bootstrap.Validate);
        bootstrap.RealOrdersEnabled = true;
        Assert.Throws<InvalidOperationException>(bootstrap.Validate);
        bootstrap.RealOrdersEnabled = false;
        bootstrap.RealPaymentIntegrationEnabled = true;
        Assert.Throws<InvalidOperationException>(bootstrap.Validate);
    }

    [Test]
    public void BCryptHasher_ProducesNonPlaintextHashAndVerifiesIt()
    {
        var hasher = new BCryptBackofficePasswordHasher(
            Options.Create(new BackofficeAuthenticationOptions { BCryptWorkFactor = 10 }),
            NullLogger<BCryptBackofficePasswordHasher>.Instance);
        const string password = "Hashing_pass_13";
        var hash = hasher.Hash(password);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hash, Is.Not.EqualTo(password));
            Assert.That(hash, Does.StartWith("$2"));
            Assert.That(hasher.Verify(password, hash), Is.True);
            Assert.That(hasher.Verify("Wrong_hash_pass_13", hash), Is.False);
            Assert.Throws<BCrypt.Net.SaltParseException>(() => hasher.Verify(password, "not-a-bcrypt-hash"));
        }
    }

    [Test]
    public void BackofficeToken_ContainsOnlyBackofficeIdentityAndRoles()
    {
        var options = new BackofficeAuthenticationOptions
        {
            Issuer = "test-backoffice-issuer",
            Audience = "test-backoffice-audience",
            SigningKey = "test-backoffice-signing-key-with-32-characters",
            AccessTokenMinutes = 5
        };
        var service = new BackofficeJwtTokenService(
            Options.Create(options),
            TimeProvider.System,
            NullLogger<BackofficeJwtTokenService>.Instance);
        var user = new BackofficeUser
        {
            Id = 17,
            Email = "redacted@sarafan.test",
            NormalizedEmail = "redacted@sarafan.test",
            FirstName = "Test",
            LastName = "User",
            PasswordHash = "not-used",
            TokenVersion = 3,
            UserRoles =
            [
                new BackofficeUserRole { RoleCode = BackofficeRoles.Operator },
                new BackofficeUserRole { RoleCode = BackofficeRoles.SeniorOperator }
            ]
        };

        var result = service.CreateAccessToken(user);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);
        var validation = BackofficeJwtTokenService.CreateValidationParameters(
            options,
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(options.SigningKey)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(token.Subject, Is.EqualTo("17"));
            Assert.That(token.Issuer, Is.EqualTo(options.Issuer));
            Assert.That(token.Audiences, Does.Contain(options.Audience));
            Assert.That(token.Claims.Single(item => item.Type == "identity_type").Value, Is.EqualTo("backoffice"));
            Assert.That(token.Claims.Single(item => item.Type == "token_version").Value, Is.EqualTo("3"));
            Assert.That(token.Claims.Where(item => item.Type == "role").Select(item => item.Value),
                Is.EqualTo(new[] { BackofficeRoles.Operator, BackofficeRoles.SeniorOperator }));
            Assert.That(token.Claims.Any(item => item.Value == "customer"), Is.False);
            Assert.That(validation.ValidIssuer, Is.EqualTo(options.Issuer));
            Assert.That(validation.ValidAudience, Is.EqualTo(options.Audience));
            Assert.That(validation.ClockSkew, Is.EqualTo(TimeSpan.Zero));
        }
    }

    [Test]
    public void BackofficeDto_SortsRolesAndDoesNotExposePasswordOrTokenVersion()
    {
        var user = new BackofficeUser
        {
            Id = 9,
            Email = "user@sarafan.test",
            NormalizedEmail = "user@sarafan.test",
            FirstName = "Back",
            LastName = "Office",
            PasswordHash = "secret-hash",
            TokenVersion = 42,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            UserRoles =
            [
                new BackofficeUserRole { RoleCode = BackofficeRoles.ShiftManager },
                new BackofficeUserRole { RoleCode = BackofficeRoles.Operator }
            ]
        };

        var dto = BackofficeUserDto.From(user);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Roles, Is.EqualTo(new[] { BackofficeRoles.Operator, BackofficeRoles.ShiftManager }));
            Assert.That(typeof(BackofficeUserDto).GetProperty(nameof(BackofficeUser.PasswordHash)), Is.Null);
            Assert.That(typeof(BackofficeUserDto).GetProperty(nameof(BackofficeUser.TokenVersion)), Is.Null);
        }
    }

    [Test]
    public async Task BootstrapProvisioning_IsIdempotent()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var bootstrap = scope.ServiceProvider.GetRequiredService<BackofficeBootstrapService>();

        Assert.DoesNotThrowAsync(async () => await bootstrap.ProvisionAsync(default));
        Assert.DoesNotThrowAsync(async () => await bootstrap.ProvisionAsync(default));
        Assert.DoesNotThrowAsync(async () => await bootstrap.EnsureReleaseGateAsync(default));

        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IBackofficePasswordHasher>();
        var initial = await database.BackofficeUsers
            .AsNoTracking()
            .SingleAsync(item => item.NormalizedEmail == IntegrationTestEnvironment.BackofficeEmail);
        Assert.That(initial.IsDemo, Is.True);
        var disabled = new BackofficeBootstrapService(
            database,
            hasher,
            Options.Create(new BackofficeBootstrapOptions()),
            TimeProvider.System,
            NullLogger<BackofficeBootstrapService>.Instance);
        Assert.DoesNotThrowAsync(async () => await disabled.ProvisionAsync(default));

        var conflicting = new BackofficeBootstrapService(
            database,
            hasher,
            Options.Create(new BackofficeBootstrapOptions
            {
                Enabled = true,
                FirstName = "Another",
                LastName = "Administrator",
                Email = "another-bootstrap@sarafan.test",
                Password = "Bootstrap_alt_13"
            }),
            TimeProvider.System,
            NullLogger<BackofficeBootstrapService>.Instance);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await conflicting.ProvisionAsync(default));

        var realOperations = new BackofficeBootstrapService(
            database,
            hasher,
            Options.Create(new BackofficeBootstrapOptions { RealOrdersEnabled = true }),
            TimeProvider.System,
            NullLogger<BackofficeBootstrapService>.Instance);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await realOperations.EnsureReleaseGateAsync(default));

        var restrictedUsers = new BackofficeUserService(
            database,
            hasher,
            Options.Create(new BackofficeBootstrapOptions { RealOrdersEnabled = true }),
            TimeProvider.System,
            NullLogger<BackofficeUserService>.Instance);
        var restricted = Assert.ThrowsAsync<ServiceException>(() => restrictedUsers.UpdateAsync(
            initial.Id,
            new BackofficeUserUpdateRequest
            {
                Email = initial.Email,
                FirstName = initial.FirstName,
                LastName = initial.LastName,
                IsActive = true,
                Roles = [BackofficeRoles.Administrator]
            },
            default));
        Assert.That(restricted?.Code, Is.EqualTo("demo_backoffice_forbidden"));
    }

    [Test]
    public async Task BackofficeTokenValidation_RejectsMissingRealmClaims()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var events = scope.ServiceProvider.GetRequiredService<BackofficeJwtBearerEvents>();
        var context = new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme(
                BackofficeAuthenticationDefaults.Scheme,
                displayName: null,
                typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"))
        };

        await events.TokenValidated(context);
        Assert.That(context.Result?.Failure, Is.Not.Null);
    }

    [Test]
    public async Task MalformedStoredPasswordHash_IsReportedAsGenericLoginFailure()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = $"malformed-{Guid.NewGuid():N}@sarafan.test";
        var now = DateTimeOffset.UtcNow;
        database.BackofficeUsers.Add(new BackofficeUser
        {
            Email = email,
            NormalizedEmail = email,
            FirstName = "Malformed",
            LastName = "Hash",
            PasswordHash = "not-a-bcrypt-hash",
            CreatedAt = now,
            UpdatedAt = now,
            UserRoles = [new BackofficeUserRole { RoleCode = BackofficeRoles.Operator }]
        });
        await database.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<BackofficeAuthenticationService>();
        var exception = Assert.ThrowsAsync<ServiceException>(() => service.LoginAsync(
            new BackofficeLoginRequest { Email = email, Password = "Login_pass_13" },
            "test-origin",
            null,
            default));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception?.StatusCode, Is.EqualTo(401));
            Assert.That(exception?.Code, Is.EqualTo("backoffice_login_failed"));
        }

        database.ChangeTracker.Clear();
        var created = await database.BackofficeUsers.SingleAsync(item => item.Email == email);
        database.BackofficeUsers.Remove(created);
        await database.SaveChangesAsync();
    }

    [Test]
    public async Task UserWithoutRoles_CannotLogin()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IBackofficePasswordHasher>();
        var email = $"roleless-{Guid.NewGuid():N}@sarafan.test";
        const string password = "Roleless_pass_13";
        var now = DateTimeOffset.UtcNow;
        database.BackofficeUsers.Add(new BackofficeUser
        {
            Email = email,
            NormalizedEmail = email,
            FirstName = "Roleless",
            LastName = "User",
            PasswordHash = hasher.Hash(password),
            CreatedAt = now,
            UpdatedAt = now
        });
        await database.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<BackofficeAuthenticationService>();
        var exception = Assert.ThrowsAsync<ServiceException>(() => service.LoginAsync(
            new BackofficeLoginRequest { Email = email, Password = password },
            "test-origin",
            null,
            default));
        Assert.That(exception?.Code, Is.EqualTo("backoffice_login_failed"));

        database.ChangeTracker.Clear();
        var created = await database.BackofficeUsers.SingleAsync(item => item.NormalizedEmail == email);
        database.BackofficeUsers.Remove(created);
        await database.SaveChangesAsync();
    }

    [Test]
    public async Task ChangingDemoPassword_ClearsDemoMarker()
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IBackofficePasswordHasher>();
        var email = $"demo-marker-{Guid.NewGuid():N}@sarafan.test";
        var now = DateTimeOffset.UtcNow;
        var user = new BackofficeUser
        {
            Email = email,
            NormalizedEmail = email,
            FirstName = "Demo",
            LastName = "Marker",
            PasswordHash = hasher.Hash("Demo_pass_old_13"),
            IsDemo = true,
            CreatedAt = now,
            UpdatedAt = now,
            UserRoles = [new BackofficeUserRole { RoleCode = BackofficeRoles.Operator }]
        };
        database.BackofficeUsers.Add(user);
        await database.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<BackofficeUserService>();
        await service.UpdateSelfAsync(
            user.Id,
            new BackofficeSelfUpdateRequest
            {
                FirstName = user.FirstName,
                LastName = user.LastName,
                Patronymic = "Test",
                Password = "Secure_pass_13"
            },
            default);
        database.ChangeTracker.Clear();
        var updated = await database.BackofficeUsers.SingleAsync(item => item.Id == user.Id);
        Assert.That(updated.IsDemo, Is.False);

        database.BackofficeUsers.Remove(updated);
        await database.SaveChangesAsync();
    }

    [Test]
    public async Task ConcurrentAdministratorDemotions_KeepOneActiveAdministrator()
    {
        BackofficeUserDto initial;
        BackofficeUserDto second;
        await using (var setupScope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            var database = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userService = setupScope.ServiceProvider.GetRequiredService<BackofficeUserService>();
            var initialUser = await database.BackofficeUsers
                .AsNoTracking()
                .Include(item => item.UserRoles)
                .SingleAsync(item => item.NormalizedEmail == IntegrationTestEnvironment.BackofficeEmail);
            initial = BackofficeUserDto.From(initialUser);
            second = await userService.CreateAsync(
                new BackofficeUserCreateRequest
                {
                    Email = $"concurrent-admin-{Guid.NewGuid():N}@sarafan.test",
                    FirstName = "Concurrent",
                    LastName = "Administrator",
                    Password = "Admin_concur_13",
                    Roles = [BackofficeRoles.Administrator]
                },
                default);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = DemoteAfterGate(initial, gate.Task);
        var secondTask = DemoteAfterGate(second, gate.Task);
        gate.SetResult();
        var outcomes = await Task.WhenAll(firstTask, secondTask);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outcomes.Count(item => item == "updated"), Is.EqualTo(1));
            Assert.That(outcomes.Count(item => item == "last_backoffice_administrator"), Is.EqualTo(1));
        }

        await using var cleanupScope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var cleanupDatabase = cleanupScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var initialEntity = await cleanupDatabase.BackofficeUsers
            .Include(item => item.UserRoles)
            .SingleAsync(item => item.Id == initial.Id);
        cleanupDatabase.BackofficeUserRoles.RemoveRange(initialEntity.UserRoles);
        initialEntity.UserRoles =
        [
            new BackofficeUserRole
            {
                BackofficeUser = initialEntity,
                RoleCode = BackofficeRoles.Administrator
            }
        ];
        initialEntity.IsActive = true;
        initialEntity.TokenVersion++;
        initialEntity.UpdatedAt = DateTimeOffset.UtcNow;
        var secondEntity = await cleanupDatabase.BackofficeUsers.SingleAsync(item => item.Id == second.Id);
        cleanupDatabase.BackofficeUsers.Remove(secondEntity);
        await cleanupDatabase.SaveChangesAsync();
    }

    [Test]
    public async Task BackofficeMigration_AppliesAndRollsBackIndependently()
    {
        string applicationConnection;
        await using (var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope())
        {
            applicationConnection = scope.ServiceProvider
                .GetRequiredService<AppDbContext>()
                .Database.GetConnectionString()!;
        }

        var databaseName = $"sarafan_backoffice_migration_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(applicationConnection)
        {
            Database = "postgres",
            Pooling = false
        };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var databaseBuilder = new NpgsqlConnectionStringBuilder(applicationConnection)
            {
                Database = databaseName,
                Pooling = false
            };
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(databaseBuilder.ConnectionString)
                .Options;
            await using var database = new AppDbContext(options);
            await database.Database.MigrateAsync();
            Assert.That(await TableExists(databaseBuilder.ConnectionString, "backoffice_users"), Is.True);
            Assert.That(await database.BackofficeRoles.CountAsync(), Is.EqualTo(4));

            await database.Database.GetService<IMigrator>()
                .MigrateAsync("20260829223243_InitialCustomerIdentity");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(await TableExists(databaseBuilder.ConnectionString, "backoffice_users"), Is.False);
                Assert.That(await TableExists(databaseBuilder.ConnectionString, "customers"), Is.True);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> DemoteAfterGate(BackofficeUserDto user, Task gate)
    {
        await using var scope = IntegrationTestEnvironment.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BackofficeUserService>();
        await gate;
        try
        {
            await service.UpdateAsync(
                user.Id,
                new BackofficeUserUpdateRequest
                {
                    Email = user.Email,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Patronymic = user.Patronymic,
                    IsActive = true,
                    Roles = [BackofficeRoles.Operator]
                },
                default);
            return "updated";
        }
        catch (ServiceException exception)
        {
            return exception.Code;
        }
    }

    private static async Task<bool> TableExists(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table) IS NOT NULL";
        command.Parameters.AddWithValue("table", $"public.{table}");
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
