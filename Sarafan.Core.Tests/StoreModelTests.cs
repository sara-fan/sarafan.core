// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

using Sarafan.Core.Data;
using Sarafan.Core.Models;

namespace Sarafan.Core.ModelTests;

[TestFixture]
public sealed class StoreModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 15, 0, 0, TimeSpan.FromHours(3));

    private static AppDbContext MetadataContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=metadata;Username=unused;Password=unused;Timeout=1").Options);

    [Test]
    public void StoreSchemaDefinesDefaultsLimitsConcurrencyAndPublicationIndexes()
    {
        using var database = MetadataContext();
        var model = database.GetService<IDesignTimeModel>().Model;
        var store = model.FindEntityType(typeof(Store))!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Enum.GetValues<StoreStatus>(), Is.EqualTo(new[] { StoreStatus.Hidden, StoreStatus.Active, StoreStatus.Priority }));
            Assert.That(store.GetTableName(), Is.EqualTo("stores"));
            Assert.That(store.FindPrimaryKey()!.Properties.Select(item => item.Name), Is.EqualTo(new[] { "Id" }));
            Assert.That(store.FindProperty(nameof(Store.Name))!.GetMaxLength(), Is.EqualTo(200));
            Assert.That(store.FindProperty(nameof(Store.Description))!.GetMaxLength(), Is.EqualTo(160));
            Assert.That(store.FindProperty(nameof(Store.OfficialUrl))!.GetMaxLength(), Is.EqualTo(2048));
            Assert.That(store.GetProperties().All(item => !item.IsNullable), Is.True);
            Assert.That(store.FindProperty(nameof(Store.Status))!.GetDefaultValue(), Is.EqualTo(StoreStatus.Hidden));
            Assert.That(store.FindProperty(nameof(Store.Status))!.GetColumnType(), Is.EqualTo("integer"));
            Assert.That(store.FindProperty(nameof(Store.DisplayOrder))!.GetDefaultValue(), Is.EqualTo(0));
            Assert.That(store.FindProperty(nameof(Store.Version))!.IsConcurrencyToken, Is.True);
            Assert.That(store.FindProperty(nameof(Store.Version))!.ValueGenerated, Is.EqualTo(ValueGenerated.Never));
            Assert.That(store.FindProperty(nameof(Store.Version))!.GetColumnType(), Is.EqualTo("uuid"));
            Assert.That(store.FindProperty(nameof(Store.CreatedAt))!.GetAfterSaveBehavior(), Is.EqualTo(PropertySaveBehavior.Throw));
            Assert.That(store.FindProperty(nameof(Store.CreatedAt))!.GetColumnType(), Is.EqualTo("timestamp with time zone"));
            Assert.That(store.FindProperty(nameof(Store.UpdatedAt))!.GetColumnType(), Is.EqualTo("timestamp with time zone"));
            Assert.That(store.GetIndexes().Select(index => string.Join(",", index.Properties.Select(item => item.Name))),
                Is.EquivalentTo(new[] { "Status,DisplayOrder,Id", "DisplayOrder" }));
            Assert.That(store.GetCheckConstraints().ToDictionary(item => item.Name!, item => item.Sql),
                Does.ContainKey("ck_stores_status").WithValue("status IN (0, 1, 2)"));
            Assert.That(store.GetCheckConstraints().ToDictionary(item => item.Name!, item => item.Sql),
                Does.ContainKey("ck_stores_display_order").WithValue("display_order >= 0"));
            Assert.That(store.GetForeignKeys(), Is.Empty);
            Assert.That(store.GetReferencingForeignKeys().Select(item => item.DeclaringEntityType.ClrType),
                Is.EqualTo(new[] { typeof(StoreLogo) }));
            Assert.That(store.GetSeedData(), Is.Empty);
        }
    }

    [Test]
    public void LogoSchemaIsAnUnseededSeparateDependentWithCascadeDeletion()
    {
        using var database = MetadataContext();
        var entity = database.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(StoreLogo))!;
        var relationship = entity.GetForeignKeys().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entity.GetTableName(), Is.EqualTo("store_logos"));
            Assert.That(entity.FindPrimaryKey()!.Properties.Select(item => item.Name), Is.EqualTo(new[] { "StoreId" }));
            Assert.That(entity.FindProperty(nameof(StoreLogo.Content))!.GetColumnType(), Is.EqualTo("bytea"));
            Assert.That(entity.FindProperty(nameof(StoreLogo.Content))!.GetFieldName(), Is.EqualTo("content"));
            Assert.That(entity.FindProperty(nameof(StoreLogo.ContentSha256))!.GetMaxLength(), Is.EqualTo(64));
            Assert.That(entity.FindProperty(nameof(StoreLogo.ContentType))!.GetMaxLength(), Is.EqualTo(64));
            Assert.That(entity.GetProperties().All(item => !item.IsNullable), Is.True);
            Assert.That(relationship.PrincipalEntityType.ClrType, Is.EqualTo(typeof(Store)));
            Assert.That(relationship.IsUnique && relationship.IsRequired, Is.True);
            Assert.That(relationship.DeleteBehavior, Is.EqualTo(DeleteBehavior.Cascade));
            Assert.That(entity.GetCheckConstraints().Select(item => item.Name), Is.EquivalentTo(new[]
            {
                "ck_store_logos_content_type", "ck_store_logos_content_size", "ck_store_logos_content_sha256"
            }));
            Assert.That(entity.GetSeedData(), Is.Empty);
        }
    }

    [Test]
    public void NewStoreIsHiddenAndTimestampsUseUtcMicrosecondPrecision()
    {
        var store = new Store("Shop", "Description", "https://shop.example", Now.AddTicks(19));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Status, Is.EqualTo(StoreStatus.Hidden));
            Assert.That(store.DisplayOrder, Is.Zero);
            Assert.That(store.Logo, Is.Null);
            Assert.That(store.Version, Is.Not.EqualTo(Guid.Empty));
            Assert.That(store.CreatedAt.Offset, Is.EqualTo(TimeSpan.Zero));
            Assert.That(store.CreatedAt, Is.EqualTo(Now.ToUniversalTime().AddMicroseconds(1)));
            Assert.That(store.UpdatedAt, Is.EqualTo(store.CreatedAt));
        }
    }

    [Test]
    public void FieldStateAndOrderChangesAdvanceVersionAndTimeWhileHidingPreservesSelection()
    {
        var store = new Store("Shop", "Description", "https://shop.example", Now);
        var version = store.Version;
        store.Update("New shop", "New description", "http://new.example", StoreStatus.Priority, 3, Now.AddSeconds(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Name, Is.EqualTo("New shop"));
            Assert.That(store.Description, Is.EqualTo("New description"));
            Assert.That(store.OfficialUrl, Is.EqualTo("http://new.example"));
            Assert.That(store.Status, Is.EqualTo(StoreStatus.Priority));
            Assert.That(store.Version, Is.Not.EqualTo(version));
            Assert.That(store.UpdatedAt, Is.EqualTo(Now.AddSeconds(1)));
        }

        version = store.Version;
        store.Update(store.Name, store.Description, store.OfficialUrl, StoreStatus.Hidden, store.DisplayOrder, Now);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Status, Is.EqualTo(StoreStatus.Hidden));
            Assert.That(store.DisplayOrder, Is.EqualTo(3));
            Assert.That(store.Name, Is.EqualTo("New shop"));
            Assert.That(store.Version, Is.Not.EqualTo(version));
            Assert.That(store.UpdatedAt, Is.EqualTo(Now.AddSeconds(1).AddMicroseconds(1)));
            Assert.That(store.CreatedAt, Is.EqualTo(Now));
        }
    }

    [Test]
    public async Task LogoReplacementTracksParentVersionAndRoundTripsWithoutLoadingBytesInLists()
    {
        await using var database = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new Store("Shop", "Description", "https://shop.example", Now);
        database.Stores.Add(store);
        var version = store.Version;
        byte[] content = [1, 2, 3];
        store.SetLogo("image/png", content, Now);
        var originalLogo = store.Logo!;
        content[0] = 255;
        store.Logo!.Content[1] = 255;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Version, Is.Not.EqualTo(version));
            Assert.That(store.UpdatedAt, Is.EqualTo(Now.AddMicroseconds(1)));
            Assert.That(store.Logo.Content, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(store.Logo.ContentSha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }))));
            Assert.That(store.Logo.Store, Is.SameAs(store));
        }
        await database.SaveChangesAsync();

        version = store.Version;
        store.SetLogo("image/webp", [4, 5], Now.AddSeconds(1));
        Assert.That(store.Logo, Is.SameAs(originalLogo));
        Assert.That(store.Version, Is.Not.EqualTo(version));
        Assert.That(store.UpdatedAt, Is.EqualTo(Now.AddSeconds(1)));
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();

        var loaded = await database.Stores.Include(item => item.Logo).SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded.Logo!.StoreId, Is.EqualTo(loaded.Id));
            Assert.That(loaded.Logo.ContentType, Is.EqualTo("image/webp"));
            Assert.That(loaded.Logo.Content, Is.EqualTo(new byte[] { 4, 5 }));
            Assert.That(loaded.Logo.ContentSha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 4, 5 }))));
            Assert.That(loaded.Version, Is.EqualTo(store.Version));
            Assert.That(await database.StoreLogos.CountAsync(), Is.EqualTo(1));
        }

        database.ChangeTracker.Clear();
        var summaries = await database.Stores.Select(item => new { item.Id, item.Name, item.Description }).ToListAsync();
        Assert.That(summaries.Single().Name, Is.EqualTo("Shop"));
        Assert.That(database.ChangeTracker.Entries<StoreLogo>(), Is.Empty);
    }

    [Test]
    public void PublicListProjectionDoesNotSelectLogoContent()
    {
        using var database = MetadataContext();
        var sql = database.Stores.Where(item => item.Status == StoreStatus.Active)
            .OrderBy(item => item.DisplayOrder).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.Name, item.Description, item.OfficialUrl }).ToQueryString();
        Assert.That(sql, Does.Not.Contain("store_logos").And.Not.Contain("content"));
    }
}
