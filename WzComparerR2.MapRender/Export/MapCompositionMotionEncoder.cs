using System;
using System.Collections.Generic;
using System.Linq;
using WzComparerR2.MapRender.Patches2;

namespace WzComparerR2.MapRender.Export
{
    /// <summary>
    /// Converts MapRender's periodic movement primitives into the small, fixed
    /// motion vocabulary understood by the composition consumer. No expression
    /// source is accepted or emitted here.
    /// </summary>
    public static class MapCompositionMotionEncoder
    {
        public static MapCompositionMotion EncodeBack(
            BackItem back,
            double baseX,
            double baseY,
            int tileWidth,
            int tileHeight,
            IList<MapCompositionMotionWarning> warnings)
        {
            if (back == null) throw new ArgumentNullException(nameof(back));
            var tracks = new MapCompositionMotionTracks();
            int phaseMs = Math.Max(0, back.View?.Time ?? 0);

            if ((back.TileMode & TileMode.ScrollHorizontal) != 0)
            {
                tracks.X = EncodeScroll(baseX, back.Rx * 5d, tileWidth, phaseMs, "horizontal", warnings);
            }
            if ((back.TileMode & TileMode.ScrollVertical) != 0)
            {
                tracks.Y = EncodeScroll(baseY, back.Ry * 5d, tileHeight, phaseMs, "vertical", warnings);
            }
            return tracks.Any ? new MapCompositionMotion { Tracks = tracks } : null;
        }

        public static MapCompositionMotion EncodeObject(
            ObjItem obj,
            double baseX,
            double baseY,
            IList<MapCompositionMotionWarning> warnings)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            if (obj.MoveNodes == null || obj.MoveNodes.Count == 0 || obj.TotalMovePeriod <= 0)
            {
                return null;
            }
            if (obj.MoveNodes.Any(node => node == null || node.MoveP <= 0 || node.MoveDelay < 0))
            {
                warnings?.Add(new MapCompositionMotionWarning(
                    "invalid-move-period",
                    "The movement contains a non-positive moveP or negative moveDelay and was frozen at the export snapshot."));
                return null;
            }

            switch (obj.MoveType)
            {
                case 1:
                case 2:
                case 3:
                    if (obj.MoveNodes.Count != 1)
                    {
                        warnings?.Add(new MapCompositionMotionWarning(
                            "complex-move-nodes",
                            "Multi-node harmonic movement cannot be represented by one exact v2 harmonic track and was frozen at the export snapshot."));
                        return null;
                    }
                    return EncodeHarmonic(obj, baseX, baseY);

                case 6:
                    return EncodeOneWay(obj, baseX, baseY);

                case 7:
                case 8:
                    if (obj.MoveNodes.Count != 1)
                    {
                        warnings?.Add(new MapCompositionMotionWarning(
                            "complex-move-nodes",
                            "Multi-node back-and-forth movement cannot be represented by one exact v2 cycle and was frozen at the export snapshot."));
                        return null;
                    }
                    return EncodeBackAndForth(obj, baseX, baseY);

                default:
                    warnings?.Add(new MapCompositionMotionWarning(
                        "unsupported-move-type",
                        "Unknown moveType " + obj.MoveType + " was frozen at the export snapshot."));
                    return null;
            }
        }

        /// <summary>
        /// Pure evaluator used to compare encoded tracks with MapRender movement
        /// samples without involving a rendering engine.
        /// </summary>
        public static double Evaluate(MapCompositionMotionTrack track, double elapsedMs)
        {
            if (track == null) throw new ArgumentNullException(nameof(track));
            if (!(track.CycleMs > 0d) || double.IsNaN(track.CycleMs) || double.IsInfinity(track.CycleMs))
            {
                throw new ArgumentOutOfRangeException(nameof(track), "cycleMs must be finite and positive.");
            }

            double time = elapsedMs + track.PhaseMs;
            if (track.Loop)
            {
                time = PositiveModulo(time, track.CycleMs);
            }
            else
            {
                time = Math.Max(0d, Math.Min(track.CycleMs, time));
            }

            double value;
            switch (track.Kind)
            {
                case "cosine":
                    value = track.Offset + track.Amplitude * Math.Cos(time * Math.PI * 2d / track.CycleMs);
                    break;
                case "sine":
                    value = track.Offset + track.Amplitude * Math.Sin(time * Math.PI * 2d / track.CycleMs);
                    break;
                case "keyframes":
                    value = EvaluateKeys(track.Keys, time);
                    break;
                default:
                    throw new ArgumentException("Unsupported motion track kind: " + track.Kind, nameof(track));
            }
            return track.PixelSnap ? Math.Floor(value) : value;
        }

