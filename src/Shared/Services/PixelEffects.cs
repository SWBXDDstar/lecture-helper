using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LectureHelper.Services;

public enum EffectKind { Mosaic, Blur }

/// <summary>
/// Produces pixelated / blurred copies of the screenshot.
/// Work is confined to the rectangle actually being drawn, never the whole canvas — on ScreenNote the
/// canvas is the entire desktop, and blurring two million pixels per wheel notch would stall a lesson.
/// </summary>
public sealed class EffectCache
{
    /// <summary>Enough for a handful of committed rectangles plus the one under the cursor.</summary>
    private const int MaxEntries = 24;

    private readonly BitmapSource _source;
    private readonly Dictionary<(int X, int Y, int W, int H, EffectKind Kind, int Strength), BitmapSource> _cache = new();
    private readonly Queue<(int, int, int, int, EffectKind, int)> _order = new();

    public EffectCache(BitmapSource source) => _source = source;

    /// <summary>Returns the effect for <paramref name="area"/>, or null when the rectangle is degenerate.</summary>
    public BitmapSource? Get(Rect area, EffectKind kind, int strength)
    {
        Int32Rect region = Clamp(area);
        if (region.Width <= 0 || region.Height <= 0) return null;

        var key = (region.X, region.Y, region.Width, region.Height, kind, strength);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        int stride = region.Width * 4;
        var pixels = new byte[stride * region.Height];
        _source.CopyPixels(region, pixels, stride, 0);

        byte[] output = kind == EffectKind.Mosaic
            ? Pixelate(pixels, region.Width, region.Height, stride, strength)
            : BoxBlur(pixels, region.Width, region.Height, stride, strength);

        var bitmap = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgra32, null, output, stride);
        bitmap.Freeze();

        Store(key, bitmap);
        return bitmap;
    }

    /// <summary>Where <see cref="Get"/> actually sampled, after clamping to the screenshot.</summary>
    public Rect ClampedArea(Rect area)
    {
        Int32Rect region = Clamp(area);
        return new Rect(region.X, region.Y, region.Width, region.Height);
    }

    private void Store((int, int, int, int, EffectKind, int) key, BitmapSource bitmap)
    {
        _cache[key] = bitmap;
        _order.Enqueue(key);

        while (_order.Count > MaxEntries)
            _cache.Remove(_order.Dequeue());
    }

    private Int32Rect Clamp(Rect rect)
    {
        int x = Math.Max(0, (int)Math.Floor(rect.X));
        int y = Math.Max(0, (int)Math.Floor(rect.Y));
        int right = Math.Min(_source.PixelWidth, (int)Math.Ceiling(rect.Right));
        int bottom = Math.Min(_source.PixelHeight, (int)Math.Ceiling(rect.Bottom));
        return new Int32Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static byte[] Pixelate(byte[] src, int width, int height, int stride, int block)
    {
        block = Math.Max(2, block);
        var dst = new byte[src.Length];

        for (int by = 0; by < height; by += block)
        {
            int yEnd = Math.Min(by + block, height);
            for (int bx = 0; bx < width; bx += block)
            {
                int xEnd = Math.Min(bx + block, width);
                long b = 0, g = 0, r = 0, a = 0;
                int count = (yEnd - by) * (xEnd - bx);

                for (int y = by; y < yEnd; y++)
                {
                    int row = y * stride;
                    for (int x = bx; x < xEnd; x++)
                    {
                        int i = row + x * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3];
                    }
                }

                byte mb = (byte)(b / count), mg = (byte)(g / count), mr = (byte)(r / count), ma = (byte)(a / count);
                for (int y = by; y < yEnd; y++)
                {
                    int row = y * stride;
                    for (int x = bx; x < xEnd; x++)
                    {
                        int i = row + x * 4;
                        dst[i] = mb; dst[i + 1] = mg; dst[i + 2] = mr; dst[i + 3] = ma;
                    }
                }
            }
        }
        return dst;
    }

    private static byte[] BoxBlur(byte[] src, int width, int height, int stride, int radius)
    {
        radius = Math.Clamp(radius, 1, Math.Max(1, Math.Min(width, height) / 2));
        var a = (byte[])src.Clone();
        var b = new byte[src.Length];

        // Two passes approximate a Gaussian closely enough at these radii.
        for (int pass = 0; pass < 2; pass++)
        {
            BlurHorizontal(a, b, width, height, stride, radius);
            BlurVertical(b, a, width, height, stride, radius);
        }
        return a;
    }

    private static void BlurHorizontal(byte[] src, byte[] dst, int width, int height, int stride, int radius)
    {
        int window = radius * 2 + 1;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            long c0 = 0, c1 = 0, c2 = 0, c3 = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int i = row + Math.Clamp(k, 0, width - 1) * 4;
                c0 += src[i]; c1 += src[i + 1]; c2 += src[i + 2]; c3 += src[i + 3];
            }

            for (int x = 0; x < width; x++)
            {
                int o = row + x * 4;
                dst[o] = (byte)(c0 / window);
                dst[o + 1] = (byte)(c1 / window);
                dst[o + 2] = (byte)(c2 / window);
                dst[o + 3] = (byte)(c3 / window);

                int add = row + Math.Clamp(x + radius + 1, 0, width - 1) * 4;
                int sub = row + Math.Clamp(x - radius, 0, width - 1) * 4;
                c0 += src[add] - src[sub];
                c1 += src[add + 1] - src[sub + 1];
                c2 += src[add + 2] - src[sub + 2];
                c3 += src[add + 3] - src[sub + 3];
            }
        }
    }

    private static void BlurVertical(byte[] src, byte[] dst, int width, int height, int stride, int radius)
    {
        int window = radius * 2 + 1;
        for (int x = 0; x < width; x++)
        {
            int col = x * 4;
            long c0 = 0, c1 = 0, c2 = 0, c3 = 0;

            for (int k = -radius; k <= radius; k++)
            {
                int i = Math.Clamp(k, 0, height - 1) * stride + col;
                c0 += src[i]; c1 += src[i + 1]; c2 += src[i + 2]; c3 += src[i + 3];
            }

            for (int y = 0; y < height; y++)
            {
                int o = y * stride + col;
                dst[o] = (byte)(c0 / window);
                dst[o + 1] = (byte)(c1 / window);
                dst[o + 2] = (byte)(c2 / window);
                dst[o + 3] = (byte)(c3 / window);

                int add = Math.Clamp(y + radius + 1, 0, height - 1) * stride + col;
                int sub = Math.Clamp(y - radius, 0, height - 1) * stride + col;
                c0 += src[add] - src[sub];
                c1 += src[add + 1] - src[sub + 1];
                c2 += src[add + 2] - src[sub + 2];
                c3 += src[add + 3] - src[sub + 3];
            }
        }
    }
}
