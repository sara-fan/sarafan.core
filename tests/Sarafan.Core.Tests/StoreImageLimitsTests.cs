// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Sarafan.Core.Services;

namespace Sarafan.Core.StoreTests;

public sealed class StoreImageLimitsTests
{
    [TestCase(4096u, 1024u, true)]
    [TestCase(4096u, 1025u, false)]
    [TestCase(4097u, 1u, false)]
    [TestCase(1u, 4097u, false)]
    [TestCase(0u, 1u, false)]
    [TestCase(1u, 0u, false)]
    [TestCase(uint.MaxValue, uint.MaxValue, false)]
    public void DimensionsUseOverflowSafeIndependentSideAndPixelBudgets(uint width, uint height, bool valid)
        => Assert.That(StoreImageContent.Fits(width, height), Is.EqualTo(valid));

    [TestCase(24, 3)]
    [TestCase(25, 5)]
    [TestCase(26, 1)]
    [TestCase(27, 1)]
    [TestCase(28, 2)]
    public void InvalidPngHeaderIsRejectedEvenWithCorrectCrc(int offset, byte value)
    {
        var header = StoreServiceTests.Png.AsSpan(16, 13).ToArray(); header[offset - 16] = value;
        Assert.That(StoreImageContent.IsValid("image/png", Png(header, Zlib(new byte[14]))), Is.False);
    }

    [Test]
    public void PngChecksExactBoundedRasterAndAdam7Rows()
    {
        var header = Header(2, 2);
        Assert.That(StoreImageContent.IsValid("image/png", Png(header, Zlib(new byte[14]))), Is.True);
        foreach (var length in new[] { 0, 13, 15, 1000 })
            Assert.That(StoreImageContent.IsValid("image/png", Png(header, Zlib(new byte[length]))), Is.False);
        var badFilter = new byte[14]; badFilter[0] = 5;
        Assert.That(StoreImageContent.IsValid("image/png", Png(header, Zlib(badFilter))), Is.False);
        Assert.That(StoreImageContent.IsValid("image/png", Png(header, [1, 2, 3, 4])), Is.False);
        Assert.That(StoreImageContent.IsValid("image/png", Png(header, Zlib(new byte[14])[..^4])), Is.False, "missing zlib checksum");
        header[12] = 1;
        Assert.That(StoreImageContent.IsValid("image/png", Png(header, Zlib(new byte[15]))), Is.True);
        Assert.That(StoreImageContent.IsValid("image/png", Png(Header(100000, 100000), Zlib(new byte[14]))), Is.False);
        Assert.That(StoreImageContent.IsValid("image/png", Png(Header(4096, 1024), Zlib(new byte[(4096 * 3 + 1) * 1024]))), Is.True);
    }

    [Test]
    public void PngRejectsTrailingBytesAndConcatenatedZlibStreams()
    {
        // The final DEFLATE payload bytes coincidentally equal Adler-32, but no trailer exists.
        byte[] missingTrailer = [0x78, 0x01, 0x01, 0x04, 0x00, 0xFB, 0xFF, 0x03, 0xFB, 0x01, 0xF8];
        Assert.That(StoreImageContent.IsValid("image/png", Png(Header(1, 1), missingTrailer)), Is.False);
        var raster = Zlib(new byte[14]);
        foreach (var suffix in new byte[][] { raster[^4..], Zlib([1]), [0] })
            Assert.That(StoreImageContent.IsValid("image/png", Png(Header(2, 2), [.. raster, .. suffix])), Is.False, Convert.ToHexString(suffix));
        foreach (var kind in new[] { "iCCP", "zTXt", "iTXt" })
        {
            byte[] prefix = kind == "iTXt" ? [65, 0, 1, 0, 0, 0] : [65, 0, 0];
            var metadata = Zlib([65]);
            Assert.That(StoreImageContent.IsValid("image/png", Png(Header(2, 2), raster,
                Chunk(kind, [.. prefix, .. missingTrailer], true))), Is.False);
            foreach (var suffix in new byte[][] { metadata[^4..], Zlib([1]), [0] })
                Assert.That(StoreImageContent.IsValid("image/png", Png(Header(2, 2), raster,
                    Chunk(kind, [.. prefix, .. metadata, .. suffix], true))), Is.False);
        }
    }

