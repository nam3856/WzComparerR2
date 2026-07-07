using System;
using System.Drawing;

namespace WzComparerR2.ImageSearch
{
    internal sealed class RenderedSpineFrame : IDisposable
    {
        public RenderedSpineFrame(Bitmap bitmap, string animationName, int time)
        {
            Bitmap = bitmap;
            AnimationName = animationName;
            Time = time;
        }

        public Bitmap Bitmap { get; }

        public string AnimationName { get; }

        public int Time { get; }

        public void Dispose()
        {
            Bitmap?.Dispose();
        }
    }
}
