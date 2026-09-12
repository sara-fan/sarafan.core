// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum CustomerState
{
    Preliminary = 0,
    Complete = 1,
    Disabled = 2
}

public static class CustomerStateExtensions
{
    public static string GetDisplayName(this CustomerState state) => state switch
    {
        CustomerState.Preliminary => "Предварительный",
        CustomerState.Complete => "Заполненный",
        CustomerState.Disabled => "Отключённый",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static string GetRouteAlias(this CustomerState state) => state switch
    {
        CustomerState.Preliminary => "preliminary",
        CustomerState.Complete => "complete",
        CustomerState.Disabled => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };
}
