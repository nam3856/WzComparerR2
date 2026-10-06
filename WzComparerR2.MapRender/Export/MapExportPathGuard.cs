using System;
using System.IO;
using System.Linq;

namespace WzComparerR2.MapRender.Export
{
    internal static class MapExportPathGuard
    {
        public const int MaxFrameRate = 120;
        public const int MaxTextureDimension = 16384;

        public static string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0
                || path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part == "." || part == ".."))
                throw new InvalidDataException("Unsafe export asset path: " + path);
            return path;
        }

        public static string ResolvePath(string normalizedRoot, string relativePath)
        {
            string root = Path.GetFullPath(normalizedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalized = NormalizeRelativePath(relativePath);
            string path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Export asset leaves the package: " + relativePath);
            return path;
        }
    }
}
