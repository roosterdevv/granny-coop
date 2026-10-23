using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;

namespace GrannyCoop.Setup
{
    /// <summary>
    /// The whole install, as a plain function so the form and the command line do
    /// exactly the same thing. Every step logs; nothing is silent.
    /// </summary>
    internal static class Installer
    {
        public const string Version = "1.0.0";
        public const string LoaderVersion = "0.7.3";

        public static int Run(Options o, Action<string> log, Action<int> progress)
        {
            string dir;
            try
            {
                dir = Path.GetFullPath(o.InstallDir.Trim().Trim('"'));
            }
            catch (Exception e)
            {
                log("Некорректный путь: " + e.Message);
                return 2;
            }

            log("Granny Co-op Setup " + Version);
            log("папка игры: " + dir);

            if (!Directory.Exists(dir))
            {
                log("Такой папки нет.");
                return 2;
            }
            if (!File.Exists(Path.Combine(dir, "Granny.exe")))
            {
                log("В этой папке нет Granny.exe — выберите папку игры.");
                return 2;
            }

            var build = BuildCheck.Identify(dir);
            if (build == null)
            {
                log("Не нашёл Granny_Data\\il2cpp_data\\Metadata\\global-metadata.dat — это не папка Granny.");
                return 2;
            }

            log("сборка: metadata v" + build.Version + ", sanity 0x" + build.Magic.ToString("X8"));
            if (build.Magic != 0xFAB11BAF || build.Version != 31)
            {
                if (!o.Force)
                {
                    log("Эта версия игры не та, на которой мод проверен (нужна metadata v31, Steam buildid 21006637).");
                    log("Если вы уверены — запустите установщик с --force.");
                    return 3;
                }
                log("ВНИМАНИЕ: неизвестная версия игры, продолжаю из-за --force.");
            }

            var running = Process.GetProcessesByName("Granny");
            if (running.Length > 0)
            {
                log("Granny сейчас запущена (" + running.Length + " процесс). Закройте игру и повторите.");
                return 4;
            }

            Step(progress, 10);

            bool runtimeOk;
            try
            {
                runtimeOk = o.SkipRuntime ? Runtime.IsPresent() : Runtime.Ensure(log);
            }
            catch (Exception e)
            {
                log("Ошибка при установке .NET 6: " + e.GetType().Name + ": " + e.Message);
                runtimeOk = Runtime.IsPresent();
            }
            if (!runtimeOk)
            {
                log(o.SkipRuntime
                    ? "ВНИМАНИЕ: .NET 6 runtime не найден, а проверку пропустили (--skip-runtime). Мод не загрузится."
                    : "ВНИМАНИЕ: .NET 6 runtime не установлен — мод не загрузится, пока он не появится.");
            }

            Step(progress, 40);
            MelonLoaderInstall(dir, log);

            Step(progress, 70);
            ModInstall(dir, log);

            if (o.Role != null)
            {
                string mode = o.Role == "host" ? "Host" : o.Role == "join" ? "Join" : "Off";
                string name = ConfigWriter.CleanName(o.PlayerName);
                string addr = ConfigWriter.CleanAddress(o.Address);
                string cfg = ConfigWriter.Write(dir, mode, addr, o.Port, name);
                log("настройки: Mode=" + mode + ", PlayerName=" + name +
                    (mode == "Join" ? ", Address=" + addr + ":" + o.Port : "") + "  ->  " + cfg);
            }

            Step(progress, 90);
            Verify(dir, log);

            Step(progress, 100);
            log(runtimeOk ? "Готово." : "Файлы установлены, но .NET 6 runtime отсутствует — см. выше.");
            return runtimeOk ? 0 : 6;
        }

        private static void Step(Action<int> progress, int percent)
        {
            if (progress != null) progress(percent);
        }

        // ---- MelonLoader ------------------------------------------------------

        private static void MelonLoaderInstall(string dir, Action<string> log)
        {
            string shim = Path.Combine(dir, "version.dll");
            string loaderDir = Path.Combine(dir, "MelonLoader");

            string present = File.Exists(shim) ? FileVersion(shim) : null;
            if (present != null && present.StartsWith(LoaderVersion, StringComparison.Ordinal) &&
                File.Exists(Path.Combine(loaderDir, @"net6\MelonLoader.dll")))
            {
                log("MelonLoader " + LoaderVersion + " уже стоит — пропускаю.");
                return;
            }

            if (File.Exists(shim) || Directory.Exists(loaderDir))
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                if (File.Exists(shim))
                {
                    File.Move(shim, shim + ".bak-" + stamp);
                    log("старый version.dll (" + (present ?? "unknown") + ") отложен в version.dll.bak-" + stamp);
                }
                if (Directory.Exists(loaderDir))
                {
                    Directory.Move(loaderDir, loaderDir + ".bak-" + stamp);
                    log("старая папка MelonLoader отложена в MelonLoader.bak-" + stamp);
                }
            }

            int files = 0;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.MelonLoader.zip"))
            {
                if (stream == null) throw new InvalidOperationException("payload.MelonLoader.zip отсутствует в установщике");
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;   // directory entry
                        string dest = SafeJoin(dir, entry.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        entry.ExtractToFile(dest, true);
                        files++;
                    }
                }
            }

