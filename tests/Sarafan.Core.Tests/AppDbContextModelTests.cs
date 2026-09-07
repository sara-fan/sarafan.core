// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

using Sarafan.Core.Data;

// Metadata checks must also run without the database setup in Sarafan.Core.Tests.
namespace Sarafan.Core.ModelTests;

[TestFixture]
public sealed class AppDbContextModelTests
{
    // No connection is opened. An unusable endpoint guards against accidental database access.
    private const string MetadataConnection = "Host=127.0.0.1;Port=1;Database=model_metadata_only;Username=unused;Password=unused;Timeout=1";

    private static AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(MetadataConnection).Options);

    private static (Type Configuration, Type Entity)[] Configurations() => typeof(AppDbContext).Assembly.GetTypes()
        .Where(type => !type.IsAbstract)
        .SelectMany(type => type.GetInterfaces()
            .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>))
            .Select(contract => (Configuration: type, Entity: contract.GenericTypeArguments[0])))
        .ToArray();

    [Test]
    public void ModelMatchesCommittedMigrationSnapshot()
    {
        using var context = CreateContext();

        Assert.That(context.Database.HasPendingModelChanges(), Is.False,
            "A configuration-only refactor must preserve the schema; intentional schema changes need a migration.");
    }

    [Test]
    public void EveryMappedEntityHasExactlyOneDedicatedConfiguration()
    {
        using var context = CreateContext();
        var entities = context.Model.GetEntityTypes().Select(entity => entity.ClrType).ToArray();
        var configurations = Configurations();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(configurations.Select(item => item.Entity), Is.EquivalentTo(entities),
                "Each entity, including explicit join entities, needs exactly one configuration.");
            Assert.That(configurations.Select(item => item.Configuration), Is.Unique,
                "A configuration class must configure only its own entity.");
            foreach (var (configuration, entity) in configurations)
            {
                Assert.That(configuration.Name, Is.EqualTo($"{entity.Name}Configuration"));
                Assert.That(configuration.Namespace, Does.StartWith("Sarafan.Core.Data.Configurations."));
                Assert.That(configuration.IsSealed && configuration.IsNotPublic, Is.True, configuration.FullName);
            }
        }
    }

    [Test]
    public void ConfigurationDiscoveryOrderDoesNotChangeTheSchema()
    {
        using var context = CreateContext();
        using var reversed = new ReverseConfigurationContext(new DbContextOptionsBuilder<ReverseConfigurationContext>()
            .UseNpgsql(MetadataConnection).Options);
        var differ = context.GetService<IMigrationsModelDiffer>();
        var expected = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var actual = reversed.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        Assert.That(differ.GetDifferences(expected, actual), Is.Empty,
            "Configuration ownership must not depend on discovery order or DbSet registration.");
    }

    private sealed class ReverseConfigurationContext(DbContextOptions<ReverseConfigurationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var apply = typeof(ModelBuilder).GetMethod(nameof(ModelBuilder.ApplyConfiguration))!;
            foreach (var (configuration, entity) in Configurations().OrderByDescending(item => item.Configuration.FullName, StringComparer.Ordinal))
                apply.MakeGenericMethod(entity).Invoke(modelBuilder, [Activator.CreateInstance(configuration, nonPublic: true)]);
        }
    }
}
