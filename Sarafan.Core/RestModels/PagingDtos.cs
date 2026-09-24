// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.RestModels;

public class PagedResult<T>
{
    public T[] Items { get; init; } = [];
    public PaginationInfo Pagination { get; init; } = new();
    public SortingInfo Sorting { get; init; } = new();
    public string? Search { get; init; }
}

public sealed class PaginationInfo
{
    public int CurrentPage { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    public bool HasNextPage { get; init; }
    public bool HasPreviousPage { get; init; }
}

public sealed class SortingInfo
{
    public string SortBy { get; init; } = "id";
    public string SortOrder { get; init; } = "asc";
}
