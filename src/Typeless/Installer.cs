using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Typeless
{
    public static class Installer
    {
        private const string KeyRoot = @"Software\Classes\SystemFileAssociations\.";
        private const string VerbName = "Typeless";

        private static string KeyPath(string ext) => KeyRoot + ext + @"\shell\" + VerbName;

        // Menu extensions: every Formats.Table source.
        public static IEnumerable<string> MenuExtensions => Formats.AllSourceExtensions;

        public static void Install()
        {
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot resolve the executable path.");

            int count = 0;
            foreach (string ext in MenuExtensions)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath(ext));
                key.SetValue("MUIVerb", "Convert with Typeless");
                key.SetValue("Icon", $"\"{exe}\",0");
                using RegistryKey command = key.CreateSubKey("command");
                command.SetValue("", $"\"{exe}\" \"%1\"");
                count++;
            }

            NotifyShell();
            Log.Write($"Installed context menu for {count} extensions ({exe}).");
            Console.WriteLine($"Registered Typeless for {count} extensions.");
        }

        // Removes the classic verbs.
        public static void Uninstall()
        {
            int count = RemoveVerbs();
            NotifyShell();
            Log.Write($"Removed context menu for {count} extensions.");
            Console.WriteLine($"Removed Typeless from {count} extensions.");
        }

        // Classic verbs only. build.ps1 runs this once the sparse package registers.
        public static void UninstallVerbs()
        {
            int count = RemoveVerbs();
            NotifyShell();
            Log.Write($"Removed context menu for {count} extensions (sparse package registered).");
            Console.WriteLine($"Removed Typeless from {count} extensions.");
        }

        private static int RemoveVerbs()
        {
            int count = 0;
            foreach (string ext in MenuExtensions)
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(KeyPath(ext), throwOnMissingSubKey: false);
                    count++;
                }
                catch (Exception ex)
                {
                    Log.Write($"Uninstall: could not remove .{ext}: {ex.Message}");
                }
            }
            return count;
        }

        private static void NotifyShell()
        {
            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