            log("MelonLoader " + LoaderVersion + " распакован: " + files + " файлов (version.dll + MelonLoader/).");
        }

        // ---- the mod ----------------------------------------------------------

        private static void ModInstall(string dir, Action<string> log)
        {
            string mods = Path.Combine(dir, "Mods");
            Directory.CreateDirectory(mods);

            string target = Path.Combine(mods, "GrannyCoop.dll");
            byte[] bytes = Payload.Read("payload.GrannyCoop.dll");

            if (File.Exists(target) && SameBytes(target, bytes))
            {
                log("GrannyCoop.dll уже актуален — пропускаю.");
                return;
            }
            if (File.Exists(target))
            {
                File.Copy(target, target + ".bak", true);
                log("прежний Mods\\GrannyCoop.dll сохранён как GrannyCoop.dll.bak");
            }

            File.WriteAllBytes(target, bytes);
            var name = AssemblyName.GetAssemblyName(target);
            log("мод " + name.Version + " установлен: Mods\\GrannyCoop.dll (" + bytes.Length + " байт)");
        }

        private static bool SameBytes(string path, byte[] bytes)
        {
            try
            {
                var current = File.ReadAllBytes(path);
                if (current.Length != bytes.Length) return false;
                for (int i = 0; i < bytes.Length; i++)
                    if (current[i] != bytes[i]) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---- checks -----------------------------------------------------------

        private static void Verify(string dir, Action<string> log)
        {
            string shim = Path.Combine(dir, "version.dll");
            string loader = Path.Combine(dir, @"MelonLoader\net6\MelonLoader.dll");
            string mod = Path.Combine(dir, @"Mods\GrannyCoop.dll");
            string cfg = Path.Combine(dir, @"UserData\MelonPreferences.cfg");

            log(shim + "  " + (File.Exists(shim) ? "ok, " + (FileVersion(shim) ?? "?") : "ОТСУТСТВУЕТ"));
            log(loader + "  " + (File.Exists(loader) ? "ok" : "ОТСУТСТВУЕТ"));
            log(mod + "  " + (File.Exists(mod) ? "ok" : "ОТСУТСТВУЕТ"));
            if (File.Exists(cfg)) log(cfg + "  ok");
        }

        internal static string FileVersion(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return info.FileVersion ?? info.ProductVersion;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeJoin(string root, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
            string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("архив пытается писать за пределы папки игры: " + relative);
            return full;
        }
    }

    internal static class Payload
    {
        public static byte[] Read(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidOperationException("в установщике нет " + name);
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
    }

    internal static class BuildCheck
    {
        internal sealed class Build
        {
            public uint Magic;
            public int Version;
        }

        public static Build Identify(string dir)
        {
            string meta = Path.Combine(dir, @"Granny_Data\il2cpp_data\Metadata\global-metadata.dat");
            if (!File.Exists(meta)) return null;
            try
            {
                using (var fs = File.OpenRead(meta))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 8) return null;
                    return new Build { Magic = br.ReadUInt32(), Version = br.ReadInt32() };
                }
            }
            catch
            {
                return null;
            }
        }
    }

    internal static class Runtime
    {
        private const string InstallerName = "dotnet-runtime-6.0.36-win-x64.exe";

        public static bool IsPresent()
        {
            foreach (var root in Roots())
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var d in Directory.GetDirectories(root))
                    {
                        Version v;
                        if (Version.TryParse(Path.GetFileName(d), out v) && v.Major == 6) return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        private static IEnumerable<string> Roots()
        {
            const string tail = @"shared\Microsoft.NETCore.App";

            // a machine-wide install: <Program Files>\dotnet\shared\...
            foreach (var baseDir in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432")
            })
            {
                if (!string.IsNullOrEmpty(baseDir))
                    yield return Path.Combine(baseDir, "dotnet", tail);
            }

            // DOTNET_ROOT points at the dotnet folder itself
            string root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, tail);

            // a per-user install
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
                yield return Path.Combine(local, @"Microsoft\dotnet", tail);
        }

        /// <summary>
        /// The bundled runtime installer runs elevated and quiet. Returns false only with
        /// a logged reason; the extracted installer is kept so the user can run it by hand.
        /// </summary>
        public static bool Ensure(Action<string> log)
        {
            if (IsPresent())
            {
                log(".NET 6 runtime: найден.");
                return true;
            }

            log(".NET 6 runtime не найден. MelonLoader без него не загрузится — устанавливаю " + InstallerName + ".");
            string temp = Path.Combine(Path.GetTempPath(), "GrannyCoop-Setup");
            Directory.CreateDirectory(temp);
            string exe = Path.Combine(temp, InstallerName);
            File.WriteAllBytes(exe, Payload.Read("payload.dotnet6.exe"));
            log("распакован: " + exe);

            Process p;
            try
            {
                var psi = new ProcessStartInfo(exe, "/install /quiet /norestart")
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };
                p = Process.Start(psi);
                if (p == null)
                {
                    log("Не удалось запустить установщик .NET.");
                    return false;
                }
            }
            catch (Win32Exception e)
            {
                log("Установка .NET отменена (запрос администратора): " + e.Message);
                log("Можно поставить вручную, запустив " + exe);
                return false;
            }

            if (!p.WaitForExit(10 * 60 * 1000))
            {
                log("Установщик .NET не завершился за 10 минут. Запустите " + exe + " вручную.");
                return false;
            }

            int code = p.ExitCode;
            log("установщик .NET завершился с кодом " + code);
            if (code != 0 && code != 3010)
            {
                log("Установка .NET не удалась (код " + code + "). Запустите " + exe + " вручную.");
                return false;
            }

            for (int i = 0; i < 10; i++)
            {
                if (IsPresent())
                {
                    log(".NET 6 runtime установлен.");
                    return true;
                }
                Thread.Sleep(500);
            }

            log(".NET установщик отработал, но папка 6.x не появилась — проверьте вручную.");
            return false;
        }
    }
}
