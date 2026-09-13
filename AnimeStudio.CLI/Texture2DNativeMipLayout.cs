using System;
using System.Collections.Generic;
using System.IO;
using AnimeStudio;

namespace AnimeStudio.CLI;

internal readonly record struct Texture2DMipRange(
    int Mip,
    int Width,
    int Height,
    int Offset,
    int ByteSize);

internal static class Texture2DNativeMipLayout
{
    public static bool TryCreate(
        TextureFormat format,
        int width,
        int height,
        int mipCount,
        int mipsStripped,
        int imageCount,
        int textureDimension,
        int payloadLength,
        out IReadOnlyList<Texture2DMipRange> mipRanges)
    {
        mipRanges = Array.Empty<Texture2DMipRange>();
        var bytesPerBlock = format switch
        {
            TextureFormat.BC5 or TextureFormat.BC7 => 16,
            _ => 0,
        };
        if (bytesPerBlock == 0)
        {
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Texture dimensions must be positive; got {width}x{height}.");
        }
        if (mipCount <= 0)
        {
            throw new InvalidDataException($"Mip count must be positive; got {mipCount}.");
        }
        var maximumMipCount = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        if (mipCount > maximumMipCount)
        {
            throw new InvalidDataException(
                $"Mip count {mipCount} exceeds the {maximumMipCount}-level chain for {width}x{height}.");
        }
        if (mipsStripped != 0 || imageCount != 1 || textureDimension != 2)
        {
            throw new InvalidDataException(
                $"Mip layout requires an unstripped 2D single-image texture; got stripped={mipsStripped}, " +
                $"images={imageCount}, dimension={textureDimension}.");
        }
        if (payloadLength < 0)
        {
            throw new InvalidDataException($"Payload length cannot be negative; got {payloadLength}.");
        }

        var ranges = new List<Texture2DMipRange>(mipCount);
        var offset = 0;
        for (var mip = 0; mip < mipCount; mip++)
        {
            var mipWidth = Math.Max(1, width >> mip);
            var mipHeight = Math.Max(1, height >> mip);
            var blocksWide = Math.Max(1, checked((mipWidth + 3) / 4));
            var blocksHigh = Math.Max(1, checked((mipHeight + 3) / 4));
            var byteSize = checked(checked(blocksWide * blocksHigh) * bytesPerBlock);
            ranges.Add(new Texture2DMipRange(mip, mipWidth, mipHeight, offset, byteSize));
            offset = checked(offset + byteSize);
        }

        if (offset != payloadLength)
        {
            throw new InvalidDataException(
                $"{format} mip layout totals {offset} bytes, but the original resource payload has {payloadLength} bytes.");
        }

        mipRanges = ranges;
        return true;
    }
}