    [Test]
    public void PngPaletteOrderingAndStaticOnlyContractAreValidated()
    {
        var palette = Chunk("PLTE", [0, 0, 0], true);
        var indexed = Header(1, 1); indexed[8] = 1; indexed[9] = 3;
        Assert.That(StoreImageContent.IsValid("image/png", Png(indexed, Zlib([0, 0]), palette)), Is.True);
        Assert.That(StoreImageContent.IsValid("image/png", Png(indexed, Zlib([0, 0]))), Is.False);
        foreach (var payload in new byte[][] { [], [1], new byte[9], new byte[771] })
            Assert.That(StoreImageContent.IsValid("image/png", Png(indexed, Zlib([0, 0]), Chunk("PLTE", payload, true))), Is.False);
        Assert.That(StoreImageContent.IsValid("image/png", Png(indexed, Zlib([0, 0]), [.. palette, .. palette])), Is.False);
        foreach (var color in new byte[] { 0, 4, 6 })
        {
            var header = Header(1, 1); header[9] = color;
            var length = color == 0 ? 2 : color == 4 ? 3 : 5;
            Assert.That(StoreImageContent.IsValid("image/png", Png(header, Zlib(new byte[length]))), Is.True);
            if (color != 6) Assert.That(StoreImageContent.IsValid("image/png", Png(header, Zlib(new byte[length]), palette)), Is.False);
        }
        foreach (var kind in new[] { "acTL", "fcTL", "fdAT", "ABCD" })
            Assert.That(StoreImageContent.IsValid("image/png", Png(Header(2, 2), Zlib(new byte[14]), Chunk(kind, [0], true))), Is.False);
        var data = Chunk("IDAT", Zlib(new byte[14]), true);
        var noncontiguous = PngChunks(Header(2, 2), [.. data, .. Chunk("tEXt", [65, 0, 65], true), .. Chunk("IDAT", [], true)]);
        Assert.That(StoreImageContent.IsValid("image/png", noncontiguous), Is.False);
    }

    [Test]
    public void PngCompressedMetadataIsBoundedAcrossChunks()
    {
        foreach (var kind in new[] { "iCCP", "zTXt", "iTXt" })
        {
            byte[] prefix = kind == "iTXt" ? [65, 0, 1, 0, 0, 0] : [65, 0, 0];
            Assert.That(StoreImageContent.IsValid("image/png", Png(Header(2, 2), Zlib(new byte[14]), Chunk(kind, [.. prefix, .. Zlib([65])], true))), Is.True);
            Assert.That(StoreImageContent.IsValid("image/png", Png(Header(2, 2), Zlib(new byte[14]), Chunk(kind, [.. prefix, .. Zlib(new byte[StoreImageContent.MaxMetadataBytes + 1])], true))), Is.False);
            foreach (var payload in new byte[][] { [], [65, 0], [0, 0, 1], [65, 0, 2, 1], [.. prefix, 1, 2, 3, 4] })
                Assert.That(StoreImageContent.IsValid("image/png", Png(Header(2, 2), Zlib(new byte[14]), Chunk(kind, payload, true))), Is.False);
        }
        var metadata = Chunk("zTXt", [65, 0, 0, .. Zlib(new byte[StoreImageContent.MaxMetadataBytes / 2 + 1])], true);
        Assert.That(StoreImageContent.IsValid("image/png", Png(Header(2, 2), Zlib(new byte[14]), [.. metadata, .. metadata])), Is.False);
        foreach (var payload in new byte[][] { [65, 0, 0, 0, 0, 0, 65], [65, 0, 0, 0, 65], [65, 0, 0, 0, 0, 0, .. new byte[StoreImageContent.MaxMetadataBytes + 1]] })
            Assert.That(StoreImageContent.IsValid("image/png", Png(Header(2, 2), Zlib(new byte[14]), Chunk("iTXt", payload, true))), Is.EqualTo(payload.Length == 7));
    }

