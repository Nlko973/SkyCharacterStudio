using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SkyCharacterStudio;

internal static class GameTextureLoader
{
    private const uint KtxEndianLittle = 0x04030201;
    private const uint GlUnsignedByte = 0x1401;
    private const uint GlRgba = 0x1908;
    private const uint GlRgba8 = 0x8058;
    private const uint GlSrgb8Alpha8 = 0x8C43;
    private const uint GlCompressedRedRgtc1 = 0x8DBB;
    private const uint GlCompressedRgbaBptcUnorm = 0x8E8C;
    private const uint GlCompressedSrgbAlphaBptcUnorm = 0x8E8D;
    private const uint DdsFourCcDx10 = 0x30315844;

    private static readonly byte[] KtxIdentifier =
    [
        0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A,
    ];

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();

    public static ImageSource? Load(string texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
        {
            return null;
        }

        lock (CacheGate)
        {
            if (Cache.TryGetValue(texturePath, out var cached)) return cached;
        }

        ImageSource? image = null;
        try
        {
            var ext = Path.GetExtension(texturePath);
            image = ext.Equals(".ktx", StringComparison.OrdinalIgnoreCase)
                ? LoadKtx(texturePath)
                : LoadBitmap(texturePath);
        }
        catch
        {
            image = null;
        }

        lock (CacheGate) Cache[texturePath] = image;
        return image;
    }

