// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Quartz;
namespace Sarafan.Core.Services;

[DisallowConcurrentExecution]
public sealed class LogibooksRegisterJob(LogibooksRegisterService registers, ILogibooksClient client) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
        => await registers.ProcessAsync(client, cancellationToken);
}