    [Test]
    public void PngAcceptsLegalSampleDepthsAndRejectsIllegalCombinations()
    {
        foreach (var (color, depths) in new (byte, byte[])[] { (0, [1, 2, 4, 8, 16]), (2, [8, 16]), (3, [1, 2, 4, 8]), (4, [8, 16]), (6, [8, 16]) })
        {
            foreach (var depth in new byte[] { 1, 2, 4, 8, 16, 32 })
            {
                var header = Header(1, 1); header[8] = depth; header[9] = color;
                var channels = color == 2 ? 3 : color == 4 ? 2 : color == 6 ? 4 : 1;
                var palette = color == 3 ? Chunk("PLTE", [0, 0, 0], true) : null;
                Assert.That(StoreImageContent.IsValid("image/png", Png(header, Zlib(new byte[1 + (depth * channels + 7) / 8]), palette)), Is.EqualTo(depths.Contains(depth)), $"color={color}, depth={depth}");
            }
        }
    }

    [Test]
    public void WebpAlphaOrderingAndHeaderFlagsCannotBypassValidation()
    {
        var image = StoreImageFixtures.Webp[12..];
        var alpha = Chunk("ALPH", new byte[5]);
        foreach (var payload in new byte[][] { [], [0], [0, 1], [192, 1], [2, 1], [32, 1] })
            Assert.That(StoreImageContent.IsValid("image/webp", Container([.. Extended(2, 2, 16), .. Chunk("ALPH", payload), .. image])), Is.False);
        foreach (var chunks in new byte[][] { [.. alpha, .. image], [.. Extended(2,2,0), .. alpha, .. image],
            [.. Extended(2,2,16), .. alpha, .. alpha, .. image], [.. Extended(2,2,16), .. image, .. alpha],
            [.. Extended(2,2,18), .. alpha, .. image], [.. Extended(2,2,16), .. alpha, .. StoreImageFixtures.WebpLossless[12..]] })
            Assert.That(StoreImageContent.IsValid("image/webp", Container(chunks)), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container([.. Extended(2, 2, 16), .. Chunk("ALPH", [1, 1]), .. image])), Is.True);
        foreach (var contents in new byte[][] { [.. alpha, .. alpha, .. image], [.. image, .. alpha], [.. Chunk("ALPH", [0]), .. image],
            [.. alpha, .. StoreImageFixtures.WebpLossless[12..]], [.. Chunk("VP8 ", image), 1] })
            Assert.That(StoreImageContent.IsValid("image/webp", Container([.. Extended(2, 2, 18), .. Chunk("ANIM", new byte[6]), .. AnimationFrame(contents)])), Is.False);
        var oversizedChunk = image.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(oversizedChunk.AsSpan(4), uint.MaxValue);
        Assert.That(StoreImageContent.IsValid("image/webp", Container([.. Extended(2, 2, 2), .. Chunk("ANIM", new byte[6]), .. AnimationFrame(oversizedChunk)])), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container([.. Extended(2, 2, 2), .. Chunk("ANIM", new byte[6]), .. AnimationFrame([.. alpha, .. image])])), Is.False);
    }

    [Test]
    public void UploadPathRejectsOversizedDeclaredImagesWithCanonicalContentError()
    {
        var bytes = Png(Header(100000, 100000), Zlib(new byte[14]));
        var failure = Assert.ThrowsAsync<ServiceException>(async () => await StoreRules.ReadLogoAsync(StoreServiceTests.Upload(bytes), default));
        Assert.That(failure!.Code, Is.EqualTo("invalid_store_logo_content"));
    }

    [Test]
    public void EveryWebpDimensionEncodingAndJpegFrameAreBounded()
    {
        var jpeg = StoreImageFixtures.Jpeg;
        var frame = jpeg.AsSpan().IndexOf(new byte[] { 255, 192 });
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(frame + 5), 4097);
        Assert.That(StoreImageContent.IsValid("image/jpeg", jpeg), Is.False);
        var lossy = StoreImageFixtures.Webp;
        BinaryPrimitives.WriteUInt16LittleEndian(lossy.AsSpan(26), 4097);
        Assert.That(StoreImageContent.IsValid("image/webp", lossy), Is.False);
        var lossless = StoreImageFixtures.WebpLossless;
        BinaryPrimitives.WriteUInt32LittleEndian(lossless.AsSpan(21), 4096);
        Assert.That(StoreImageContent.IsValid("image/webp", lossless), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container([.. Extended(4097, 1, 0), .. StoreImageFixtures.Webp[12..]])), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container([.. Extended(3, 2, 0), .. StoreImageFixtures.Webp[12..]])), Is.False);
    }

    [Test]
    public void AnimationRequiresOrderedHeadersMatchingFramesAndBothResourceBudgets()
    {
        var image = StoreImageFixtures.Webp[12..];
        var frame = AnimationFrame(image);
        var anim = Chunk("ANIM", new byte[6]);
        var extended = Extended(2, 2, 2);
        Assert.That(StoreImageContent.IsValid("image/webp", Container(frame)), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container([.. extended, .. frame])), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container([.. extended, .. anim, .. frame])), Is.True);
        foreach (var chunks in new byte[][] {
            [.. anim, .. extended, .. frame], [.. Extended(2,2,0), .. anim, .. frame],
            [.. extended, .. anim, .. anim, .. frame], [.. extended, .. frame, .. anim],
            [.. extended, .. anim, .. image], [.. image, .. image],
            [.. extended, .. Chunk("ANIM", new byte[5]), .. frame],
            [.. Extended(2,2,193), .. image], [.. extended, .. anim, .. AnimationFrame(image, flags:4)],
            [.. extended, .. anim, .. AnimationFrame(image, x:2)],
            [.. extended, .. anim, .. AnimationFrame(image, width:1)],
            [.. extended, .. anim, .. AnimationFrame([.. image, .. image])],
            [.. extended, .. anim, .. AnimationFrame(Chunk("WHAT", [1]))],
            [.. extended, .. anim, .. AnimationFrame([.. image, 1])],
            [.. extended, .. anim, .. AnimationFrame(Chunk("VP8 ", [1]))]
        }) Assert.That(StoreImageContent.IsValid("image/webp", Container(chunks)), Is.False);
        foreach (var count in new[] { 100, 101 })
            Assert.That(StoreImageContent.IsValid("image/webp", Container([.. extended, .. anim, .. Enumerable.Range(0, count).SelectMany(_ => frame)])), Is.EqualTo(count == 100));
        foreach (var count in new[] { 4, 5 })
            Assert.That(StoreImageContent.IsValid("image/webp", Container([.. Extended(2048, 2048, 2), .. anim, .. Enumerable.Range(0, count).SelectMany(_ => frame)])), Is.EqualTo(count == 4));
    }

    private static byte[] Header(uint width, uint height)
    {
        var result = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(result, width);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), height); result[8] = 8; result[9] = 2; return result;
    }
    private static byte[] Zlib(byte[] raw)
    {
        using var buffer = new MemoryStream();
        using (var stream = new ZLibStream(buffer, CompressionLevel.SmallestSize, true)) stream.Write(raw);
        return buffer.ToArray();
    }
    private static byte[] Png(byte[] header, byte[] compressed, byte[]? before = null)
        => PngChunks(header, [.. before ?? [], .. Chunk("IDAT", compressed, true)]);
    private static byte[] PngChunks(byte[] header, byte[] chunks)
        => [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header, true), .. chunks, .. Chunk("IEND", [], true)];
    private static byte[] Chunk(string kind, byte[] payload, bool png = false)
    {
        var result = new byte[8 + payload.Length + (png ? 4 : payload.Length % 2)];
        Encoding.ASCII.GetBytes(kind).CopyTo(result, png ? 4 : 0);
        if (png) BinaryPrimitives.WriteUInt32BigEndian(result, (uint)payload.Length);
        else BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(result, 8);
        if (png)
        {
            var crc = uint.MaxValue;
            foreach (var value in result.AsSpan(4, payload.Length + 4))
            { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), ~crc);
        }
        return result;
    }
    private static byte[] Extended(uint width, uint height, byte flags)
    {
        var bytes = new byte[10]; bytes[0] = flags; Write24(bytes.AsSpan(4), width - 1); Write24(bytes.AsSpan(7), height - 1);
        return Chunk("VP8X", bytes);
    }
    private static byte[] AnimationFrame(byte[] image, uint width = 2, uint x = 0, byte flags = 0)
    {
        var header = new byte[16]; Write24(header, x); Write24(header.AsSpan(6), width - 1); Write24(header.AsSpan(9), 1); header[15] = flags;
        return Chunk("ANMF", [.. header, .. image]);
    }
    private static void Write24(Span<byte> bytes, uint value) { bytes[0] = (byte)value; bytes[1] = (byte)(value >> 8); bytes[2] = (byte)(value >> 16); }
    private static byte[] Container(byte[] chunks)
    {
        byte[] result = [.. "RIFF\0\0\0\0WEBP"u8, .. chunks]; BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length - 8); return result;
    }
}
