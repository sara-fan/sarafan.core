// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Models;

namespace Sarafan.Core.Services;

public static class CustomerProfileState
{
    public static CustomerState Evaluate(CustomerProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.LastName)
        && !string.IsNullOrWhiteSpace(profile.FirstName)
        && !string.IsNullOrWhiteSpace(profile.Email)
        && !string.IsNullOrWhiteSpace(profile.Inn)
        && !string.IsNullOrWhiteSpace(profile.PostalCode)
        && !string.IsNullOrWhiteSpace(profile.City)
        && !string.IsNullOrWhiteSpace(profile.Address)
            ? CustomerState.Complete
            : CustomerState.Preliminary;
}
