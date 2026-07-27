using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WzComparerR2.MapRender.Export
{
    public static class MapCompositionManifestSerializer
    {
        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static void Write(string path, MapCompositionManifest manifest)
        {
            if (path == null) throw new System.ArgumentNullException(nameof(path));
            if (manifest == null) throw new System.ArgumentNullException(nameof(manifest));

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(JsonSerializer.Serialize(manifest, SerializerOptions));
                writer.Write('\n');
            }
        }

        public static MapCompositionManifest Read(string path)
        {
            if (path == null) throw new System.ArgumentNullException(nameof(path));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var manifest = JsonSerializer.Deserialize<MapCompositionManifest>(stream, SerializerOptions);
                if (manifest == null)
                {
                    throw new InvalidDataException("The map composition manifest is empty.");
                }
                return manifest;
            }
        }
    }
}
