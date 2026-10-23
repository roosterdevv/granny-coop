using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GrannyCoop.Setup
{
    /// <summary>
    /// Finds "Granny" the way Steam itself would: the uninstall entry Steam makes for
    /// every installed app, then the Steam library list, then the default paths.
    /// Nothing is guessed from the caller's drive layout.
    /// </summary>
    internal static class SteamLocator
    {
        public static string FindGranny()
        {
            foreach (var dir in Candidates())
            {
                try
                {
                    if (dir != null && File.Exists(Path.Combine(dir, "Granny.exe")))
                        return dir;
                }
                catch
                {
                    // unreadable candidate: try the next one
                }
            }
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            // 1. what Steam registered for app 962400 at install time
            foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    string loc = ReadValue(root + @"\Steam App 962400", "InstallLocation", view);
                    if (!string.IsNullOrEmpty(loc)) yield return loc.Trim('"');
                }
            }

            // 2. every Steam library on the machine
            foreach (var steamRoot in SteamRoots())
            {
                yield return Path.Combine(steamRoot, @"steamapps\common\Granny");

                string vdf = Path.Combine(steamRoot, @"steamapps\libraryfolders.vdf");
                foreach (var lib in LibrariesFromVdf(vdf))
                    yield return Path.Combine(lib, @"steamapps\common\Granny");
            }

            // 3. the usual install locations, for a Steam that was never started
            foreach (var pf in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            })
            {
                if (!string.IsNullOrEmpty(pf))
                    yield return Path.Combine(pf, @"Steam\steamapps\common\Granny");
            }
        }

        private static IEnumerable<string> SteamRoots()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in new[]
            {
                new { Key = @"Software\Valve\Steam", Name = "SteamPath", View = RegistryView.Default },
                new { Key = @"SOFTWARE\WOW6432Node\Valve\Steam", Name = "InstallPath", View = RegistryView.Registry32 },
                new { Key = @"SOFTWARE\Valve\Steam", Name = "InstallPath", View = RegistryView.Registry64 }
            })
            {
                string path = ReadValue(pair.Key, pair.Name, pair.View);
                if (string.IsNullOrEmpty(path)) continue;
                path = path.Trim('"').Replace('/', '\\');
                if (seen.Add(path)) yield return path;
            }
        }

        private static IEnumerable<string> LibrariesFromVdf(string vdf)
        {
            string text;
            try
            {
                if (!File.Exists(vdf)) yield break;
                text = File.ReadAllText(vdf);
            }
            catch
            {
                yield break;
            }

            // "path"   "D:\\SteamLibrary"
            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
            {
                string lib = m.Groups[1].Value.Replace("\\\\", "\\");
                if (lib.Length > 2 && lib[1] == ':') yield return lib;
            }
        }

        private static string ReadValue(string keyPath, string name, RegistryView view)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(keyPath))
                {
                    var v = key != null ? key.GetValue(name) as string : null;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch
            {
            }

            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view))
                using (var key = baseKey.OpenSubKey(keyPath))
                {
                    var v = key != null ? key.GetValue(name) as string : null;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
