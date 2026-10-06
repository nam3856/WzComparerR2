using System;
using System.IO;
using UnityEditor;

namespace WzComparerR2.Unity.Editor
{
    /// <summary>A byte-for-byte snapshot includes metadata, so rollback preserves existing GUIDs.</summary>
    internal sealed class WzImportTransaction : IDisposable
    {
        private readonly string output;
        private readonly string backup;
        private readonly string transactionsRoot;
        private readonly bool existed;
        private bool committed;

        public WzImportTransaction(string assetDirectory)
        {
            output = Path.GetFullPath(assetDirectory);
            string importRoot = Path.GetFullPath(WzUnityImporter.OutputRoot) + Path.DirectorySeparatorChar;
            if (!output.StartsWith(importRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid import transaction destination.");
            transactionsRoot = Path.GetFullPath("Library/WzImportTransactions") + Path.DirectorySeparatorChar;
            backup = Path.Combine(transactionsRoot, Guid.NewGuid().ToString("N"));
            existed = Directory.Exists(output);
            Directory.CreateDirectory(backup);
            if (existed) CopyTree(output, backup + "/files");
            if (File.Exists(output + ".meta")) File.Copy(output + ".meta", backup + "/root.meta");
        }

        public void Commit() => committed = true;

        public void Dispose()
        {
            try
            {
                if (!committed)
                {
                    // The constructor verified this is exactly one bundle below Assets/MapleImported.
                    if (Directory.Exists(output)) Directory.Delete(output, true);
                    if (File.Exists(output + ".meta")) File.Delete(output + ".meta");
                    if (existed) CopyTree(backup + "/files", output);
                    if (File.Exists(backup + "/root.meta")) File.Copy(backup + "/root.meta", output + ".meta");
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
            }
            finally
            {
                if (Path.GetFullPath(backup).StartsWith(transactionsRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(backup)) Directory.Delete(backup, true);
            }
        }

        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(source))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Import output contains a symbolic link: " + directory);
                CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
            }
        }
    }
}
