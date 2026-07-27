using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace WzComparerR2.Avatar.Export
{
    internal sealed class RtdExportLog
    {
        private readonly object syncRoot = new object();

        private RtdExportLog(string logPath)
        {
            LogPath = logPath;
        }

        public string LogPath { get; }

        public static RtdExportLog Start(string outputDirectory, IEnumerable<string> actionNames)
        {
            try
            {
                string[] names = actionNames?.ToArray() ?? new string[0];
                string fileName = "rtd-export-"
                    + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                    + ".log";
                string output = outputDirectory;
                try
                {
                    output = Path.GetFullPath(outputDirectory);
                }
                catch
                {
                    // The exporter will report an invalid output path separately.
                }

                var roots = new List<string>();
                try
                {
                    string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrWhiteSpace(localApplicationData))
                    {
                        roots.Add(Path.Combine(localApplicationData, "WzComparerR2", "Logs"));
                    }
                }
                catch
                {
                }
                try
                {
                    roots.Add(Path.Combine(Path.GetTempPath(), "WzComparerR2", "Logs"));
                }
                catch
                {
                }

                foreach (string root in roots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.CreateDirectory(root);
                        string logPath = Path.Combine(root, fileName);
                        using (new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        {
                        }

                        var log = new RtdExportLog(logPath);
                        string header = "RTD avatar export started" + Environment.NewLine
                            + "Output: " + output + Environment.NewLine
                            + "Actions (" + names.Length.ToString(CultureInfo.InvariantCulture) + "): " + string.Join(", ", names);
                        if (log.TryWrite(header))
                        {
                            return log;
                        }
                        try
                        {
                            File.Delete(logPath);
                        }
                        catch
                        {
                        }
                    }
                    catch
                    {
                        // Try the next diagnostics location.
                    }
                }
            }
            catch
            {
                // Logging must never prevent the export itself from running.
            }

            return new RtdExportLog(null);
        }

        public void Write(string message)
        {
            TryWrite(message);
        }

        private bool TryWrite(string message)
        {
            if (string.IsNullOrEmpty(LogPath))
            {
                return false;
            }

            try
            {
                string line = "[" + DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + "] " + message + Environment.NewLine;
                lock (syncRoot)
                {
                    File.AppendAllText(LogPath, line, new UTF8Encoding(false));
                }
                return true;
            }
            catch
            {
                // Preserve the original export result even if diagnostics cannot be written.
                return false;
            }
        }
    }
}
