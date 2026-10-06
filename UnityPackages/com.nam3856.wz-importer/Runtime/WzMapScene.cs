using UnityEngine;
using WzUnity;

namespace WzComparerR2.Unity
{
    public sealed class WzMapScene : MonoBehaviour
    {
        public string mapId;
        public string sourcePath;
        public float pixelsPerUnit = 100;
        public Rect sourceBounds;
    }
}
