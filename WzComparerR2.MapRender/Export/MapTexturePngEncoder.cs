using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework.Graphics;
using WzComparerR2.Animation;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;

namespace WzComparerR2.MapRender.Export
{
    internal static class MapTexturePngEncoder
    {
        public static EncodedPng Encode(Frame frame)
        {
            if (frame?.Texture == null)
            {
                return EncodePixels(new[] { XnaColor.Transparent }, 1, 1);
            }

            Texture2D texture = frame.Texture;
            XnaRectangle source = frame.AtlasRect ?? new XnaRectangle(0, 0, texture.Width, texture.Height);
            if (source.Width <= 0 || source.Height <= 0 || source.Left < 0 || source.Top < 0
                || source.Right > texture.Width || source.Bottom > texture.Height)
            {
                throw new InvalidDataException("The animation frame has an invalid texture rectangle.");
            }

            // Rendering into a Color target handles compressed and packed WZ texture formats
            // consistently. BlendState.Opaque copies RGB and alpha without compositing.
            GraphicsDevice device = texture.GraphicsDevice;
            RenderTargetBinding[] previousTargets = device.GetRenderTargets();
            Viewport previousViewport = device.Viewport;
            XnaRectangle previousScissor = device.ScissorRectangle;
            var pixels = new XnaColor[source.Width * source.Height];
            using (var target = new RenderTarget2D(device, source.Width, source.Height, false, SurfaceFormat.Color, DepthFormat.None))
            using (var spriteBatch = new SpriteBatch(device))
            {
                try
                {
                    device.SetRenderTarget(target);
                    device.Clear(XnaColor.Transparent);
                    spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
                        DepthStencilState.None, RasterizerState.CullNone);
                    spriteBatch.Draw(texture, new XnaRectangle(0, 0, source.Width, source.Height), source, XnaColor.White);
                    spriteBatch.End();
                    target.GetData(pixels);
                }
                finally
                {
                    device.SetRenderTargets(previousTargets);
                    device.Viewport = previousViewport;
                    device.ScissorRectangle = previousScissor;
                }
            }

            return EncodePixels(pixels, source.Width, source.Height);
        }

        public static EncodedPng EncodeBgra32(Texture2D texture)
        {
            if (texture == null) throw new ArgumentNullException(nameof(texture));
            if (texture.Width <= 0 || texture.Height <= 0)
            {
                throw new InvalidDataException("The texture has invalid dimensions.");
            }

            var bytes = new byte[checked(texture.Width * texture.Height * 4)];
            texture.GetData(bytes);
            return EncodeBgraPixels(bytes, texture.Width, texture.Height);
        }

        private static EncodedPng EncodePixels(XnaColor[] pixels, int width, int height)
        {
            var bytes = new byte[checked(width * height * 4)];
            for (int i = 0; i < pixels.Length; i++)
            {
                int offset = i * 4;
                XnaColor color = pixels[i];
                bytes[offset] = color.B;
                bytes[offset + 1] = color.G;
                bytes[offset + 2] = color.R;
                bytes[offset + 3] = color.A;
            }

            return EncodeBgraPixels(bytes, width, height);
        }

        private static EncodedPng EncodeBgraPixels(byte[] bytes, int width, int height)
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(bytes, y * width * 4, bitmapData.Scan0 + y * bitmapData.Stride, width * 4);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    return new EncodedPng(stream.ToArray(), width, height);
                }
            }
        }

        internal sealed class EncodedPng
        {
            public EncodedPng(byte[] bytes, int width, int height)
            {
                Bytes = bytes;
                Width = width;
                Height = height;
            }

            public byte[] Bytes { get; }
            public int Width { get; }
            public int Height { get; }
        }
    }
}
