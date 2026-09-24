// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Globalization;
using System.Security.Cryptography;

namespace Sarafan.Core.Services;

public interface ICustomerOrderCodeGenerator
{
    string Generate();
}

public sealed class CustomerOrderCodeGenerator : ICustomerOrderCodeGenerator
{
    public string Generate() => RandomNumberGenerator
        .GetInt32(100_000_000)
        .ToString("D8", CultureInfo.InvariantCulture);
}
