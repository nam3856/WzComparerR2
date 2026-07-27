using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace WzComparerR2.Avatar.Export
{
    internal static class RtdAvatarSetCommitter
    {
        private const string ManifestName = "rtd-avatar.json";
        private const string ExpectedSchema = "wzcomparer-rtd-avatar";
        private static readonly Regex LegacyPng = new Regex(
            @"^avatar_.+\(\d+\)_(?:default\(0\)|blink\([12]\))\.png$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool HasUnmanagedLegacySet(string outputDirectory)
        {
            if (File.Exists(Path.Combine(outputDirectory, ManifestName)))
            {
                return false;
            }

            return Directory.Exists(outputDirectory) && Directory.EnumerateFiles(outputDirectory, "*.png", SearchOption.TopDirectoryOnly)
                .Any(path => LegacyPng.IsMatch(Path.GetFileName(path)));
        }

        public static void Commit(string stageDirectory, string outputDirectory, bool claimLegacyFiles)
        {
            string outputFull = Path.GetFullPath(outputDirectory);
            string stageFull = Path.GetFullPath(stageDirectory);
            if (!Directory.Exists(stageFull))
            {
                throw new DirectoryNotFoundException(stageFull);
            }

            // Validate the old ownership contract before touching the destination.
            // Unknown or corrupt manifests must not create output/backup directories.
            var oldManaged = ReadOldManagedFiles(outputFull);
            oldManaged.Add(ManifestName);
            if (claimLegacyFiles && !File.Exists(Path.Combine(outputFull, ManifestName)) && Directory.Exists(outputFull))
            {
                foreach (string path in Directory.EnumerateFiles(outputFull, "*.png", SearchOption.TopDirectoryOnly))
                {
                    if (LegacyPng.IsMatch(Path.GetFileName(path)))
                    {
                        oldManaged.Add(Path.GetFileName(path));
                    }
                }
            }

            // Resolve all staged paths before moving any destination file.
            string[] stagedFiles = Directory.EnumerateFiles(stageFull, "*", SearchOption.AllDirectories)
                .Select(path => NormalizeOwnedPath(GetRelativePath(stageFull, path)))
                .OrderBy(path => string.Equals(path, ManifestName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Directory.CreateDirectory(outputFull);
            string parent = Directory.GetParent(outputFull)?.FullName ?? outputFull;
            string backup = Path.Combine(parent, "." + new DirectoryInfo(outputFull).Name + ".rtd-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);

            var movedOld = new List<string>();
            var movedNew = new List<string>();
            try
            {
                foreach (string relative in oldManaged.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    string safe = NormalizeOwnedPath(relative);
                    string source = Path.Combine(outputFull, safe.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    string destination = Path.Combine(backup, safe.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Move(source, destination);
                    movedOld.Add(safe);
                }

                foreach (string relative in stagedFiles)
                {
                    string source = Path.Combine(stageFull, relative.Replace('/', Path.DirectorySeparatorChar));
                    string destination = Path.Combine(outputFull, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    if (File.Exists(destination))
                    {
                        throw new IOException("A staged RTD file conflicts with an unrelated destination file: " + relative);
                    }
                    File.Move(source, destination);
                    movedNew.Add(relative);
                }

                DeleteEmptyDirectories(stageFull);
                Directory.Delete(stageFull, true);
            }
            catch (Exception commitError)
            {
                var rollbackErrors = new List<Exception>();
                for (int i = movedNew.Count - 1; i >= 0; i--)
                {
                    string destination = Path.Combine(outputFull, movedNew[i].Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (File.Exists(destination))
                        {
                            File.Delete(destination);
                        }
                    }
                    catch (Exception ex)
                    {
                        rollbackErrors.Add(new IOException(
                            "Failed to remove a newly committed RTD file during rollback: " + movedNew[i], ex));
                    }
                }

                for (int i = movedOld.Count - 1; i >= 0; i--)
                {
                    string source = Path.Combine(backup, movedOld[i].Replace('/', Path.DirectorySeparatorChar));
                    string destination = Path.Combine(outputFull, movedOld[i].Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (File.Exists(source))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination));
                            File.Move(source, destination);
                        }
                    }
                    catch (Exception ex)
                    {
                        rollbackErrors.Add(new IOException(
                            "Failed to restore a previous RTD file during rollback: " + movedOld[i], ex));
                    }
                }

                TryDeleteEmptyBackup(backup);
                if (rollbackErrors.Count > 0)
                {
                    var errors = new List<Exception> { commitError };
                    errors.AddRange(rollbackErrors);
                    throw new AggregateException(
                        "The RTD set switch failed and rollback was incomplete. Recoverable files remain in: " + backup,
                        errors);
                }
                throw;
            }

            // The switch is committed before cleanup. Cleanup failures are non-fatal,
            // and a remaining directory is a recoverable copy of the previous set.
            TryDeleteBackup(backup);
        }

        private static HashSet<string> ReadOldManagedFiles(string outputDirectory)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string manifestPath = Path.Combine(outputDirectory, ManifestName);
            if (!File.Exists(manifestPath))
            {
                return result;
            }

            string json = File.ReadAllText(manifestPath, Encoding.UTF8);
            foreach (string path in new StrictManifestReader(json).ReadManagedFiles())
            {
                string safe = NormalizeOwnedPath(path);
                if (string.Equals(safe, ManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("managedFiles must not contain the RTD manifest itself.");
                }
                if (!result.Add(safe))
                {
                    throw new InvalidDataException("The existing RTD manifest contains a duplicate managed path: " + path);
                }
            }
            return result;
        }

        private static void TryDeleteEmptyBackup(string backup)
        {
            try
            {
                if (Directory.Exists(backup))
                {
                    DeleteEmptyDirectories(backup);
                    if (!Directory.EnumerateFileSystemEntries(backup).Any())
                    {
                        Directory.Delete(backup, false);
                    }
                }
            }
            catch
            {
                // Preserve the original commit failure. This directory has no data.
            }
        }

        private static void TryDeleteBackup(string backup)
        {
            try
            {
                if (Directory.Exists(backup))
                {
                    Directory.Delete(backup, true);
                }
            }
            catch
            {
                // The destination is already authoritative. Leave any remaining
                // backup files available for manual recovery.
            }
        }

        private static string NormalizeOwnedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
                path.IndexOf(':') >= 0 || path.IndexOf('\\') >= 0)
            {
                throw new InvalidDataException("Unsafe RTD managed path: " + path);
            }
            string normalized = path;
            if (normalized.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part == "." || part == ".."))
            {
                throw new InvalidDataException("Unsafe RTD managed path: " + path);
            }
            return normalized;
        }

        private static string GetRelativePath(string root, string path)
        {
            var rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(root)));
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('\\', '/');
        }

        private static string AppendDirectorySeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static void DeleteEmptyDirectories(string root)
        {
            foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length))
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
        }

        private sealed class StrictManifestReader
        {
            private const int MaximumDepth = 256;
            private readonly string text;
            private int index;

            public StrictManifestReader(string text)
            {
                this.text = text ?? throw new ArgumentNullException(nameof(text));
            }

            public IReadOnlyList<string> ReadManagedFiles()
            {
                SkipWhitespace();
                Expect('{');
                SkipWhitespace();

                string schema = null;
                string version = null;
                List<string> managedFiles = null;
                var rootProperties = new HashSet<string>(StringComparer.Ordinal);
                if (!Consume('}'))
                {
                    while (true)
                    {
                        SkipWhitespace();
                        string property = ReadString();
                        if (!rootProperties.Add(property))
                        {
                            throw Error("Duplicate root property: " + property);
                        }
                        SkipWhitespace();
                        Expect(':');
                        SkipWhitespace();

                        if (property == "schema")
                        {
                            schema = ReadString();
                        }
                        else if (property == "version")
                        {
                            version = ReadNumber();
                        }
                        else if (property == "managedFiles")
                        {
                            managedFiles = ReadStringArray();
                        }
                        else
                        {
                            SkipValue(1);
                        }

                        SkipWhitespace();
                        if (Consume('}')) break;
                        Expect(',');
                    }
                }

                SkipWhitespace();
                if (index != text.Length)
                {
                    throw Error("Unexpected content after the root object.");
                }
                if (!string.Equals(schema, ExpectedSchema, StringComparison.Ordinal) || version != "1")
                {
                    throw new InvalidDataException("The existing RTD manifest schema or version is not supported.");
                }
                if (managedFiles == null)
                {
                    throw new InvalidDataException("The existing RTD manifest has no managedFiles string array.");
                }
                return managedFiles;
            }

            private List<string> ReadStringArray()
            {
                Expect('[');
                SkipWhitespace();
                var values = new List<string>();
                if (Consume(']')) return values;
                while (true)
                {
                    SkipWhitespace();
                    if (index >= text.Length || text[index] != '"')
                    {
                        throw Error("managedFiles must contain only strings.");
                    }
                    values.Add(ReadString());
                    SkipWhitespace();
                    if (Consume(']')) return values;
                    Expect(',');
                }
            }

            private void SkipValue(int depth)
            {
                if (depth > MaximumDepth) throw Error("JSON nesting is too deep.");
                SkipWhitespace();
                if (index >= text.Length) throw Error("Unexpected end of JSON.");
                char ch = text[index];
                if (ch == '"')
                {
                    ReadString();
                    return;
                }
                if (ch == '{')
                {
                    index++;
                    SkipWhitespace();
                    if (Consume('}')) return;
                    while (true)
                    {
                        SkipWhitespace();
                        ReadString();
                        SkipWhitespace();
                        Expect(':');
                        SkipValue(depth + 1);
                        SkipWhitespace();
                        if (Consume('}')) return;
                        Expect(',');
                    }
                }
                if (ch == '[')
                {
                    index++;
                    SkipWhitespace();
                    if (Consume(']')) return;
                    while (true)
                    {
                        SkipValue(depth + 1);
                        SkipWhitespace();
                        if (Consume(']')) return;
                        Expect(',');
                    }
                }
                if (ch == 't')
                {
                    ReadLiteral("true");
                    return;
                }
                if (ch == 'f')
                {
                    ReadLiteral("false");
                    return;
                }
                if (ch == 'n')
                {
                    ReadLiteral("null");
                    return;
                }
                if (ch == '-' || (ch >= '0' && ch <= '9'))
                {
                    ReadNumber();
                    return;
                }
                throw Error("Invalid JSON value.");
            }

            private string ReadString()
            {
                Expect('"');
                var value = new StringBuilder();
                while (index < text.Length)
                {
                    char ch = text[index++];
                    if (ch == '"') return value.ToString();
                    if (ch < 0x20) throw Error("Unescaped control character in JSON string.");
                    if (ch != '\\')
                    {
                        value.Append(ch);
                        continue;
                    }

                    if (index >= text.Length) throw Error("Incomplete JSON escape.");
                    char escaped = text[index++];
                    switch (escaped)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            int codePoint = 0;
                            for (int digit = 0; digit < 4; digit++)
                            {
                                if (index >= text.Length) throw Error("Incomplete JSON unicode escape.");
                                codePoint = checked(codePoint * 16 + HexValue(text[index++]));
                            }
                            value.Append((char)codePoint);
                            break;
                        default:
                            throw Error("Unsupported JSON escape.");
                    }
                }
                throw Error("Unterminated JSON string.");
            }

            private string ReadNumber()
            {
                int start = index;
                if (Consume('-') && index >= text.Length) throw Error("Incomplete JSON number.");
                if (Consume('0'))
                {
                    if (index < text.Length && char.IsDigit(text[index])) throw Error("JSON numbers cannot have a leading zero.");
                }
                else
                {
                    if (index >= text.Length || text[index] < '1' || text[index] > '9') throw Error("Invalid JSON number.");
                    while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
                }
                if (Consume('.'))
                {
                    int fractionStart = index;
                    while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
                    if (fractionStart == index) throw Error("JSON fraction requires a digit.");
                }
                if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
                {
                    index++;
                    if (index < text.Length && (text[index] == '+' || text[index] == '-')) index++;
                    int exponentStart = index;
                    while (index < text.Length && text[index] >= '0' && text[index] <= '9') index++;
                    if (exponentStart == index) throw Error("JSON exponent requires a digit.");
                }
                return text.Substring(start, index - start);
            }

            private void ReadLiteral(string literal)
            {
                if (index + literal.Length > text.Length ||
                    string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                {
                    throw Error("Invalid JSON literal.");
                }
                index += literal.Length;
            }

            private void SkipWhitespace()
            {
                while (index < text.Length)
                {
                    char ch = text[index];
                    if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n') break;
                    index++;
                }
            }

            private bool Consume(char expected)
            {
                if (index < text.Length && text[index] == expected)
                {
                    index++;
                    return true;
                }
                return false;
            }

            private void Expect(char expected)
            {
                if (!Consume(expected)) throw Error("Expected '" + expected + "'.");
            }

            private int HexValue(char ch)
            {
                if (ch >= '0' && ch <= '9') return ch - '0';
                if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
                if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
                throw Error("Invalid JSON unicode escape.");
            }

            private InvalidDataException Error(string message)
            {
                return new InvalidDataException(string.Format(
                    CultureInfo.InvariantCulture,
                    "Invalid existing RTD manifest JSON at character {0}: {1}",
                    index,
                    message));
            }
        }
    }
}
