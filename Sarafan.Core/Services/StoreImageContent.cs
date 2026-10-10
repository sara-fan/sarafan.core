// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

namespace Sarafan.Core.Services;

internal static class StoreImageContent
{
    internal const int MaxDimension = ImageContent.MaxDimension;
    internal const int MaxPixels = ImageContent.MaxPixels;
    internal const int MaxFrames = ImageContent.MaxFrames;
    internal const int MaxAnimationPixels = ImageContent.MaxAnimationPixels;
    internal const int MaxMetadataBytes = ImageContent.MaxMetadataBytes;
    internal static bool Fits(uint width, uint height) => ImageContent.Fits(width, height);
    internal static bool IsValid(string type, ReadOnlySpan<byte> bytes) => ImageContent.IsValid(type, bytes);
}
