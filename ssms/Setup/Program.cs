using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SqlSmartCompleteSetup
{
    internal static class Program
    {
        private const string ProductName = "SQL Smart Complete";
        private const string ExtensionFolderName = "SqlSmartComplete";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SqlSmartComplete";
        private static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        private static readonly string ProgramDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ExtensionFolderName);
        private static readonly string[] Payload = { "SqlSmartComplete.dll", "extension.vsixmanifest" };

        private sealed class SsmsInstall
        {
            public string Ide;
            public Version Version;
            public override string ToString() => $"SSMS {Version.Major} ({Version}) at {Ide}";
        }

        private static bool quiet;

        private static int Main(string[] args)
        {
            quiet = args.Any(a => Is(a, "/quiet", "/q", "-q", "--quiet"));
            bool uninstall = args.Any(a => Is(a, "/uninstall", "/u", "-u", "--uninstall"));
            string customIde = args.Where(a => a.StartsWith("/ide:", StringComparison.OrdinalIgnoreCase)).Select(a => a.Substring(5).Trim('"')).FirstOrDefault();

            Console.Title = $"{ProductName} {Version} Setup";
            Console.WriteLine($"{ProductName} {Version} for SQL Server Management Studio");
            Console.WriteLine(new string('-', 60));

            try
            {
                var targets = customIde != null
                    ? new List<SsmsInstall> { new SsmsInstall { Ide = customIde, Version = ExeVersion(Path.Combine(customIde, "SSMS.exe")) ?? new Version(22, 0) } }
                    : FindSsms();

                if (targets.Count == 0 && !uninstall)
                {
                    Fail("SQL Server Management Studio 21 or later was not found.\nInstall SSMS 21/22, or run this setup with /ide:\"<path to Common7\\IDE>\".");
                    return 2;
                }

                foreach (var t in targets) Console.WriteLine("Found " + t);
                Console.WriteLine();

                if (!WaitForSsmsToClose()) return 3;

                foreach (var t in targets)
                {
                    if (uninstall) Remove(t); else Install(t);
                }
                ClearComponentCaches();

                if (uninstall)
                {
                    Unregister();
                    Console.WriteLine($"\n{ProductName} was removed. Start SSMS again to finish.");
                    ScheduleSelfCleanup();
                }
                else
                {
                    Register();
                    Console.WriteLine($"\n{ProductName} is installed.");
                    Console.WriteLine("Start SSMS, open a query window connected to a database, and start typing.");
                    Console.WriteLine("Settings: %APPDATA%\\SqlSmartComplete\\settings.ini (created on first use)");
                    Console.WriteLine("Log:      %LOCALAPPDATA%\\SqlSmartComplete\\log.txt");
                    Console.WriteLine("To remove it, use Windows Settings > Apps, or run this setup with /uninstall.");
                }
                Pause();
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                Fail("Access denied. Right-click the setup and choose 'Run as administrator'.");
                return 5;
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                return 1;
            }
        }

        private static bool Is(string a, params string[] options) => options.Any(o => string.Equals(a, o, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ discovery

        private static Version ExeVersion(string exe)
        {
            if (!File.Exists(exe)) return null;
            var v = FileVersionInfo.GetVersionInfo(exe);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
        }

        private static List<SsmsInstall> FindSsms()
        {
            var ides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") }
                         .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(pf)) continue;
                foreach (var dir in Directory.GetDirectories(pf, "Microsoft SQL Server Management Studio*"))
                {
                    foreach (var ide in new[] { Path.Combine(dir, "Release", "Common7", "IDE"), Path.Combine(dir, "Common7", "IDE") })
                        if (File.Exists(Path.Combine(ide, "SSMS.exe"))) ides.Add(ide);
                }
            }

            // SSMS 21+ is installed by the Visual Studio installer, which records custom install paths here.
            var instances = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\VisualStudio\Packages\_Instances");
            if (Directory.Exists(instances))
            {
                foreach (var state in Directory.GetFiles(instances, "state.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var m = Regex.Match(File.ReadAllText(state), "\"installationPath\"\\s*:\\s*\"(.*?)\"");
                        if (!m.Success) continue;
                        var path = Regex.Unescape(m.Groups[1].Value);
                        var ide = Path.Combine(path, "Common7", "IDE");
                        if (File.Exists(Path.Combine(ide, "SSMS.exe"))) ides.Add(ide);
                    }
                    catch { /* ignore unreadable state files */ }
                }
            }

            return ides
                .Select(i => new SsmsInstall { Ide = i, Version = ExeVersion(Path.Combine(i, "SSMS.exe")) })
                .Where(s => s.Version != null && s.Version.Major >= 21)
                .OrderByDescending(s => s.Version)
                .ToList();
        }

        private static bool WaitForSsmsToClose()
        {
            while (Process.GetProcessesByName("SSMS").Length > 0)
            {
                if (quiet)
                {
                    Fail("SSMS is running. Close it and run the setup again.");
                    return false;
                }
                Console.WriteLine("SSMS is running. Please close all SSMS windows, then press Enter (or type Q to quit).");
                var answer = Console.ReadLine();
                if (answer != null && answer.Trim().Equals("q", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ install / remove

        private static void Install(SsmsInstall t)
        {
            var target = Path.Combine(t.Ide, "Extensions", ExtensionFolderName);
            Directory.CreateDirectory(target);
            foreach (var name in Payload)
            {
                using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload." + name))
                {
                    if (src == null) throw new InvalidOperationException("Setup is damaged: missing " + name);
                    using (var dst = File.Create(Path.Combine(target, name))) src.CopyTo(dst);
                }
            }
            TouchConfigurationChanged(t.Ide);
            Console.WriteLine($"Installed into {target}");
        }

        private static void Remove(SsmsInstall t)
        {
            var target = Path.Combine(t.Ide, "Extensions", ExtensionFolderName);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true);
                Console.WriteLine($"Removed {target}");
            }
            TouchConfigurationChanged(t.Ide);
        }

        /// <summary>Tells SSMS to rescan its Extensions folder on next start.</summary>
        private static void TouchConfigurationChanged(string ide)
        {
            var marker = Path.Combine(ide, "Extensions", "extensions.configurationchanged");
            if (!File.Exists(marker)) File.WriteAllText(marker, "");
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
        }

        /// <summary>Deletes the MEF component caches so SSMS picks up the new editor parts.</summary>
        private static void ClearComponentCaches()
        {
            var roots = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "SSMS") };
            try
            {
                var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                if (users != null)
                    roots.AddRange(Directory.GetDirectories(users).Select(u => Path.Combine(u, "AppData", "Local", "Microsoft", "SSMS")));
            }
            catch { /* other profiles may be unreadable */ }

            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var inst in Directory.GetDirectories(root))
                    {
                        var cache = Path.Combine(inst, "ComponentModelCache");
                        if (Directory.Exists(cache)) { Directory.Delete(cache, true); Console.WriteLine($"Cleared {cache}"); }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Could not clear cache under {root}: {ex.Message}");
                }
            }
        }

        // ------------------------------------------------------------------ Apps & Features entry

        private static void Register()
        {
            Directory.CreateDirectory(ProgramDir);
            var self = Assembly.GetExecutingAssembly().Location;
            var copy = Path.Combine(ProgramDir, "SqlSmartCompleteSetup.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, copy, true);

            using (var key = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", ProductName + " for SSMS");
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", "EMSL");
                key.SetValue("InstallLocation", ProgramDir);
                key.SetValue("DisplayIcon", copy);
                key.SetValue("UninstallString", $"\"{copy}\" /uninstall");
                key.SetValue("QuietUninstallString", $"\"{copy}\" /uninstall /quiet");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", 400, RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        private static void Unregister()
        {
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        }

        private static void ScheduleSelfCleanup()
        {
            if (!Directory.Exists(ProgramDir)) return;
            var self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            if (!self.StartsWith(ProgramDir, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(ProgramDir, true);
                return;
            }
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{ProgramDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        // ------------------------------------------------------------------ console helpers

        private static void Fail(string message)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n" + message);
            Console.ForegroundColor = old;
            Pause();
        }

        private static void Pause()
        {
            if (quiet || Console.IsInputRedirected) return;
            Console.WriteLine("\nPress any key to close.");
            try { Console.ReadKey(true); } catch { /* no console */ }
        }
    }
}
