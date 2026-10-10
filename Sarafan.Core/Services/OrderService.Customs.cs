// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed partial class OrderService
{
    private static bool CanMarkCustomsPaid(Order order, OrderPricingSnapshot? snapshot, string[] roles)
        => BackofficeAuthorization.IsAllowed(roles, BackofficeAction.MarkCustomsPaid)
            && Enum.IsDefined(order.Status) && !order.CustomsPaid && snapshot is not null
            && ReadCalculation(snapshot).Components.Any(component => component.Service == ServiceKind.CustomsPayments
                && component.State == PriceComponentState.Calculated && component.AmountRub > 0);

    public Task<BackofficeOrderDetailsDto> MarkCustomsPaidAsync(string number, MarkCustomsPaidRequest request,
        int actorId, string[] roles, CancellationToken token)
        => StaffPaymentMutation(nameof(MarkCustomsPaidAsync),
            () => LogValueSummary.Inputs((nameof(number), number), (nameof(request), request),
                (nameof(actorId), actorId), (nameof(roles), roles), (nameof(token), token)),
            number, request.ExpectedUpdatedAt, actorId, roles, false, token);
}
