using System;
using System.IO;
using System.Text;

namespace WzComparerR2.Avatar.Export
{
    internal static class RtdAvatarJsonWriter
    {
        public static void Write(string path, RtdAvatarManifest manifest)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                WriteManifest(writer, manifest);
            }
        }

        private static void WriteManifest(TextWriter writer, RtdAvatarManifest value)
        {
            writer.Write("{\n  \"schema\": "); WriteString(writer, value.Schema);
            writer.Write(",\n  \"version\": "); writer.Write(value.Version);
            writer.Write(",\n  \"frameRate\": "); writer.Write(value.FrameRate);
            writer.Write(",\n  \"character\": "); WriteString(writer, value.Character);
            writer.Write(",\n  \"managedFiles\": [");
            for (int i = 0; i < value.ManagedFiles.Count; i++)
            {
                writer.Write(i == 0 ? "\n    " : ",\n    "); WriteString(writer, value.ManagedFiles[i]);
            }
            writer.Write(value.ManagedFiles.Count == 0 ? "]" : "\n  ]");

            writer.Write(",\n  \"effects\": [");
            for (int i = 0; i < value.Effects.Count; i++)
            {
                var effect = value.Effects[i];
                writer.Write(i == 0 ? "\n    {" : ",\n    {");
                WriteProperty(writer, "id", effect.Id, 0);
                WriteProperty(writer, "displayName", effect.DisplayName, 1);
                WriteProperty(writer, "kind", effect.Kind, 1);
                WriteProperty(writer, "slotIndex", effect.SlotIndex, 1);
                WriteProperty(writer, "itemId", effect.ItemId, 1);
                writer.Write("\n    }");
            }
            writer.Write(value.Effects.Count == 0 ? "]" : "\n  ]");

            writer.Write(",\n  \"assets\": [");
            for (int i = 0; i < value.Assets.Count; i++)
            {
                var asset = value.Assets[i];
                writer.Write(i == 0 ? "\n    {" : ",\n    {");
                WriteProperty(writer, "id", asset.Id, 0);
                WriteProperty(writer, "path", asset.Path, 1);
                WriteProperty(writer, "sha256", asset.Sha256, 1);
                WriteProperty(writer, "width", asset.Width, 1);
                WriteProperty(writer, "height", asset.Height, 1);
                writer.Write("\n    }");
            }
            writer.Write(value.Assets.Count == 0 ? "]" : "\n  ]");

            writer.Write(",\n  \"actions\": [");
            for (int i = 0; i < value.Actions.Count; i++)
            {
                WriteAction(writer, value.Actions[i], i == 0);
            }
            writer.Write(value.Actions.Count == 0 ? "]" : "\n  ]");
            writer.Write("\n}\n");
        }

        private static void WriteAction(TextWriter writer, RtdAction action, bool first)
        {
            writer.Write(first ? "\n    {" : ",\n    {");
            WriteProperty(writer, "id", action.Id, 0);
            WriteProperty(writer, "name", action.Name, 1);
            WriteProperty(writer, "fileToken", action.FileToken, 1);
            WriteProperty(writer, "canvasWidth", action.CanvasWidth, 1);
            WriteProperty(writer, "canvasHeight", action.CanvasHeight, 1);

            writer.Write(",\n      \"baseFrames\": [");
            for (int i = 0; i < action.BaseFrames.Count; i++)
            {
                var frame = action.BaseFrames[i];
                writer.Write(i == 0 ? "\n        {" : ",\n        {");
                WriteProperty(writer, "bodyFrame", frame.BodyFrame, 0, 10);
                WriteProperty(writer, "expression", frame.Expression, 1, 10);
                WriteProperty(writer, "expressionIndex", frame.ExpressionIndex, 1, 10);
                WriteProperty(writer, "path", frame.Path, 1, 10);
                WriteProperty(writer, "durationFrames", frame.DurationFrames, 1, 10);
                writer.Write("\n        }");
            }
            writer.Write(action.BaseFrames.Count == 0 ? "]" : "\n      ]");

            writer.Write(",\n      \"effectTracks\": [");
            for (int i = 0; i < action.EffectTracks.Count; i++)
            {
                var track = action.EffectTracks[i];
                writer.Write(i == 0 ? "\n        {" : ",\n        {");
                WriteProperty(writer, "effectId", track.EffectId, 0, 10);
                WriteProperty(writer, "branch", track.Branch, 1, 10);
                WriteProperty(writer, "resolvedAction", track.ResolvedAction, 1, 10);
                WriteProperty(writer, "cycleFrames", track.CycleFrames, 1, 10);
                writer.Write(",\n          \"frames\": [");
                for (int j = 0; j < track.Frames.Count; j++)
                {
                    var frame = track.Frames[j];
                    writer.Write(j == 0 ? "\n            {" : ",\n            {");
                    WriteProperty(writer, "sourceFrameIndex", frame.SourceFrameIndex, 0, 14);
                    WriteProperty(writer, "delayMs", frame.DelayMs, 1, 14);
                    WriteProperty(writer, "durationFrames", frame.DurationFrames, 1, 14);
                    WriteProperty(writer, "a0", frame.A0, 1, 14);
                    WriteProperty(writer, "a1", frame.A1, 1, 14);
                    writer.Write("\n            }");
                }
                writer.Write(track.Frames.Count == 0 ? "]" : "\n          ]");
                writer.Write("\n        }");
            }
            writer.Write(action.EffectTracks.Count == 0 ? "]" : "\n      ]");

            WriteProperty(writer, "masterFrames", action.MasterFrames, 1, 6);
            WriteProperty(writer, "masterMode", action.MasterMode, 1, 6);
            writer.Write(",\n      \"warnings\": [");
            for (int i = 0; i < action.Warnings.Count; i++)
            {
                writer.Write(i == 0 ? "\n        " : ",\n        "); WriteString(writer, action.Warnings[i]);
            }
            writer.Write(action.Warnings.Count == 0 ? "]" : "\n      ]");

            writer.Write(",\n      \"intervals\": [");
            for (int i = 0; i < action.Intervals.Count; i++)
            {
                var interval = action.Intervals[i];
                writer.Write(i == 0 ? "\n        {" : ",\n        {");
                WriteProperty(writer, "startFrame", interval.StartFrame, 0, 10);
                WriteProperty(writer, "durationFrames", interval.DurationFrames, 1, 10);
                writer.Write(",\n          \"planes\": [");
                for (int j = 0; j < interval.Planes.Count; j++)
                {
                    var plane = interval.Planes[j];
                    writer.Write(j == 0 ? "\n            {" : ",\n            {");
                    WriteProperty(writer, "assetId", plane.AssetId, 0, 14);
                    WriteProperty(writer, "kind", plane.Kind, 1, 14);
                    WriteNullableProperty(writer, "effectId", plane.EffectId, 1, 14);
                    WriteNullableProperty(writer, "branch", plane.Branch, 1, 14);
                    WriteProperty(writer, "sourceKey", plane.SourceKey, 1, 14);
                    WriteProperty(writer, "drawOrder", plane.DrawOrder, 1, 14);
                    WriteProperty(writer, "rawZ", plane.RawZ ?? string.Empty, 1, 14);
                    WriteProperty(writer, "resolvedZ", plane.ResolvedZ, 1, 14);
                    WriteProperty(writer, "opacityStart", plane.OpacityStart, 1, 14);
                    WriteProperty(writer, "opacityEnd", plane.OpacityEnd, 1, 14);
                    writer.Write("\n            }");
                }
                writer.Write(interval.Planes.Count == 0 ? "]" : "\n          ]");
                writer.Write("\n        }");
            }
            writer.Write(action.Intervals.Count == 0 ? "]" : "\n      ]");
            writer.Write("\n    }");
        }

        private static void WriteProperty(TextWriter writer, string name, string value, int comma, int indent = 6)
        {
            writer.Write(comma == 0 ? "\n" : ",\n");
            writer.Write(new string(' ', indent));
            WriteString(writer, name);
            writer.Write(": ");
            WriteString(writer, value ?? string.Empty);
        }

        private static void WriteNullableProperty(TextWriter writer, string name, string value, int comma, int indent)
        {
            writer.Write(comma == 0 ? "\n" : ",\n");
            writer.Write(new string(' ', indent));
            WriteString(writer, name);
            writer.Write(": ");
            if (value == null) writer.Write("null"); else WriteString(writer, value);
        }

        private static void WriteProperty(TextWriter writer, string name, int value, int comma, int indent = 6)
        {
            writer.Write(comma == 0 ? "\n" : ",\n");
            writer.Write(new string(' ', indent));
            WriteString(writer, name);
            writer.Write(": ");
            writer.Write(value);
        }

        private static void WriteString(TextWriter writer, string value)
        {
            writer.Write('"');
            if (value != null)
            {
                foreach (char ch in value)
                {
                    switch (ch)
                    {
                        case '"': writer.Write("\\\""); break;
                        case '\\': writer.Write("\\\\"); break;
                        case '\b': writer.Write("\\b"); break;
                        case '\f': writer.Write("\\f"); break;
                        case '\n': writer.Write("\\n"); break;
                        case '\r': writer.Write("\\r"); break;
                        case '\t': writer.Write("\\t"); break;
                        default:
                            if (ch < 0x20) writer.Write("\\u" + ((int)ch).ToString("x4"));
                            else writer.Write(ch);
                            break;
                    }
                }
            }
            writer.Write('"');
        }
    }
}
