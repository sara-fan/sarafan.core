// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Buffers.Binary;
using System.IO.Compression;

namespace Sarafan.Core.Services;

// Bounded container/frame validation, without allocating a decompressed raster.
// Format references: W3C PNG, ITU T.81, and developers.google.com/speed/webp/docs/riff_container.
internal static class StoreImageContent
{
    internal const int MaxDimension = 4096;
    internal const int MaxPixels = 4 * 1024 * 1024;
    internal const int MaxFrames = 100;
    internal const int MaxAnimationPixels = 16 * 1024 * 1024;
    internal const int MaxMetadataBytes = 1024 * 1024;

    internal static bool Fits(uint width, uint height) => width is > 0 and <= MaxDimension
        && height is > 0 and <= MaxDimension && (ulong)width * height <= MaxPixels;
    internal static bool IsValid(string type, ReadOnlySpan<byte> bytes) => type switch
    {
        "image/png" => Png(bytes),
        "image/jpeg" => Jpeg(bytes),
        "image/webp" => Webp(bytes),
        _ => false
    };

    private static bool Png(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)
            || !Fits(BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]))) return false;
        var depth = bytes[24];
        var color = bytes[25];
        if (!(color switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => depth is 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => false
        }) || bytes[26] != 0 || bytes[27] != 0 || bytes[28] > 1) return false;
        var hasData = false;
        var endedData = false;
        var hasPalette = false;
        var metadataBytes = 0;
        using var compressed = new MemoryStream();
        var position = 8;
        while (position <= bytes.Length - 12)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(bytes[position..]);
            if (size > bytes.Length - position - 12) return false;
            if (PngCrc(bytes.Slice(position + 4, (int)size + 4))
                != BinaryPrimitives.ReadUInt32BigEndian(bytes[(position + 8 + (int)size)..])) return false;
            var kind = bytes.Slice(position + 4, 4);
            var payload = bytes.Slice(position + 8, (int)size);
            if (kind.SequenceEqual("IHDR"u8) && position != 8) return false;
            if (kind.SequenceEqual("PLTE"u8))
            {
                if (hasPalette || hasData || color is 0 or 4 || size == 0 || size % 3 != 0
                    || size > 768 || (color == 3 && size / 3 > (1u << depth))) return false;
                hasPalette = true;
            }
            else if (kind.SequenceEqual("IDAT"u8))
            {
                if (endedData || (color == 3 && !hasPalette)) return false;
                hasData = true;
                compressed.Write(payload);
            }
            else
            {
                if (hasData) endedData = true;
                // PNG logos are static. Bound ancillary decompression independently of raster data.
                if (kind.SequenceEqual("acTL"u8) || kind.SequenceEqual("fcTL"u8) || kind.SequenceEqual("fdAT"u8)
                    || !PngMetadata(kind, payload, ref metadataBytes)) return false;
                if ((kind[0] & 32) == 0 && !kind.SequenceEqual("IHDR"u8) && !kind.SequenceEqual("IEND"u8)) return false;
            }
            position += (int)size + 12;
            if (kind.SequenceEqual("IEND"u8)) return size == 0 && hasData && position == bytes.Length
                && PngPixels(compressed, (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]),
                    (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]), depth, color, bytes[28] == 1);
        }
        return false;
    }

    private static bool PngMetadata(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> payload, ref int total)
    {
        var international = kind.SequenceEqual("iTXt"u8);
        if (!international && !kind.SequenceEqual("iCCP"u8) && !kind.SequenceEqual("zTXt"u8)) return true;
        var end = payload.IndexOf((byte)0);
        if (end is < 1 or > 79 || payload.Length <= end + 2) return false;
        payload = payload[(end + 1)..];
        var compressed = true;
        if (international)
        {
            if (payload[0] > 1 || payload[1] != 0) return false;
            compressed = payload[0] == 1;
            payload = payload[2..];
            for (var field = 0; field < 2; field++)
            {
                end = payload.IndexOf((byte)0);
                if (end < 0) return false;
                payload = payload[(end + 1)..];
            }
        }
        else
        {
            if (payload[0] != 0) return false;
            payload = payload[1..];
        }
        if (!compressed) { total += payload.Length; return total <= MaxMetadataBytes; }
        if (!ZlibHeader(payload)) return false;
        try
        {
            using var input = new ExactZlibInput(payload.ToArray(), payload.Length);
            using var decoded = new ZLibStream(input, CompressionMode.Decompress);
            Span<byte> buffer = stackalloc byte[4096];
            uint a = 1, b = 0;
            int count;
            while ((count = decoded.Read(buffer)) != 0)
            {
                total += count;
                if (total > MaxMetadataBytes) return false;
                Adler(buffer[..count], ref a, ref b);
            }
            return input.Position == payload.Length && ((b << 16) | a) == BinaryPrimitives.ReadUInt32BigEndian(payload[^4..]);
        }
        catch (InvalidDataException) { return false; }
    }

    private static bool PngPixels(MemoryStream compressed, int width, int height, int depth, int color, bool interlaced)
    {
        var encoded = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        if (!ZlibHeader(encoded)) return false;
        try
        {
            using var input = new ExactZlibInput(compressed.GetBuffer(), (int)compressed.Length);
            using var decoded = new ZLibStream(input, CompressionMode.Decompress);
            var channels = color switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
            // Only one bounded scanline is retained. Adam7 uses the same pixel budget as a static image.
            var row = new byte[1 + (width * depth * channels + 7) / 8];
            uint a = 1, b = 0;
            (int X, int Y, int Dx, int Dy)[] passes = interlaced
                ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
                : [(0, 0, 1, 1)];
            foreach (var pass in passes)
            {
                var columns = (width - pass.X + pass.Dx - 1) / pass.Dx;
                var rows = (height - pass.Y + pass.Dy - 1) / pass.Dy;
                if (columns <= 0 || rows <= 0) continue;
                var length = 1 + (columns * depth * channels + 7) / 8;
                for (var y = 0; y < rows; y++)
                {
                    if (decoded.ReadAtLeast(row.AsSpan(0, length), length, throwOnEndOfStream: false) != length || row[0] > 4) return false;
                    Adler(row.AsSpan(0, length), ref a, ref b);
                }
            }
            return decoded.ReadByte() == -1 && input.Position == encoded.Length
                && ((b << 16) | a) == BinaryPrimitives.ReadUInt32BigEndian(encoded[^4..]);
        }
        catch (InvalidDataException) { return false; }
    }

    // ZLibStream can return partial output without throwing on a missing trailer.
    // Independently require the PNG zlib header and Adler-32 trailer, including metadata streams.
    private static bool ZlibHeader(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && (bytes[0] & 15) == 8
        && (bytes[0] >> 4) <= 7 && (bytes[1] & 32) == 0 && ((bytes[0] << 8) | bytes[1]) % 31 == 0;

    private static void Adler(ReadOnlySpan<byte> bytes, ref uint a, ref uint b)
    {
        foreach (var value in bytes) { a = (a + value) % 65521; b = (b + a) % 65521; }
    }

    // Prevent inflater read-ahead from hiding bytes after the sole zlib stream.
    // Sentinels force incomplete streams to consume beyond their declared length instead of
    // silently returning partial output at EOF. Complete streams stop before the sentinels.
    // Input is already bounded by the upload byte limit; decoded output has separate budgets.
    private sealed class ExactZlibInput(byte[] bytes, int length) : MemoryStream(WithSentinels(bytes, length), writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);

        private static byte[] WithSentinels(byte[] bytes, int length)
        {
            var result = new byte[length + 8];
            bytes.AsSpan(0, length).CopyTo(result);
            return result;
        }
    }

    private static uint PngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }

    private static bool Jpeg(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216) return false;
        var position = 2;
        var hasFrame = false;
        var hasScanData = false;
        while (position < bytes.Length)
        {
            if (bytes[position++] != 255) return false;
            while (position < bytes.Length && bytes[position] == 255) position++;
            if (position == bytes.Length) return false;
            var marker = bytes[position++];
            if (marker == 217) return hasFrame && hasScanData && position == bytes.Length;
            if (marker is 0 or 216 or >= 208 and <= 215 || position > bytes.Length - 2) return false;
            var size = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
            if (size < 2 || size > bytes.Length - position) return false;
            var segment = bytes.Slice(position + 2, size - 2);
            if (marker is >= 192 and <= 207 and not (196 or 200 or 204))
            {
                if (segment.Length < 6 || segment[5] == 0 || segment.Length != 6 + 3 * segment[5]
                    || hasFrame || !Fits(BinaryPrimitives.ReadUInt16BigEndian(segment[3..]),
                        BinaryPrimitives.ReadUInt16BigEndian(segment[1..]))) return false;
                hasFrame = true;
            }
            position += size;
            if (marker != 218) continue;
            if (!hasFrame || segment.Length < 4 || segment[0] == 0 || segment.Length != 4 + 2 * segment[0]) return false;
            // Entropy data ends at an unescaped non-restart marker; progressive JPEG may have multiple scans.
            while (position < bytes.Length)
            {
                if (bytes[position] != 255) { hasScanData = true; position++; continue; }
                if (position == bytes.Length - 1) return false;
                if (bytes[position + 1] == 0 || bytes[position + 1] is >= 208 and <= 215)
                { hasScanData = true; position += 2; continue; }
                break;
            }
        }
        return false;
    }

    private static bool Webp(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 20 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) == bytes.Length - 8 && WebpChunks(bytes[12..]);

    private static bool WebpChunks(ReadOnlySpan<byte> bytes)
    {
        var hasFrame = false;
        var extended = false;
        var animated = false;
        var allowsAlpha = false;
        var animationHeader = false;
        var alpha = false;
        uint width = 0, height = 0;
        var frames = 0;
        var position = 0;
        while (position <= bytes.Length - 8)
        {
            if (!ReadWebpChunk(bytes, ref position, out var kind, out var payload)) return false;
            if (kind.SequenceEqual("VP8 "u8) || kind.SequenceEqual("VP8L"u8))
            {
                if (animated || hasFrame || (alpha && kind.SequenceEqual("VP8L"u8))
                    || !WebpFrame(kind, payload, out var frameWidth, out var frameHeight)
                    || (extended && (width != frameWidth || height != frameHeight))) return false;
                hasFrame = true;
            }
            else if (kind.SequenceEqual("ALPH"u8))
            {
                if (!extended || !allowsAlpha || animated || alpha || hasFrame || !WebpAlpha(payload, width, height)) return false;
                alpha = true;
            }
            else if (kind.SequenceEqual("VP8X"u8))
            {
                if (extended || payload.Length != 10 || position != 18 || (payload[0] & 193) != 0
                    || payload[1] != 0 || payload[2] != 0 || payload[3] != 0) return false;
                extended = true;
                animated = (payload[0] & 2) != 0;
                allowsAlpha = (payload[0] & 16) != 0;
                width = UInt24(payload[4..]) + 1;
                height = UInt24(payload[7..]) + 1;
                if (!Fits(width, height)) return false;
            }
            else if (kind.SequenceEqual("ANIM"u8))
            {
                if (!animated || animationHeader || hasFrame || payload.Length != 6) return false;
                animationHeader = true;
            }
            else if (kind.SequenceEqual("ANMF"u8))
            {
                if (!animationHeader || payload.Length <= 16 || (payload[15] & 252) != 0) return false;
                var x = UInt24(payload) * 2;
                var y = UInt24(payload[3..]) * 2;
                var frameWidth = UInt24(payload[6..]) + 1;
                var frameHeight = UInt24(payload[9..]) + 1;
                if (!Fits(frameWidth, frameHeight) || x + frameWidth > width || y + frameHeight > height
                    || ++frames > MaxFrames || (ulong)width * height * (uint)frames > MaxAnimationPixels
                    || !WebpAnimationFrame(payload[16..], frameWidth, frameHeight, allowsAlpha)) return false;
                hasFrame = true;
            }
        }
        return position == bytes.Length && hasFrame;
    }

    private static uint UInt24(ReadOnlySpan<byte> bytes) => (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16);

    private static bool ReadWebpChunk(ReadOnlySpan<byte> bytes, ref int position, out ReadOnlySpan<byte> kind, out ReadOnlySpan<byte> payload)
    {
        kind = bytes.Slice(position, 4);
        payload = default;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..]);
        if (size > bytes.Length - position - 8) return false;
        payload = bytes.Slice(position + 8, (int)size);
        position += 8 + (int)size;
        return size % 2 == 0 || (position < bytes.Length && bytes[position++] == 0);
    }

    private static bool WebpAlpha(ReadOnlySpan<byte> payload, uint width, uint height) => payload.Length > 1 && (payload[0] & 192) == 0
        && (payload[0] & 3) <= 1 && ((payload[0] >> 4) & 3) <= 1
        && ((payload[0] & 3) == 1 || payload.Length == 1 + (long)width * height);

    private static bool WebpFrame(ReadOnlySpan<byte> kind, ReadOnlySpan<byte> payload, out uint width, out uint height)
    {
        width = height = 0;
        if (kind.SequenceEqual("VP8 "u8))
        {
            if (payload.Length <= 10 || (payload[0] & 1) != 0 || !payload.Slice(3, 3).SequenceEqual(new byte[] { 157, 1, 42 })) return false;
            width = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 16383);
            height = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 16383);
        }
        else
        {
            if (payload.Length <= 5 || payload[0] != 47 || (payload[4] & 224) != 0) return false;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
            width = (bits & 16383) + 1;
            height = ((bits >> 14) & 16383) + 1;
        }
        return Fits(width, height);
    }

    private static bool WebpAnimationFrame(ReadOnlySpan<byte> bytes, uint width, uint height, bool allowsAlpha)
    {
        var position = 0;
        var image = false;
        var alpha = false;
        while (position <= bytes.Length - 8)
        {
            if (!ReadWebpChunk(bytes, ref position, out var kind, out var payload)) return false;
            if (kind.SequenceEqual("ALPH"u8))
            {
                if (!allowsAlpha || alpha || image || !WebpAlpha(payload, width, height)) return false;
                alpha = true;
            }
            else if (kind.SequenceEqual("VP8 "u8) || kind.SequenceEqual("VP8L"u8))
            {
                if (image || (alpha && kind.SequenceEqual("VP8L"u8)) || !WebpFrame(kind, payload, out var w, out var h)
                    || w != width || h != height) return false;
                image = true;
            }
            else return false;
        }
        return position == bytes.Length && image;
    }
}
