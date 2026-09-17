// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Buffers.Binary;
using Sarafan.Core.Services;

namespace Sarafan.Core.StoreTests;

public sealed class StoreImageContentTests
{
    private static IEnumerable<TestCaseData> Images()
    {
        yield return new("image/png", StoreServiceTests.Png);
        yield return new("image/jpeg", StoreImageFixtures.Jpeg);
        yield return new("image/webp", StoreImageFixtures.Webp);
        yield return new("image/webp", StoreImageFixtures.WebpLossless);
        yield return new("image/webp", StoreImageFixtures.WebpExtended);
        yield return new("image/webp", StoreImageFixtures.WebpAnimated);
    }

    [TestCaseSource(nameof(Images))]
    public void RealFramesAreAcceptedButTruncationAndTrailingDataAreRejected(string type, byte[] bytes)
    {
        Assert.That(StoreImageContent.IsValid(type, bytes), Is.True);
        for (var size = 0; size < bytes.Length; size++)
            Assert.That(StoreImageContent.IsValid(type, bytes.AsSpan(0, size)), Is.False, $"truncated at {size}");
        Assert.That(StoreImageContent.IsValid(type, [.. bytes, 1]), Is.False);
    }

    [Test]
    public void PngRequiresDataAndCompleteBoundedChunks()
    {
        var png = StoreServiceTests.Png;
        Assert.That(StoreImageContent.IsValid("image/png", [.. png[..33], .. png[^12..]]), Is.False);
        Assert.That(StoreImageContent.IsValid("image/png", [.. png[..33], .. png[8..33], .. png[33..]]), Is.False);
        foreach (var offset in new[] { 8, 16, 20, 33, png.Length - 12 })
        {
            var invalid = png.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(offset), uint.MaxValue);
            Assert.That(StoreImageContent.IsValid("image/png", invalid), Is.False);
        }
        var noEnd = png.ToArray(); noEnd[^8] = (byte)'X';
        Assert.That(StoreImageContent.IsValid("image/png", noEnd), Is.False);
        foreach (var offset in new[] { 32, png.Length - 13, png.Length - 1 })
        {
            var badCrc = png.ToArray(); badCrc[offset] ^= 1;
            Assert.That(StoreImageContent.IsValid("image/png", badCrc), Is.False, $"CRC at {offset}");
        }
        Assert.That(StoreImageContent.IsValid("image/svg+xml", png), Is.False);
    }

    [Test]
    public void JpegRequiresFrameDimensionsAndScanWithData()
    {
        var jpeg = StoreImageFixtures.Jpeg;
        var frame = Find(jpeg, [255, 192]);
        var scan = Find(jpeg, [255, 218]);
        foreach (var offset in new[] { frame + 5, frame + 7 })
        {
            var invalid = jpeg.ToArray(); invalid[offset] = 0; invalid[offset + 1] = 0;
            Assert.That(StoreImageContent.IsValid("image/jpeg", invalid), Is.False);
        }
        foreach (var (offset, value) in new[] { (2, 0), (3, 216), (3, 208), (4, 255), (5, 0), (frame + 9, 0), (frame + 3, 2), (scan + 4, 0), (scan + 3, 2) })
        {
            var invalid = jpeg.ToArray(); invalid[offset] = (byte)value;
            Assert.That(StoreImageContent.IsValid("image/jpeg", invalid), Is.False, $"offset {offset}");
        }
        Assert.That(StoreImageContent.IsValid("image/jpeg", [255, 216, .. jpeg[scan..]]), Is.False);
        var scanStart = scan + 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(scan + 2));
        Assert.That(StoreImageContent.IsValid("image/jpeg", [.. jpeg[..scanStart], 255, 217]), Is.False);
        Assert.That(StoreImageContent.IsValid("image/jpeg", [.. jpeg[..scanStart], 1, 255]), Is.False);
        Assert.That(StoreImageContent.IsValid("image/jpeg", [.. jpeg[..scanStart], 1, 255, 0, 255, 208, 255, 255, 217]), Is.True);
        Assert.That(StoreImageContent.IsValid("image/jpeg", [255, 216, 255, 255]), Is.False);
    }

    [Test]
    public void WebpRequiresBoundedImagePayloadAndValidKeyframeHeaders()
    {
        foreach (var (source, offset, value) in new[]
        {
            (StoreImageFixtures.Webp, 16, 255), (StoreImageFixtures.Webp, 20, 1),
            (StoreImageFixtures.Webp, 23, 0), (StoreImageFixtures.Webp, 26, 0), (StoreImageFixtures.Webp, 28, 0),
            (StoreImageFixtures.WebpLossless, 20, 0), (StoreImageFixtures.WebpLossless, 24, 255),
            (StoreImageFixtures.WebpLossless, 37, 1), (StoreImageFixtures.WebpExtended, 16, 9)
        })
        {
            source[offset] = (byte)value;
            Assert.That(StoreImageContent.IsValid("image/webp", source), Is.False, $"offset {offset}");
        }
        var frame = StoreImageFixtures.Webp[12..];
        var withoutFrame = frame.ToArray(); withoutFrame[0] = (byte)'X';
        Assert.That(StoreImageContent.IsValid("image/webp", Container(withoutFrame)), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container([.. frame, 1])), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container(Chunk("ANMF", new byte[16]))), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container(Chunk("ANMF", [.. new byte[16], .. Chunk("ANMF", [.. new byte[16], .. frame])]))), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container(Chunk("ANMF", [.. new byte[16], .. Chunk("VP8X", new byte[10]), .. frame]))), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container([.. frame, .. Chunk("VP8X", new byte[10])])), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container(Chunk("VP8 ", new byte[10]))), Is.False);
        Assert.That(StoreImageContent.IsValid("image/webp", Container(Chunk("VP8L", new byte[5]))), Is.False);
        var missingPad = Chunk("VP8L", StoreImageFixtures.WebpLossless.AsSpan(20, 17).ToArray())[..^1];
        Assert.That(StoreImageContent.IsValid("image/webp", Container(missingPad)), Is.False);
    }

    private static int Find(byte[] bytes, byte[] marker) => bytes.AsSpan().IndexOf(marker);
    private static byte[] Chunk(string kind, byte[] payload)
    {
        var bytes = new byte[8 + payload.Length + payload.Length % 2];
        System.Text.Encoding.ASCII.GetBytes(kind).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);
        return bytes;
    }
    private static byte[] Container(byte[] chunks)
    {
        byte[] bytes = [.. "RIFF\0\0\0\0WEBP"u8, .. chunks];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        return bytes;
    }
}