        private static MapCompositionMotionTrack EncodeScroll(
            double offset,
            double pixelsPerSecond,
            int tileSize,
            int sourcePhaseMs,
            string axis,
            IList<MapCompositionMotionWarning> warnings)
        {
            if (pixelsPerSecond == 0d) return null;
            if (tileSize <= 0)
            {
                warnings?.Add(new MapCompositionMotionWarning(
                    "invalid-scroll-cycle",
                    "The " + axis + " scrolling Back has no positive tile size and was frozen at the export snapshot."));
                return null;
            }

            double cycleMs = Math.Abs(tileSize * 1000d / pixelsPerSecond);
            double end = offset + Math.Sign(pixelsPerSecond) * tileSize;
            return Keyframes(cycleMs, sourcePhaseMs, true,
                new MapCompositionMotionKey { TimeMs = 0d, Value = offset, Interpolation = "linear" },
                new MapCompositionMotionKey { TimeMs = cycleMs, Value = end, Interpolation = "linear" });
        }

        private static MapCompositionMotion EncodeHarmonic(ObjItem obj, double baseX, double baseY)
        {
            MoveNode node = obj.MoveNodes[0];
            double cycleMs = node.MoveP;
            double phaseMs = PositiveModulo(Math.Max(0, obj.View?.Time ?? 0), cycleMs);
            var tracks = new MapCompositionMotionTracks();
            if (node.MoveW != 0)
            {
                tracks.X = Harmonic("cosine", cycleMs, phaseMs, baseX, node.MoveW);
            }
            if (node.MoveH != 0)
            {
                tracks.Y = Harmonic(obj.MoveType == 3 ? "sine" : "cosine", cycleMs, phaseMs, baseY, node.MoveH);
            }
            return tracks.Any ? new MapCompositionMotion { Tracks = tracks } : null;
        }

        private static MapCompositionMotion EncodeOneWay(ObjItem obj, double baseX, double baseY)
        {
            double cycleMs = obj.TotalMovePeriod;
            double phaseMs = PositiveModulo(Math.Max(0, obj.View?.Time ?? 0), cycleMs);
            var xKeys = new List<MapCompositionMotionKey>();
            var yKeys = new List<MapCompositionMotionKey>();
            foreach (MoveNode node in obj.MoveNodes)
            {
                double start = node.StartTime;
                double moveStart = start + node.MoveDelay;
                double end = moveStart + node.MoveP;
                double x0 = baseX + node.StartPos.X;
                double y0 = baseY + node.StartPos.Y;
                AddKey(xKeys, start, x0, node.MoveDelay > 0 ? "hold" : "linear");
                AddKey(yKeys, start, y0, node.MoveDelay > 0 ? "hold" : "linear");
                AddKey(xKeys, moveStart, x0, "linear");
                AddKey(yKeys, moveStart, y0, "linear");
                AddKey(xKeys, end, x0 + node.MoveW, "hold");
                AddKey(yKeys, end, y0 + node.MoveH, "hold");
            }

            var tracks = new MapCompositionMotionTracks();
            if (HasChangingValue(xKeys)) tracks.X = Keyframes(cycleMs, phaseMs, false, xKeys);
            if (HasChangingValue(yKeys)) tracks.Y = Keyframes(cycleMs, phaseMs, false, yKeys);
            tracks.Opacity = EncodeOneWayOpacity(obj, cycleMs, phaseMs);
            return tracks.Any ? new MapCompositionMotion { Tracks = tracks } : null;
        }

        private static MapCompositionMotionTrack EncodeOneWayOpacity(ObjItem obj, double cycleMs, double phaseMs)
        {
            MoveNode first = obj.MoveNodes[0];
            MoveNode last = obj.MoveNodes[obj.MoveNodes.Count - 1];
            double fadeInMs = Math.Min(first.MoveP * 0.1d, 300d);
            double fadeOutMs = Math.Min(last.MoveP * 0.1d, 300d);
            var keys = new List<MapCompositionMotionKey>();

            AddKey(keys, 0d, 0d, "hold");
            AddKey(keys, first.MoveDelay, 0d, "linear");
            AddKey(keys, first.MoveDelay + fadeInMs, 100d, "hold");
            double fadeOutStart = last.StartTime + last.MoveDelay + last.MoveP - fadeOutMs;
            AddKey(keys, fadeOutStart, 100d, "linear");
            AddKey(keys, cycleMs, 0d, "hold");
            return Keyframes(cycleMs, phaseMs, false, keys);
        }

