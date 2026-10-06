using System;
using System.Collections.Generic;

namespace WzUnity
{
    // Engine-independent contract shared by the exporter and Unity package.
    // All positions and origins are in WZ pixels (positive Y points down).
    [Serializable] public sealed class WzUnityManifest
    {
        public int schemaVersion = 1;
        public string id;
        public string kind;
        public string sourcePath;
        public float pixelsPerUnit = 100;
        public List<WzPngAsset> assets = new List<WzPngAsset>();
        public List<WzEntity> entities = new List<WzEntity>();
        public WzMap map;
        public List<WzExportWarning> warnings = new List<WzExportWarning>();
    }
    [Serializable] public sealed class WzPngAsset
    {
        public string id;
        public string file;
        public int width;
        public int height;
        public string sha256;
    }
    [Serializable] public sealed class WzExportWarning
    {
        public string sourcePath;
        public string reason;
    }
    [Serializable] public sealed class WzMetadata
    {
        public string key;
        public string value;
    }
    [Serializable] public sealed class WzEntity
    {
        public string id;
        public string kind;
        public string sourcePath;
        public string displayName;
        public string defaultAction;
        // False means an older export or a source that did not supply outfit data.
        public bool hasEquipmentMetadata;
        public List<WzEquippedItem> equipment = new List<WzEquippedItem>();
        public List<WzMetadata> metadata = new List<WzMetadata>();
        public List<WzAnimationClip> clips = new List<WzAnimationClip>();
    }
    [Serializable] public sealed class WzEquippedItem
    {
        public int slotIndex;
        public string slot;
        public string itemId;
        public string name;
        public string nameSourcePath;
        public string sourcePath;
        public string islot;
        public string vslot;
        public bool visible;
        public bool effectVisible;
        public bool hasImage;
        public bool isSkill;
        public bool isIllusionRing;
        public bool illusionRingClassificationKnown;
        public string illusionRingSourcePath;
        // Original scalar info fields, including an explicit illusionGrade when present.
        public List<WzMetadata> metadata = new List<WzMetadata>();
    }
    [Serializable] public sealed class WzAnimationClip
    {
        public string name;
        public bool loop = true;
        public double durationMs;
        public List<WzAnimationTrack> tracks = new List<WzAnimationTrack>();
    }
    [Serializable] public sealed class WzAnimationTrack
    {
        public string id;
        public string kind;
        public string slot;
        public string itemId;
        public List<WzMetadata> metadata = new List<WzMetadata>();
        public bool loop = true;
        public double startMs;
        public List<WzSpriteFrame> frames = new List<WzSpriteFrame>();
        // Optional independent track whose frame index selects anchor poses.
        public string poseTrack;
        public List<WzTrackPose> poses = new List<WzTrackPose>();
    }
    [Serializable] public sealed class WzTrackPose
    {
        public int poseFrame;
        public int frameIndex = -1;
        public bool overrideSprite;
        public string assetId;
        public float originX;
        public float originY;
        public float x;
        public float y;
        public int z;
        public int drawOrder;
        public bool visible = true;
    }
    [Serializable] public sealed class WzSpriteFrame
    {
        public string assetId;
        public double delayMs = 100;
        public float originX;
        public float originY;
        public float x;
        public float y;
        public float scaleX = 1;
        public float scaleY = 1;
        public float rotation;
        public int z;
        public int drawOrder;
        public int a0 = 255;
        public int a1 = 255;
        public bool visible = true;
        public string blend = "normal";
        public string sourcePath;
    }
    [Serializable] public sealed class WzMap
    {
        public string id;
        public float left;
        public float top;
        public float width;
        public float height;
        public List<WzMapLayer> layers = new List<WzMapLayer>();
        public List<WzFoothold> footholds = new List<WzFoothold>();
        public List<WzLadder> ladders = new List<WzLadder>();
        public List<WzPortal> portals = new List<WzPortal>();
        public List<WzPlacement> placements = new List<WzPlacement>();
    }
    [Serializable] public sealed class WzMapLayer
    {
        public string id;
        public string group;
        public string sourcePath;
        public string entityId;
        public string action;
        public int containerOrder;
        public int order;
        public int z;
        public float x;
        public float y;
        public bool flip;
        public float alpha = 1;
        public WzBackground background;
        public WzMotion motion;
    }
    [Serializable] public sealed class WzBackground
    {
        public int type;
        public float rx;
        public float ry;
        public float cx;
        public float cy;
        public bool repeatX;
        public bool repeatY;
        public bool scrollX;
        public bool scrollY;
        public bool parallax = true;
        public float scrollDistanceX = 100;
        public float scrollDistanceY = 100;
    }
    [Serializable] public sealed class WzMotion
    {
        public WzMotionChannel x;
        public WzMotionChannel y;
        public WzMotionChannel opacity;
        public WzMotionChannel scaleX;
    }
    [Serializable] public sealed class WzMotionChannel
    {
        public string kind;
        public bool loop = true;
        public double cycleMs;
        public double phaseMs;
        public float offset;
        public float amplitude;
        public bool pixelSnap;
        public List<WzMotionKey> keys = new List<WzMotionKey>();
    }
    [Serializable] public sealed class WzMotionKey
    {
        public double timeMs;
        public float value;
        public string interpolation = "linear";
    }
    [Serializable] public sealed class WzFoothold
    {
        public int id;
        public int prev;
        public int next;
        public int layer;
        public int group;
        public float x1;
        public float y1;
        public float x2;
        public float y2;
        public bool forbidFallDown;
    }
    [Serializable] public sealed class WzLadder
    {
        public string id;
        public float x;
        public float y1;
        public float y2;
        public bool ladder;
        public bool upperFoothold;
        public int page;
    }
    [Serializable] public sealed class WzPortal
    {
        public string id;
        public string name;
        public int type;
        public float x;
        public float y;
        public string targetMap;
        public string targetName;
        public string script;
    }
    [Serializable] public sealed class WzPlacement
    {
        public string id;
        public string kind;
        public string entityId;
        public string sourcePath;
        public string action;
        public float x;
        public float y;
        public float originalY;
        public float cy;
        public bool flip;
        public bool hide;
        public int foothold;
        public float rx0;
        public float rx1;
        public int spawnTime;
        public int containerOrder;
        public int order;
    }
}
