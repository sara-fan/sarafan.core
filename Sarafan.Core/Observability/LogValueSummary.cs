// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Mvc;

using Sarafan.Core.Authentication;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Observability;

internal static class LogValueSummary
{
    private static readonly HashSet<string> PrivateListStateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "pageSize", "sortBy", "sortOrder", "search", "processed", "kind", "action", "service", "entryId", "customerId", "documentId"
    };

    // Only these explicit projections may read values. Never serialize or call ToString on arbitrary input.
    internal static string Inputs(params (string Name, object? Value)[] values)
        => values.Length == 0 ? "none" : string.Join("; ", values.Select(value =>
            $"{value.Name}={(PrivateListStateNames.Contains(value.Name) ? "[redacted]" : Describe(value.Value))}"));

    internal static string Describe(object? value) => value switch
    {
        null => "null",
        CancellationToken token => $"cancellation requested={token.IsCancellationRequested}",
        VerifyCodeRequest => "VerifyCodeRequest(phone/code/receipt=[redacted])",
        RequestCodeRequest => "RequestCodeRequest(phone/consents=[redacted])",
        PhoneResolveRequest => "PhoneResolveRequest(phone=[redacted])",
        BackofficeLoginRequest => "BackofficeLoginRequest(email/password=[redacted])",
        BackofficeUserCreateRequest => "BackofficeUserCreateRequest(identity/password/roles=[redacted])",
        BackofficeUserUpdateRequest => "BackofficeUserUpdateRequest(identity/password/roles=[redacted])",
        BackofficeSelfUpdateRequest => "BackofficeSelfUpdateRequest(identity/password=[redacted])",
        CustomerProfileUpdateRequest => "CustomerProfileUpdateRequest([redacted])",
        ProductPreviewRequest => "ProductPreviewRequest(sourceUrl=[redacted])",
        StoreWriteRequest => "StoreWriteRequest(fields/logo/version=[redacted])",
        DeleteStoreRequest => "DeleteStoreRequest(version=[redacted])",
        StaffStoreDto => "StaffStoreDto(fields/logo/version=[redacted])",
        StoreLogoDto => "StoreLogoDto(content/metadata=[redacted])",
        StoreListDto<PublicStoreDto> stores => $"PublicStoreDto collection(count={stores.Items.Length})",
        StoreListDto<StaffStoreDto> stores => $"StaffStoreDto collection(count={stores.Items.Length})",
        StoreOpsDto => "StoreOpsDto(catalogue/actions=[redacted])",
        ServiceCatalogueWriteRequest => "ServiceCatalogueWriteRequest(parameters/dates/version=[redacted])",
        OrderPricingWriteRequest => "OrderPricingWriteRequest(inputs/version=[redacted])",
        ConfirmOrderPricingRequest => "ConfirmOrderPricingRequest(version=[redacted])",
        OrderPricingDto => "OrderPricingDto(calculation/history=[redacted])",
        OrderPricingOpsDto => "OrderPricingOpsDto(metadata/actions=[redacted])",
        DeleteServiceCatalogueEntryRequest => "DeleteServiceCatalogueEntryRequest(version=[redacted])",
        ServiceCatalogueEntryDto => "ServiceCatalogueEntryDto(parameters/dates/version=[redacted])",
        ServiceCatalogueListDto catalogue => $"ServiceCatalogueEntryDto collection(count={catalogue.Items.Length})",
        ServiceCatalogueOpsDto => "ServiceCatalogueOpsDto(catalogue/limits/actions=[redacted])",
        ServiceCatalogueAuditPageDto audit => $"ServiceCatalogueAuditDto page(count={audit.Items.Length}; filters=[redacted])",
        ProductPreviewDto preview when preview.Outcome is ProductPreviewDto.ManualReviewOutcome or ProductPreviewDto.RecognizedOutcome
            => $"ProductPreviewDto(sourceUrl/product=[redacted]; outcome={preview.Outcome})",
        ProductPreviewDto => "ProductPreviewDto(sourceUrl/product=[redacted]; outcome=[redacted])",
        CreateOrderRequest => "CreateOrderRequest(sourceUrl/product=[redacted])",
        OrderProductRequest => "OrderProductRequest([redacted])",
        UpdateOrderProductRequest => "UpdateOrderProductRequest([redacted])",
        BackofficeOrderDetailsDto => "BackofficeOrderDetailsDto(order/product/customer=[redacted])",
        OrderProductDto => "OrderProductDto([redacted])",
        OrderLimitRatePair => "OrderLimitRatePair([redacted])",
        BackofficeUserDto => "BackofficeUserDto([redacted])",
        IReadOnlyCollection<BackofficeUserDto> users => $"BackofficeUserDto collection(count={users.Count})",
        BackofficeIdentityDto => "BackofficeIdentityDto([redacted])",
        BackofficeRoleDto => "BackofficeRoleDto([redacted])",
        IReadOnlyCollection<BackofficeRoleDto> roles => $"BackofficeRoleDto collection(count={roles.Count})",
        CustomerDto => "CustomerDto([redacted])",
        OrderDto => "OrderDto(identity/product/pricing=[redacted])",
        IReadOnlyCollection<CustomerOrderListItemDto> orders => $"CustomerOrderListItemDto collection(count={orders.Count})",
        Customer => "Customer([redacted])",
        BackofficeUser => "BackofficeUser([redacted])",
        AuthenticationSession => "AuthenticationSession(tokens/customer=[redacted])",
        AuthenticationSessionDto => "AuthenticationSessionDto(token/customer=[redacted])",
        BackofficeAuthenticationSession => "BackofficeAuthenticationSession(tokens/user=[redacted])",
        BackofficeAuthenticationSessionDto => "BackofficeAuthenticationSessionDto(token/user=[redacted])",
        AccessTokenResult => "AccessTokenResult(token/expiry=[redacted])",
        IFormFile => "file(content/metadata=[redacted])",
        FileResult => "file result(content/metadata=[redacted])",
        SarafanProblemDetails problem => $"problem(status={problem.Status}; details=[redacted])",
        ServiceStatus status when status.Service == "Sarafan.Core" && status.Status == "ok" && status.AppVersion == VersionInfo.AppVersion
            => $"ServiceStatus(name=Sarafan.Core; status=ok; version={VersionInfo.AppVersion})",
        ServiceStatus => "ServiceStatus([redacted])",
        BackofficeStatus => "BackofficeStatus(version/rates=[redacted])",
        ExchangeRateDto => "ExchangeRateDto(rate/metadata=[redacted])",
        CbrRate => "CbrRate(rate/metadata=[redacted])",
        IanaTldDownload download => $"IanaTldDownload(version={download.Version}; count={download.TopLevelDomains.Count}; digest=[redacted])",
        IanaTldCatalogSnapshot snapshot => $"IanaTldCatalogSnapshot(version={snapshot.Version}; count={snapshot.TopLevelDomains.Count})",
        IanaTldUpdateResult result => result.ToString(),
        ObjectResult result => $"status={result.StatusCode ?? StatusCodes.Status200OK}; output={Describe(result.Value)}",
        StatusCodeResult result => $"status={result.StatusCode}; no body",
        EmptyResult => "no body",
        OperationLogging.OperationCompleted => "completed; no return value",
        bool result => result ? "true" : "false",
        _ => "[redacted]"
    };
}
