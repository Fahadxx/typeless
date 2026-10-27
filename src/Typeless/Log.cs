using System;
using System.Globalization;
using System.IO;

namespace Typeless
{
    public static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 1 << 20;

        public static string AppDataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Typeless");

        public static string FilePath { get; } = Path.Combine(AppDataDir, "typeless.log");

        public static void Write(string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " " + message + Environment.NewLine;
                lock (Gate)
                {
                    Directory.CreateDirectory(AppDataDir);
                    // Keep one older log, so the log never grows past about 2 MB in all.
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                        File.Move(FilePath, FilePath + ".1", overwrite: true);
                    File.AppendAllText(FilePath, line);
                }
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }
}
