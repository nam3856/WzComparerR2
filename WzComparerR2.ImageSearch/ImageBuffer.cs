using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WzComparerR2.ImageSearch
{
    internal sealed class ImageBuffer
    {
        private ImageBuffer(int width, int height, byte[] bgra)
        {
            Width = width;
            Height = height;
            Bgra = bgra;
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Bgra { get; }

        public static ImageBuffer FromBitmap(Bitmap source)
        {
            return FromBitmap(source, source.Width, source.Height);
        }

        public static ImageBuffer FromBitmap(Bitmap source, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));
            }

            using (Bitmap normalized = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(normalized))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(source, new Rectangle(0, 0, normalized.Width, normalized.Height));
                }

                Rectangle rect = new Rectangle(0, 0, normalized.Width, normalized.Height);
                BitmapData data = normalized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = Math.Abs(data.Stride);
                    byte[] pixels = new byte[normalized.Width * normalized.Height * 4];
                    byte[] row = new byte[stride];

                    for (int y = 0; y < normalized.Height; y++)
                    {
                        IntPtr rowPtr = data.Scan0 + (data.Stride < 0 ? normalized.Height - 1 - y : y) * data.Stride;
                        Marshal.Copy(rowPtr, row, 0, stride);
                        Buffer.BlockCopy(row, 0, pixels, y * normalized.Width * 4, normalized.Width * 4);
                    }

                    return new ImageBuffer(normalized.Width, normalized.Height, pixels);
                }
                finally
                {
                    normalized.UnlockBits(data);
                }
            }
        }
    }
}