        private static MapCompositionMotion EncodeBackAndForth(ObjItem obj, double baseX, double baseY)
        {
            MoveNode node = obj.MoveNodes[0];
            double halfCycleMs = node.MoveDelay + node.MoveP;
            double cycleMs = halfCycleMs * 2d;
            double phaseMs = PositiveModulo(Math.Max(0, obj.View?.Time ?? 0), cycleMs);
            var xKeys = TriangleKeys(baseX, baseX + node.MoveW, node.MoveDelay, node.MoveP, cycleMs);
            var yKeys = TriangleKeys(baseY, baseY + node.MoveH, node.MoveDelay, node.MoveP, cycleMs);
            var tracks = new MapCompositionMotionTracks();
            if (HasChangingValue(xKeys)) tracks.X = Keyframes(cycleMs, phaseMs, false, xKeys);
            if (HasChangingValue(yKeys)) tracks.Y = Keyframes(cycleMs, phaseMs, false, yKeys);
            if (obj.MoveType == 8)
            {
                double normalScale = obj.Flip ? -100d : 100d;
                tracks.ScaleX = Keyframes(cycleMs, phaseMs, false,
                    new MapCompositionMotionKey { TimeMs = 0d, Value = normalScale, Interpolation = "hold" },
                    new MapCompositionMotionKey { TimeMs = halfCycleMs, Value = -normalScale, Interpolation = "hold" },
                    new MapCompositionMotionKey { TimeMs = cycleMs, Value = normalScale, Interpolation = "hold" });
            }
            return tracks.Any ? new MapCompositionMotion { Tracks = tracks } : null;
        }

        private static List<MapCompositionMotionKey> TriangleKeys(
            double startValue,
            double endValue,
            double delayMs,
            double moveMs,
            double cycleMs)
        {
            double halfCycleMs = delayMs + moveMs;
            var keys = new List<MapCompositionMotionKey>();
            AddKey(keys, 0d, startValue, delayMs > 0d ? "hold" : "linear");
            AddKey(keys, delayMs, startValue, "linear");
            AddKey(keys, halfCycleMs, endValue, delayMs > 0d ? "hold" : "linear");
            AddKey(keys, halfCycleMs + delayMs, endValue, "linear");
            AddKey(keys, cycleMs, startValue, "hold");
            return keys;
        }

        private static MapCompositionMotionTrack Harmonic(
            string kind,
            double cycleMs,
            double phaseMs,
            double offset,
            double amplitude)
        {
            return new MapCompositionMotionTrack
            {
                Kind = kind,
                Loop = true,
                CycleMs = cycleMs,
                PhaseMs = phaseMs,
                Offset = offset,
                Amplitude = amplitude,
                PixelSnap = false
            };
        }

        private static MapCompositionMotionTrack Keyframes(
            double cycleMs,
            double phaseMs,
            bool pixelSnap,
            params MapCompositionMotionKey[] keys)
        {
            return Keyframes(cycleMs, phaseMs, pixelSnap, (IEnumerable<MapCompositionMotionKey>)keys);
        }

        private static MapCompositionMotionTrack Keyframes(
            double cycleMs,
            double phaseMs,
            bool pixelSnap,
            IEnumerable<MapCompositionMotionKey> keys)
        {
            return new MapCompositionMotionTrack
            {
                Kind = "keyframes",
                Loop = true,
                CycleMs = cycleMs,
                PhaseMs = PositiveModulo(phaseMs, cycleMs),
                PixelSnap = pixelSnap,
                Offset = 0d,
                Amplitude = 0d,
                Keys = keys.ToList()
            };
        }

        private static double EvaluateKeys(IList<MapCompositionMotionKey> keys, double timeMs)
        {
            if (keys == null || keys.Count == 0) throw new ArgumentException("A keyframe track requires keys.", nameof(keys));
            if (timeMs <= keys[0].TimeMs) return keys[0].Value;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                MapCompositionMotionKey left = keys[i];
                MapCompositionMotionKey right = keys[i + 1];
                if (timeMs >= right.TimeMs) continue;
                if (left.Interpolation == "hold") return left.Value;
                double amount = (timeMs - left.TimeMs) / (right.TimeMs - left.TimeMs);
                return left.Value + (right.Value - left.Value) * amount;
            }
            return keys[keys.Count - 1].Value;
        }

        private static void AddKey(List<MapCompositionMotionKey> keys, double timeMs, double value, string interpolation)
        {
            var key = new MapCompositionMotionKey
            {
                TimeMs = timeMs,
                Value = value,
                Interpolation = interpolation
            };
            if (keys.Count > 0 && Math.Abs(keys[keys.Count - 1].TimeMs - timeMs) < 0.000001d)
            {
                keys[keys.Count - 1] = key;
            }
            else
            {
                keys.Add(key);
            }
        }

        private static bool HasChangingValue(IList<MapCompositionMotionKey> keys)
        {
            if (keys == null || keys.Count < 2) return false;
            double first = keys[0].Value;
            return keys.Any(key => Math.Abs(key.Value - first) > 0.000001d);
        }

        private static double PositiveModulo(double value, double divisor)
        {
            if (!(divisor > 0d)) return 0d;
            double result = value % divisor;
            return result < 0d ? result + divisor : result;
        }
    }

    public sealed class MapCompositionMotionWarning
    {
        public MapCompositionMotionWarning(string kind, string reason)
        {
            Kind = kind;
            Reason = reason;
        }

        public string Kind { get; }
        public string Reason { get; }
    }
}
