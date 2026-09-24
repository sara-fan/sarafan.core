// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Sarafan.Core.Observability;
using Sarafan.Core.RestModels;

namespace Sarafan.Core.Services;

public sealed class ProductPreviewService(
    IanaTldCatalogService tlds,
    ILogger<ProductPreviewService> logger)
{
    public Task<ProductPreviewDto> PreviewAsync(ProductPreviewRequest request, CancellationToken cancellationToken)
        => OperationLogging.RunAsync(
            logger,
            $"{typeof(ProductPreviewService).FullName}.{nameof(PreviewAsync)}",
            () => LogValueSummary.Inputs((nameof(request), request)),
            async () =>
            {
                var catalog = await tlds.GetRequiredAsync(cancellationToken);
                return new ProductPreviewDto(
                    ProductSourceUrl.Normalize(request.SourceUrl, catalog.Values),
                    ProductPreviewDto.ManualReviewOutcome);
            },
            cancellationToken);
}
