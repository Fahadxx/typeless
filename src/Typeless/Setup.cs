using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Management.Deployment;

namespace Typeless
{
    // Install-time commands shared by the installer, its uninstaller and build.ps1.
    public static class Setup
    {
        private const string PackageName = "Typeless";
        private const int ShutdownWaitMs = 3000;

        // Classic verbs first, then the sparse package from <app>\Typeless.local.msix (signed on this PC) when it
        // exists, else <app>\Typeless.msix. The package replaces the verbs only when it registers, so an unsigned
        // or untrusted package leaves the classic menu working.
        public static int Register()
        {
            RemovePackages();
            Installer.Install();

            string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string msix = Path.Combine(appDir, LocalSign.LocalMsixName);
            if (!File.Exists(msix)) msix = Path.Combine(appDir, "Typeless.msix");
            if (!File.Exists(msix))
            {
                Log.Write("Register: no Typeless.msix, classic menu only.");
                Console.WriteLine("menu=classic");
                return 0;
            }
            Log.Write("Register: using " + Path.GetFileName(msix));

            try
            {
                var options = new AddPackageOptions { ExternalLocationUri = new Uri(appDir + Path.DirectorySeparatorChar) };
                DeploymentResult result = Task.Run(async () =>
                    await new PackageManager().AddPackageByUriAsync(new Uri(msix), options)).GetAwaiter().GetResult();
                if (result.ExtendedErrorCode != null) throw result.ExtendedErrorCode;
            }
            catch (Exception ex)
            {
                Log.Write($"Register: package not added (0x{ex.HResult:X8}): {ex.Message}");
                Console.WriteLine("menu=classic");
                return 0;
            }

            Installer.UninstallVerbs();
            Log.Write("Register: top-level menu package added.");
            Console.WriteLine("menu=top-level");
            return 0;
        }

        public static int Unregister()
        {
            RemovePackages();
            Installer.Uninstall();
            return 0;
        }

        // Any Typeless package, whatever its publisher, so an old dev-signed one goes too.
        private static void RemovePackages()
        {
            var pm = new PackageManager();
            List<string> names;
            try
            {
                names = pm.FindPackagesForUser("")
                    .Where(p => string.Equals(p.Id.Name, PackageName, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Id.FullName)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Write("Package lookup failed: " + ex.Message);
                return;
            }

            foreach (string name in names)
            {
                try
                {
                    DeploymentResult result = Task.Run(async () => await pm.RemovePackageAsync(name)).GetAwaiter().GetResult();
                    if (result.ExtendedErrorCode != null) throw result.ExtendedErrorCode;
                    Log.Write("Removed package " + name);
                    Console.WriteLine("Removed package " + name);
                }
                catch (Exception ex)
                {
                    Log.Write($"Could not remove package {name}: {ex.Message}");
                }
            }
        }

        // Closes every other Typeless running from this exe (WM_CLOSE, then kill after 3 s), so its files can be replaced.
        public static int Shutdown()
        {
            string? self = Environment.ProcessPath;
            int selfId = Environment.ProcessId;
            var others = new List<Process>();
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(self ?? "Typeless")))
            {
                try
                {
                    if (p.Id != selfId && string.Equals(p.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase))
                    {
                        others.Add(p);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"Shutdown: skipped process {p.Id}: {ex.Message}");
                }
                p.Dispose();
            }

            foreach (Process p in others)
            {
                try { p.CloseMainWindow(); } catch { }
            }
            var sw = Stopwatch.StartNew();
            foreach (Process p in others)
            {
                try
                {
                    int left = Math.Max(0, ShutdownWaitMs - (int)sw.ElapsedMilliseconds);
                    if (!p.WaitForExit(left)) p.Kill();
                }
                catch (Exception ex)
                {
                    Log.Write($"Shutdown: could not stop process {p.Id}: {ex.Message}");
                }
                p.Dispose();
            }

            Console.WriteLine($"Stopped {others.Count} running Typeless.");
            return 0;
        }

        public static int Backends()
        {
            Console.WriteLine("word=" + YesNo(Tools.HasWord));
            Console.WriteLine("powerpoint=" + YesNo(Tools.HasPowerPoint));
            Console.WriteLine("libreoffice=" + YesNo(Tools.Soffice != null));
            Console.WriteLine("ffmpeg=" + YesNo(Tools.Ffmpeg != null));
            Console.WriteLine("ffprobe=" + YesNo(Tools.Ffprobe != null));
            return 0;
        }

        private static string YesNo(bool b) => b ? "yes" : "no";
    }
}
