// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;
using Sarafan.Core.Data;

namespace Sarafan.Core.Services;

internal static class ConsentTransaction
{
    internal static async Task<T> Run<T>(AppDbContext database, Func<Task<T>> action, CancellationToken token, Func<Task>? beforeCommit = null)
    {
        if (database.Database.CurrentTransaction is not null)
        {
            await Lock(database, token);
            var result = await action();
            if (beforeCommit is not null) await beforeCommit();
            return result;
        }
        await using var transaction = await AppDatabaseOperations.For(database)
            .BeginTransactionAsync(database, token);
        await Lock(database, token);
        try
        {
            var result = await action();
            await database.SaveChangesAsync(token);
            if (beforeCommit is not null) await beforeCommit();
            await transaction.CommitAsync(token);
            return result;
        }
        catch (DbUpdateConcurrencyException) { throw new ServiceException(409, "consent_conflict"); }
    }

    internal static Task Lock(AppDbContext database, CancellationToken token)
        => AppDatabaseOperations.For(database).LockConsentsAsync(database, token);

    internal static Task LockCustomer(AppDbContext database, int customerId, CancellationToken token) =>
        AppDatabaseOperations.For(database).LockCustomerAsync(database, customerId, token);
}
