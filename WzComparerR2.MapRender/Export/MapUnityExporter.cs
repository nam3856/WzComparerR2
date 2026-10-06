using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using WzComparerR2.Animation;
using WzComparerR2.Common;
using WzComparerR2.Controls;
using WzComparerR2.MapRender.Patches2;
using WzComparerR2.PluginBase;
using WzComparerR2.UnityExport;
using WzComparerR2.WzLib;
using WzUnity;

namespace WzComparerR2.MapRender.Export
{
    /// <summary>Exports editable Unity scene data without an After Effects process or timeline limit.</summary>
    public sealed class MapUnityExporter
    {
        public Microsoft.Xna.Framework.Graphics.GraphicsDevice GraphicsDevice { get; set; }
        private readonly Dictionary<string, WzEntity> entities = new Dictionary<string, WzEntity>(StringComparer.Ordinal);
        private readonly Dictionary<ContainerNode, int> containerOrders = new Dictionary<ContainerNode, int>();
        private UnityExportWriter writer;
        private MapCompositionExportOptions options;
        private CancellationToken cancellation;
        private string scratch;

        public WzUnityManifest Export(MapData mapData, string outputDirectory,
            MapCompositionExportOptions exportOptions = null, CancellationToken cancellationToken = default(CancellationToken),
            IProgress<MapCompositionExportProgress> progress = null)
        {
            if (mapData == null) throw new ArgumentNullException(nameof(mapData));
            options = exportOptions ?? new MapCompositionExportOptions();
            cancellation = cancellationToken;
            if (options.FrameRate < 1 || options.FrameRate > 120) throw new ArgumentOutOfRangeException(nameof(options.FrameRate));
            entities.Clear();
            containerOrders.Clear();
            string mapId = (mapData.ID ?? 0).ToString("D9", CultureInfo.InvariantCulture);
            string source = mapData.SourceNode?.FullPathToFile ?? mapData.Name;
            scratch = Path.Combine(Path.GetTempPath(), "WzUnityMap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                using (writer = new UnityExportWriter(outputDirectory, mapId, "map", source, cancellation))
                {
                    Rectangle bounds = mapData.VRect;
                    writer.Manifest.map = new WzMap { id = mapId, left = bounds.X, top = bounds.Y, width = bounds.Width, height = bounds.Height };
                    var containers = GetContainers(mapData.Scene).ToList();
                    for (int i = 0; i < containers.Count; i++) containerOrders.Add(containers[i], i);
                    for (int i = 0; i < containers.Count; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        progress?.Report(new MapCompositionExportProgress("맵 레이어", i, containers.Count));
                        foreach (SceneItem item in containers[i].Slots) ExportVisual(item, i);
                    }
                    ExportGeometry(mapData);
                    ExportOriginalPlacements(mapData);
                    if (mapData.Light != null) writer.Warn(source + "/light", "Map lighting and custom shader output are not reconstructed.");
                    if (!string.IsNullOrEmpty(mapData.Bgm)) writer.Warn(mapData.Bgm, "BGM is not included in this scene package.");
                    if (mapData.MapEvents.Count > 0) writer.Warn(source + "/effect", "Map events and gameplay scripts are recorded only as unsupported; no gameplay logic is imported.");
                    cancellation.ThrowIfCancellationRequested();
                    progress?.Report(new MapCompositionExportProgress("검증 및 저장", 0, 1));
                    writer.Commit();
                    progress?.Report(new MapCompositionExportProgress("완료", 1, 1));
                    return writer.Manifest;
                }
            }
            finally
            {
                // This exact directory is created by this export and never contains user data.
                if (Directory.Exists(scratch))
                {
                    try { Directory.Delete(scratch, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static IEnumerable<ContainerNode> GetContainers(SceneNode node)
        {
            if (node is ContainerNode container) { yield return container; yield break; }
            foreach (SceneNode child in node.Nodes)
                foreach (ContainerNode result in GetContainers(child)) yield return result;
        }

        private void ExportVisual(SceneItem item, int containerOrder)
        {
            cancellation.ThrowIfCancellationRequested();
            PatchVisibility visible = options.Visibility;
            if (item.Tags != null && visible != null && item.Tags.Any(tag => !visible.IsTagVisible(tag))) return;
            var layer = new WzMapLayer { id = "layer-" + containerOrder + "-" + item.Index, containerOrder = containerOrder, order = item.Index, action = "default" };
            object animator;
            bool useFrameZ = false;
            if (item is BackItem back)
            {
                if (back.ScreenMode != 0 && back.ScreenMode != options.DisplayMode + 1 &&
                    !((back.ScreenMode & 2) != 0 && options.ViewportWidth == 1024 && options.ViewportHeight == 768)) return;
                if (visible != null && (!(back.IsFront ? visible.FrontVisible : visible.BackVisible) || back.Quest.Any(q => !visible.IsQuestVisible(q.ID, q.State)))) return;
                layer.group = back.IsFront ? "Front" : "Back";
                layer.sourcePath = "Map/Back/" + back.BS + ".img/" + (back.Ani == 0 ? "back" : back.Ani == 1 ? "ani" : "spine" + back.SpineNo) + "/" + back.No;
                layer.x = back.X; layer.y = back.Y; layer.flip = back.Flip; layer.alpha = back.Alpha / 255f;
                animator = back.View?.Animator;
                Point size = back.View?.Bounds.Size ?? Point.Zero;
                bool hasFlow = back.View?.FlowX.HasValue == true || back.View?.FlowY.HasValue == true;
                int flowX = back.View?.FlowX ?? 0;
                int flowY = back.View?.FlowY ?? 0;
                int flowRate = flowX != 0 ? flowX : flowY;
                layer.background = new WzBackground
                {
                    type = GetBackType(back.TileMode), rx = hasFlow ? flowRate : back.Rx, ry = hasFlow ? flowRate : back.Ry,
                    cx = back.Cx == 0 ? size.X : back.Cx, cy = back.Cy == 0 ? size.Y : back.Cy,
                    repeatX = (back.TileMode & TileMode.Horizontal) != 0, repeatY = (back.TileMode & TileMode.Vertical) != 0,
                    scrollX = hasFlow ? flowX != 0 : (back.TileMode & TileMode.ScrollHorizontal) != 0,
                    scrollY = hasFlow ? flowY != 0 : (back.TileMode & TileMode.ScrollVertical) != 0,
                    parallax = !hasFlow,
                    scrollDistanceX = back.W && back.Wx != 0 ? Math.Abs(back.Wx) : 100,
                    scrollDistanceY = back.W && back.Wy != 0 ? Math.Abs(back.Wy) : 100
                };
                if ((animator is FrameAnimator || animator is ISpineAnimator) &&
                    ((layer.background.repeatX && layer.background.cx <= 0) || (layer.background.repeatY && layer.background.cy <= 0)))
                    throw new InvalidDataException("Repeating background has no positive tile size: " + layer.sourcePath);
            }
            else if (item is ObjItem obj)
            {
                if (obj.Hide) return;
                if (visible != null && (!visible.ObjVisible || obj.Quest.Any(q => !visible.IsQuestVisible(q.ID, q.State)) || obj.Questex.Any(q => !visible.IsQuestVisible(q.ID, q.Key, q.State)))) return;
                layer.group = "Obj";
                layer.sourcePath = "Map/Obj/" + obj.OS + ".img/" + obj.L0 + "/" + obj.L1 + "/" + obj.L2;
                if (obj.Light) { writer.Warn(layer.sourcePath, "Light-map objects are not reconstructed."); return; }
                layer.x = obj.X; layer.y = obj.Y; layer.flip = obj.Flip; layer.z = obj.Z;
                animator = obj.View?.Animator;
                var warnings = new List<MapCompositionMotionWarning>();
                layer.motion = ConvertMotion(MapCompositionMotionEncoder.EncodeObject(obj, 0, 0, warnings));
                foreach (var warning in warnings) writer.Warn(layer.sourcePath, warning.Reason);
                if (obj.Events.Count > 0) writer.Warn(layer.sourcePath + "/event", "Object input/collision events are not executed in Unity.");
            }
            else if (item is TileItem tile)
            {
                if (visible != null && !visible.TileVisible) return;
                layer.group = "Tile";
                layer.sourcePath = "Map/Tile/" + tile.TS + ".img/" + tile.U + "/" + tile.No;
                layer.x = tile.X; layer.y = tile.Y; animator = tile.View?.Animator;
                useFrameZ = true;
            }
            else if (item is PortalItem portal)
            {
                if (visible != null && !visible.PortalVisible) return;
                if (portal.Type < 0 || portal.Type >= PortalItem.PortalTypes.Count)
                {
                    writer.Warn(portal.Name, "Unknown portal type; metadata is retained.");
                    return;
                }
                layer.group = "Portal";
                layer.x = portal.X; layer.y = portal.Y;
                string portalType = PortalItem.PortalTypes[portal.Type == 7 ? 2 : portal.Type];
                string basePath = "Map/MapHelper.img/portal/game/" + portalType;
                string image = portal.Image == 0 ? "default" : portal.Image.ToString();
                Wz_Node source = PluginManager.FindWz(basePath + "/" + image) ?? PluginManager.FindWz(basePath);
                if (source == null) return; // Invisible spawn/script portals intentionally have no game visual.
                layer.sourcePath = source.FullPathToFile;
                animator = portal.View?.Animator;
                if (animator is FrameAnimator && source.Value == null && source.Nodes["0"] == null)
                {
                    // Some portal families (e.g. ps) provide variant 1 but no default image.
                    // The source renderer's fallback creates an empty animator for this family node.
                    writer.Warn(layer.sourcePath, "This portal selects a default image that has no animation frames; portal metadata is retained.");
                    return;
                }
                if (animator is StateMachineAnimator)
                {
                    var portalEntity = UnityEntityExporter.AddEntity(source, "portal", writer, PluginManager.FindWz,
                        "portal-visual-" + portalType + "-" + image, GraphicsDevice);
                    if (portalEntity == null) return;
                    layer.entityId = portalEntity.id;
                    layer.action = portalEntity.clips.Any(clip => clip.name == "portalContinue") ? "portalContinue" : portalEntity.defaultAction;
                    writer.Manifest.map.layers.Add(layer);
                    return;
                }
                useFrameZ = true;
            }
            else
            {
                if (!(item is LifeItem) && !(item is ReactorItem) && !(item is PortalItem) && !(item is LadderRopeItem))
                    writer.Warn(item.Name ?? item.GetType().Name, "Unsupported scene visual: " + item.GetType().Name);
                return;
            }
            WzEntity entity = AddVisualEntity(animator, layer.sourcePath, useFrameZ);
            if (entity != null) layer.entityId = entity.id;
            // Preserve missing/unsupported layer metadata for an explicit importer placeholder.
            writer.Manifest.map.layers.Add(layer);
        }

        private WzEntity AddVisualEntity(object animator, string source, bool useFrameZ)
        {
            if (animator == null) throw new InvalidDataException("Visible map resource did not load: " + source);
            string key = source + "|" + useFrameZ;
            if (animator is ISpineAnimator spineKey) key += "|" + spineKey.SelectedAnimationName + "|" + spineKey.SelectedSkin;
            if (entities.TryGetValue(key, out var existing)) return existing;
            var entity = new WzEntity { id = "map-visual-" + entities.Count, kind = "map-visual", sourcePath = source, displayName = source, defaultAction = "default" };
            var clip = new WzAnimationClip { name = "default" };
            var track = new WzAnimationTrack { id = "sprite", kind = "sprite" };
            clip.tracks.Add(track);
            entity.clips.Add(clip);
            if (animator is FrameAnimator frames)
            {
                clip.loop = track.loop = !(frames is RepeatableFrameAnimator repeat) || repeat.IsLoop;
                foreach (Frame frame in frames.Data.Frames)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (frame?.Texture == null) throw new InvalidDataException("Missing frame texture: " + source);
                    var png = MapTexturePngEncoder.Encode(frame);
                    using (var stream = new MemoryStream(png.Bytes))
                    using (var bitmap = new System.Drawing.Bitmap(stream))
                    {
                        track.frames.Add(new WzSpriteFrame
                        {
                            assetId = writer.AddBitmap(bitmap), delayMs = Math.Max(1, frame.Delay), originX = frame.Origin.X, originY = frame.Origin.Y,
                            z = useFrameZ ? frame.Z : 0, a0 = frame.A0, a1 = frame.A1, blend = frame.Blend ? "interpolate" : "normal", sourcePath = source
                        });
                    }
                }
            }
            else if (animator is ISpineAnimator spine)
            {
                var baker = options.SpineBaker;
                if (baker == null && MapRenderSpineSequenceBaker.TryCreate(spine, out var defaultBaker)) baker = defaultBaker;
                if (baker == null) throw new InvalidOperationException("No active graphics device can bake Spine: " + source);
                int cycleMs = Math.Max(0, (spine as AnimationItem)?.Length ?? 0);
                var baked = baker.Bake(new MapSpineBakeRequest
                {
                    PackageRootDirectory = scratch, RelativeOutputDirectory = entity.id, AssetId = entity.id,
                    FrameRate = options.FrameRate, CycleDurationMs = cycleMs,
                    DurationFrames = Math.Max(1, (int)Math.Ceiling(cycleMs * options.FrameRate / 1000d)),
                    Animator = spine, AnimationName = spine.SelectedAnimationName, SkinName = spine.SelectedSkin
                }, cancellation);
                if (baked.BlendMode != "normal")
                    writer.Warn(source, "Spine blend mode '" + baked.BlendMode + "' is retained as metadata; the Unity importer currently renders it with normal sprite blending.");
                clip.loop = track.loop = baked.Loop;
                foreach (var frame in baked.Frames)
                {
                    cancellation.ThrowIfCancellationRequested();
                    track.frames.Add(new WzSpriteFrame
                    {
                        assetId = writer.AddPngFile(Path.Combine(scratch, frame.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                        delayMs = Math.Max(1, frame.DelayMs), originX = frame.OriginX, originY = frame.OriginY,
                        blend = baked.BlendMode, sourcePath = source
                    });
                }
                foreach (string warning in baked.Warnings) writer.Warn(source, warning);
            }
            else
            {
                writer.Warn(source, "Custom shader/sprite visual is not supported: " + animator.GetType().Name);
                return null;
            }
            if (track.frames.Count == 0) throw new InvalidDataException("Map animation has no frames: " + source);
            clip.durationMs = track.frames.Sum(frame => frame.delayMs);
            entities.Add(key, entity);
            writer.Manifest.entities.Add(entity);
            return entity;
        }

        private static WzMotion ConvertMotion(MapCompositionMotion motion)
        {
            if (motion == null) return null;
            return new WzMotion { x = ConvertChannel(motion.Tracks.X, 1), y = ConvertChannel(motion.Tracks.Y, 1), opacity = ConvertChannel(motion.Tracks.Opacity, .01f), scaleX = ConvertChannel(motion.Tracks.ScaleX, .01f) };
        }

        private static WzMotionChannel ConvertChannel(MapCompositionMotionTrack channel, float factor)
        {
            if (channel == null) return null;
            return new WzMotionChannel
            {
                // Sprite clips and movement both start at canonical source time zero, independent of preview uptime.
                kind = channel.Kind, loop = channel.Loop, cycleMs = channel.CycleMs, phaseMs = 0,
                offset = (float)channel.Offset * factor, amplitude = (float)channel.Amplitude * factor, pixelSnap = channel.PixelSnap,
                keys = channel.Keys.Select(key => new WzMotionKey { timeMs = key.TimeMs, value = (float)key.Value * factor, interpolation = key.Interpolation }).ToList()
            };
        }

        private static int GetBackType(TileMode mode)
        {
            bool both = (mode & TileMode.BothTile) == TileMode.BothTile;
            if ((mode & TileMode.ScrollHorizontal) != 0) return both ? 6 : 4;
            if ((mode & TileMode.ScrollVertical) != 0) return both ? 7 : 5;
            if (both) return 3;
            return (mode & TileMode.Horizontal) != 0 ? 1 : (mode & TileMode.Vertical) != 0 ? 2 : 0;
        }

        private void ExportGeometry(MapData data)
        {
            var map = writer.Manifest.map;
            foreach (var fh in data.FootholdManager.AllFootholdByID.Values.OrderBy(fh => fh.ID))
            {
                cancellation.ThrowIfCancellationRequested();
                // Read the original node for properties the preview physics does not retain.
                Wz_Node source = data.SourceNode?.Nodes["foothold"]?.Nodes[fh.LayerLevel.ToString()]?.Nodes
                    .SelectMany(group => group.Nodes).FirstOrDefault(node => node.Text == fh.ID.ToString());
                map.footholds.Add(new WzFoothold { id = fh.ID, prev = fh.Prev, next = fh.Next, layer = fh.LayerLevel, group = fh.GroupIndex,
                    x1 = fh.X1, y1 = fh.Y1, x2 = fh.X2, y2 = fh.Y2, forbidFallDown = (source?.Nodes["forbidFallDown"].GetValueEx(0) ?? 0) != 0 });
            }
            foreach (var ladder in data.Scene.Fly.LadderRope.Slots.OfType<LadderRopeItem>())
                map.ladders.Add(new WzLadder { id = ladder.Index.ToString(), x = ladder.X, y1 = ladder.Y1, y2 = ladder.Y2, ladder = ladder.L != 0, upperFoothold = ladder.Uf != 0, page = ladder.Page });
            foreach (var portal in data.Scene.Portals)
                map.portals.Add(new WzPortal { id = portal.Index.ToString(), name = portal.PName, type = portal.Type, x = portal.X, y = portal.Y,
                    targetMap = portal.ToMap?.ToString("D9"), targetName = portal.ToName, script = portal.Script });
        }

        private void ExportOriginalPlacements(MapData data)
        {
            Wz_Node lifeRoot = data.SourceNode?.Nodes["life"];
            if (lifeRoot != null)
            {
                bool hasCategories = lifeRoot.Nodes["isCategory"].GetValueEx(0) != 0;
                IEnumerable<Wz_Node> nodes = !hasCategories
                    ? lifeRoot.Nodes : lifeRoot.Nodes.Where(node => node.Text != "isCategory").SelectMany(node => node.Nodes);
                int index = 0;
                foreach (Wz_Node node in nodes)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string type = node.Nodes["type"].GetValueEx<string>(null);
                    if (type != "m" && type != "n") continue;
                    string kind = type == "m" ? "mob" : "npc";
                    int id = node.Nodes["id"].GetValueEx(0);
                    int fhId = node.Nodes["fh"].GetValueEx(0);
                    string resourcePath = (kind == "mob" ? "Mob/" : "Npc/") + id.ToString("D7") + ".img";
                    WzEntity entity = AddPlacementEntity(resourcePath, kind);
                    ContainerNode container = data.Scene.FootholdContainerById.TryGetValue(fhId, out var footholdContainer) ? footholdContainer : data.Scene.Fly.Sky;
                    int order = !hasCategories && int.TryParse(node.Text, out int originalIndex) ? originalIndex : index;
                    float x = node.Nodes["x"].GetValueEx(0);
                    float cy = node.Nodes["cy"].GetValueEx(node.Nodes["y"].GetValueEx(0));
                    writer.Manifest.map.placements.Add(new WzPlacement
                    {
                        id = "life-" + index++, kind = kind, entityId = entity?.id, sourcePath = node.FullPathToFile,
                        action = entity?.defaultAction, x = x, y = cy, originalY = node.Nodes["y"].GetValueEx(0), cy = cy,
                        flip = node.Nodes["f"].GetValueEx(0) != 0, hide = node.Nodes["hide"].GetValueEx(0) != 0 || IsResourceHidden(resourcePath),
                        foothold = fhId, rx0 = node.Nodes["rx0"].GetValueEx(0), rx1 = node.Nodes["rx1"].GetValueEx(0),
                        spawnTime = node.Nodes["mobTime"].GetValueEx(0), containerOrder = containerOrders[container], order = order
                    });
                }
            }
            foreach (LayerNode layer in data.Scene.Layers.Nodes.OfType<LayerNode>())
            foreach (ReactorItem reactor in layer.Reactor.Slots.OfType<ReactorItem>())
            {
                cancellation.ThrowIfCancellationRequested();
                string path = "Reactor/" + reactor.ID.ToString("D7") + ".img";
                WzEntity entity = AddPlacementEntity(path, "reactor");
                writer.Manifest.map.placements.Add(new WzPlacement { id = "reactor-" + reactor.Index, kind = "reactor", entityId = entity?.id, sourcePath = path,
                    action = entity?.defaultAction, x = reactor.X, y = reactor.Y, originalY = reactor.Y, cy = reactor.Y, flip = reactor.Flip,
                    spawnTime = reactor.ReactorTime, containerOrder = containerOrders[layer.Reactor], order = reactor.Index });
            }
        }

        private WzEntity AddPlacementEntity(string path, string kind)
        {
            if (entities.TryGetValue(path, out var entity)) return entity;
            Wz_Node node = PluginManager.FindWz(path);
            if (node == null) { writer.Warn(path, "Placement resource is not loaded; placement metadata is retained."); return null; }
            if (node.Value is Wz_Image img)
            {
                if (!img.TryExtract()) throw new InvalidDataException("Could not extract placement resource: " + path);
                node = img.Node;
            }
            entity = UnityEntityExporter.AddEntity(node, kind, writer, PluginManager.FindWz, graphicsDevice: GraphicsDevice);
            entities[path] = entity;
            return entity;
        }

        private static bool IsResourceHidden(string path)
        {
            Wz_Node node = PluginManager.FindWz(path);
            if (node?.Value is Wz_Image image && image.TryExtract()) node = image.Node;
            return (node?.FindNodeByPath("info\\hide").GetValueEx(0) ?? 0) != 0;
        }
    }
}
