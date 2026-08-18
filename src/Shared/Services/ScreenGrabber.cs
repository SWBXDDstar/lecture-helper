using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LectureHelper.Native;

namespace LectureHelper.Services;

public static class ScreenGrabber
{
    /// <summary>Grabs the whole virtual desktop as a 96-dpi Bgra32 bitmap sized in physical pixels.</summary>
    public static BitmapSource CaptureVirtualScreen(out NativeMethods.RECT bounds)
    {
        bounds = NativeMethods.GetVirtualScreen();

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
        }
        return ToBitmapSource(bitmap);
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(
                bitmap.Width, bitmap.Height, 96, 96,
                PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bitmap.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
