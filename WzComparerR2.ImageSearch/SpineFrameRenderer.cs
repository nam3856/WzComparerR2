using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WzComparerR2.Animation;
using WzComparerR2.Controls;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace WzComparerR2.ImageSearch
{
    internal sealed class SpineFrameRenderer
    {
        private readonly PluginContext context;
        private global::WzComparerR2.PictureBoxEx pictureBoxEx;

        public SpineFrameRenderer(PluginContext context)
        {
            this.context = context;
        }

        public List<RenderedSpineFrame> RenderFrames(Wz_Node sourceNode, int samplesPerAnimation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            global::WzComparerR2.PictureBoxEx pictureBox = EnsurePictureBox();
            if (pictureBox == null || sourceNode == null)
            {
                return new List<RenderedSpineFrame>();
            }

            try
            {
                if (pictureBox.InvokeRequired)
                {
                    return (List<RenderedSpineFrame>)pictureBox.Invoke(new Func<List<RenderedSpineFrame>>(
                        () => RenderFramesCore(pictureBox, sourceNode, samplesPerAnimation, cancellationToken)));
                }

                return RenderFramesCore(pictureBox, sourceNode, samplesPerAnimation, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new List<RenderedSpineFrame>();
            }
        }

        private global::WzComparerR2.PictureBoxEx EnsurePictureBox()
        {
            if (pictureBoxEx != null && !pictureBoxEx.IsDisposed)
            {
                return pictureBoxEx;
            }

            Control[] controls = context.MainForm.Controls.Find("pictureBoxEx1", true);
            pictureBoxEx = controls.OfType<global::WzComparerR2.PictureBoxEx>().FirstOrDefault();
            return pictureBoxEx;
        }

        private static List<RenderedSpineFrame> RenderFramesCore(
            global::WzComparerR2.PictureBoxEx pictureBox,
            Wz_Node sourceNode,
            int samplesPerAnimation,
            CancellationToken cancellationToken)
        {
            List<RenderedSpineFrame> renderedFrames = new List<RenderedSpineFrame>();
            ISpineAnimationData spineData = pictureBox.LoadSpineAnimation(sourceNode);
            AnimationItem spineItem = spineData?.CreateAnimator() as AnimationItem;
            try
            {
                if (!(spineItem is ISpineAnimator spineAnimator))
                {
                    return renderedFrames;
                }

                IEnumerable<string> animationNames = spineAnimator.Animations.Count > 0
                    ? spineAnimator.Animations
                    : new string[] { null };

                foreach (string animationName in animationNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    spineAnimator.SelectedAnimationName = animationName;
                    int length = Math.Max(0, spineItem.Length);

                    foreach (int time in GetSampleTimes(length, samplesPerAnimation))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FrameAnimationData frameData = null;
                        try
                        {
                            frameData = pictureBox.CaptureAnimation(
                                new AnimationItem[] { spineItem },
                                new Tuple<int, int>[] { Tuple.Create(0, length) },
                                time);

                            Bitmap bitmap = CreateBitmap(frameData);
                            if (bitmap != null)
                            {
                                renderedFrames.Add(new RenderedSpineFrame(bitmap, animationName, time));
                            }
                        }
                        finally
                        {
                            if (frameData != null)
                            {
                                pictureBox.DisposeAnimationItem(new FrameAnimator(frameData));
                            }
                        }
                    }
                }
            }
            finally
            {
                if (spineItem != null)
                {
                    pictureBox.DisposeAnimationItem(spineItem);
                }
            }

            return renderedFrames;
        }

        private static IEnumerable<int> GetSampleTimes(int length, int samplesPerAnimation)
        {
            int sampleCount = Math.Max(1, samplesPerAnimation);
            if (length <= 0 || sampleCount == 1)
            {
                yield return 0;
                yield break;
            }

            for (int i = 0; i < sampleCount; i++)
            {
                yield return Math.Min(length, length * i / sampleCount);
            }
        }

        private static Bitmap CreateBitmap(FrameAnimationData frameData)
        {
            Frame frame = frameData?.Frames.FirstOrDefault();
            if (frame?.Texture == null || frame.Texture.Width <= 0 || frame.Texture.Height <= 0)
            {
                return null;
            }

            int width = frame.Texture.Width;
            int height = frame.Texture.Height;
            byte[] pixels = new byte[width * height * 4];
            frame.Texture.GetData(pixels);

            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            Rectangle rect = new Rectangle(0, 0, width, height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < height; y++)
                {
                    IntPtr rowPtr = data.Scan0 + y * data.Stride;
                    Marshal.Copy(pixels, y * width * 4, rowPtr, width * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }
    }
}
