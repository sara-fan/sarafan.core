// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Models;

public enum AuthenticationFlowStep
{
    Code = 0,
    Agreement = 1,
    Registration = 2
}

public static class AuthenticationFlowStepExtensions
{
    public static string GetDisplayName(this AuthenticationFlowStep step) => step switch
    {
        AuthenticationFlowStep.Code => "Код подтверждения",
        AuthenticationFlowStep.Agreement => "Пользовательское соглашение",
        AuthenticationFlowStep.Registration => "Регистрация",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
    };

    public static string GetRouteAlias(this AuthenticationFlowStep step) => step switch
    {
        AuthenticationFlowStep.Code => "code",
        AuthenticationFlowStep.Agreement => "agreement",
        AuthenticationFlowStep.Registration => "registration",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
    };
}