    /// <summary>
    /// Wraps a supported KTX1 image and all of its authored mip levels in a
    /// DX10 DDS container. The GPU then samples the original BC blocks and
    /// sRGB format instead of a decoded top mip with regenerated filtering.
    /// </summary>
    public static byte[]? BuildDdsFromKtx(string texturePath, bool isColor)
    {
        var raw = File.ReadAllBytes(texturePath);
        if (raw.Length < 68 || !raw.AsSpan(0, KtxIdentifier.Length).SequenceEqual(KtxIdentifier)
            || ReadUInt32(raw, 12) != KtxEndianLittle)
            return null;

        var glType = ReadUInt32(raw, 16);
        var glFormat = ReadUInt32(raw, 24);
        var internalFormat = ReadUInt32(raw, 28);
        var width = checked((int)ReadUInt32(raw, 36));
        var height = checked((int)ReadUInt32(raw, 40));
        var depth = ReadUInt32(raw, 44);
        var arrayElements = ReadUInt32(raw, 48);
        var faces = ReadUInt32(raw, 52);
        var storedMipCount = Math.Max(1, checked((int)ReadUInt32(raw, 56)));
        var keyValueBytes = checked((int)ReadUInt32(raw, 60));
        if (width <= 0 || height <= 0 || depth != 0 || arrayElements != 0 || faces != 1 || storedMipCount > 16)
            return null;

        var (dxgiFormat, blockBytes, bytesPerPixel) = internalFormat switch
        {
            GlCompressedRgbaBptcUnorm or GlCompressedSrgbAlphaBptcUnorm => (98u, 16, 0),
            GlCompressedRedRgtc1 => (80u, 8, 0),
            GlRgba8 or GlSrgb8Alpha8 when glType == GlUnsignedByte && glFormat == GlRgba => (28u, 0, 4),
            _ => (0u, 0, 0),
        };
        if (dxgiFormat == 0) return null;

        // KTX's transfer-function tag describes how the source was stored, but
        // Avatar slots define how the shader interprets the bytes. Diffuse is
        // sampled as sRGB color; masks, normals and packed attributes are
        // numeric data and must stay linear even when the container says sRGB.
        if (dxgiFormat is 98 or 28)
            dxgiFormat = isColor ? (dxgiFormat == 98 ? 99u : 29u) : dxgiFormat;

        var levels = new List<byte[]>(storedMipCount);
        var cursor = checked(64 + keyValueBytes);
        var levelWidth = width;
        var levelHeight = height;
        var totalDataBytes = 0;
        for (var level = 0; level < storedMipCount; level++)
        {
            if (cursor + 4 > raw.Length) return null;
            var imageSize = checked((int)ReadUInt32(raw, cursor));
            cursor += 4;
            var expectedSize = bytesPerPixel > 0
                ? checked(levelWidth * levelHeight * bytesPerPixel)
                : checked(Math.Max(1, (levelWidth + 3) / 4) * Math.Max(1, (levelHeight + 3) / 4) * blockBytes);
            if (imageSize != expectedSize || cursor + imageSize > raw.Length) return null;
            var mip = new byte[imageSize];
            Buffer.BlockCopy(raw, cursor, mip, 0, imageSize);
            levels.Add(mip);
            totalDataBytes = checked(totalDataBytes + imageSize);
            cursor = checked(cursor + ((imageSize + 3) & ~3));
            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }

        var dds = new byte[checked(148 + totalDataBytes)];
        WriteUInt32(dds, 0, 0x20534444); // "DDS "
        WriteUInt32(dds, 4, 124);
        WriteUInt32(dds, 8, 0x1 | 0x2 | (bytesPerPixel > 0 ? 0x8u : 0x80000u) | 0x1000 | (storedMipCount > 1 ? 0x20000u : 0u));
        WriteUInt32(dds, 12, (uint)height);
        WriteUInt32(dds, 16, (uint)width);
        var topLinearSize = levels[0].Length;
        WriteUInt32(dds, 20, (uint)topLinearSize);
        WriteUInt32(dds, 28, (uint)storedMipCount);
        WriteUInt32(dds, 76, 32);
        WriteUInt32(dds, 80, 0x4); // DDPF_FOURCC
        WriteUInt32(dds, 84, DdsFourCcDx10);
        WriteUInt32(dds, 108, 0x1000 | (storedMipCount > 1 ? 0x400008u : 0u));
        WriteUInt32(dds, 128, dxgiFormat);
        WriteUInt32(dds, 132, 3); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        WriteUInt32(dds, 140, 1); // array size
        var outputCursor = 148;
        foreach (var mip in levels)
        {
            Buffer.BlockCopy(mip, 0, dds, outputCursor, mip.Length);
            outputCursor += mip.Length;
        }
        return dds;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)), value);

    private static ImageSource LoadBitmap(string texturePath)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(texturePath, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static ImageSource? LoadKtx(string texturePath)
    {
        var image = ReadKtx(texturePath);
        if (image is null)
        {
            return null;
        }

        byte[]? bgra = image.InternalFormat switch
        {
            GlRgba8 or GlSrgb8Alpha8 when image.GlType == GlUnsignedByte && image.GlFormat == GlRgba => SwizzleRgbaToBgra(image.Data),
            GlCompressedRgbaBptcUnorm or GlCompressedSrgbAlphaBptcUnorm => DecodeCompressedKtx(image),
            GlCompressedRedRgtc1 => DecodeCompressedKtx(image),
            _ => null,
        };

        if (bgra is null || bgra.Length != image.Width * image.Height * 4)
        {
            return null;
        }

        var bitmap = BitmapSource.Create(
            image.Width,
            image.Height,
            96.0,
            96.0,
            PixelFormats.Bgra32,
            null,
            bgra,
            image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static KtxImage? ReadKtx(string texturePath)
    {
        var raw = File.ReadAllBytes(texturePath);
        if (raw.Length < 68 || !raw.AsSpan(0, KtxIdentifier.Length).SequenceEqual(KtxIdentifier))
        {
            return null;
        }

        if (ReadUInt32(raw, 12) != KtxEndianLittle)
        {
            return null;
        }

        var glType = ReadUInt32(raw, 16);
        var glFormat = ReadUInt32(raw, 24);
        var internalFormat = ReadUInt32(raw, 28);
        var width = checked((int)ReadUInt32(raw, 36));
        var height = checked((int)ReadUInt32(raw, 40));
        var depth = ReadUInt32(raw, 44);
        var arrayElements = ReadUInt32(raw, 48);
        var faces = ReadUInt32(raw, 52);
        var keyValueBytes = checked((int)ReadUInt32(raw, 60));

        if (width <= 0 || height <= 0 || depth != 0 || arrayElements != 0 || faces != 1)
        {
            return null;
        }

        var imageSizeOffset = 64 + keyValueBytes;
        if (imageSizeOffset + 4 > raw.Length)
        {
            return null;
        }

        var imageSize = checked((int)ReadUInt32(raw, imageSizeOffset));
        var imageOffset = imageSizeOffset + 4;
        if (imageSize < 0 || imageOffset + imageSize > raw.Length)
        {
            return null;
        }

        var data = new byte[imageSize];
        Buffer.BlockCopy(raw, imageOffset, data, 0, imageSize);

        return new KtxImage(width, height, glType, glFormat, internalFormat, data);
    }

    private static byte[] SwizzleRgbaToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }

        return bgra;
    }

    private static byte[]? DecodeCompressedKtx(KtxImage image)
    {
        var format = image.InternalFormat switch
        {
            GlCompressedRgbaBptcUnorm or GlCompressedSrgbAlphaBptcUnorm => CompressionFormat.Bc7,
            GlCompressedRedRgtc1 => CompressionFormat.Bc4,
            _ => CompressionFormat.Unknown,
        };

        if (format == CompressionFormat.Unknown)
        {
            return null;
        }

        try
        {
            var pixels = new BcDecoder().DecodeRaw(image.Data, image.Width, image.Height, format);
            if (pixels.Length != image.Width * image.Height)
            {
                return null;
            }

            return RgbaToBgra(pixels);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Texture decoder failed: format=0x{image.InternalFormat:X} size={image.Width}x{image.Height} error={ex.Message}");
            return null;
        }
    }

    private static byte[] RgbaToBgra(ColorRgba32[] pixels)
    {
        var bgra = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];
            var offset = i * 4;
            bgra[offset] = pixel.b;
            bgra[offset + 1] = pixel.g;
            bgra[offset + 2] = pixel.r;
            bgra[offset + 3] = pixel.a;
        }

        return bgra;
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        return BitConverter.ToUInt32(data, offset);
    }

    private sealed record KtxImage(
        int Width,
        int Height,
        uint GlType,
        uint GlFormat,
        uint InternalFormat,
        byte[] Data);
}

