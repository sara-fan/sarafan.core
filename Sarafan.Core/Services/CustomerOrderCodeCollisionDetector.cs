// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Data;

namespace Sarafan.Core.Services;

public interface ICustomerOrderCodeCollisionDetector
{
    bool IsCollision(DbUpdateException exception);
}

internal sealed class CustomerOrderCodeCollisionDetector(AppDbContext database)
    : ICustomerOrderCodeCollisionDetector
{
    public bool IsCollision(DbUpdateException exception)
        => AppDatabaseOperations.For(database).IsCustomerOrderCodeCollision(exception);
}
