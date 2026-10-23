using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GrannyCoop.Setup
{
    /// <summary>
    /// Merges the [GrannyCoop] section into MelonPreferences.cfg without touching any
    /// other section: MelonLoader writes the file for every mod on the machine, and the
    /// game folder may already hold preferences from an older install.
    /// </summary>
    internal static class ConfigWriter
    {
        public const string Section = "GrannyCoop";

        public static string Write(string installDir, string mode, string address, int port, string playerName)
        {
            string userData = Path.Combine(installDir, "UserData");
            Directory.CreateDirectory(userData);

            string path = Path.Combine(userData, "MelonPreferences.cfg");
            string backup = path + ".bak";
            if (File.Exists(path) && !File.Exists(backup))
            {
                File.Copy(path, backup);
            }

            string existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            File.WriteAllText(path, Merge(existing, mode, address, port, playerName), new UTF8Encoding(false));
            return path;
        }

        public static string Merge(string existing, string mode, string address, int port, string playerName)
        {
            string normalized = (existing ?? string.Empty).Replace("\r\n", "\n");
            bool trailingNewline = normalized.EndsWith("\n", StringComparison.Ordinal);
            var lines = new List<string>(normalized.Split('\n'));
            if (trailingNewline && lines.Count > 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 1 && lines[0].Length == 0) lines.Clear();

            var block = new List<string>
            {
                "[" + Section + "]",
                "# Off, Host or Join",
                "Mode = \"" + Quote(mode) + "\"",
                "# Host address to join (direct IP or virtual LAN)",
                "Address = \"" + Quote(address) + "\"",
                "# UDP port the host listens on",
                "Port = " + port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "# Shown in logs and on other players' screens",
                "PlayerName = \"" + Quote(playerName) + "\"",
                "# DA-0: drive the menu automatically (test harness only)",
                "AutoPlay = false"
            };

            int start = -1, end = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (start < 0)
                {
                    if (t.Equals("[" + Section + "]", StringComparison.OrdinalIgnoreCase)) start = i;
                }
                else if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
                {
                    end = i;
                    break;
                }
            }

            if (start >= 0)
            {
                if (end < 0) end = lines.Count;
                lines.RemoveRange(start, end - start);
                lines.InsertRange(start, block);
            }
            else
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length != 0) lines.Add(string.Empty);
                lines.AddRange(block);
            }

            return string.Join("\r\n", lines) + "\r\n";
        }

        public static string CleanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Player";
            var sb = new StringBuilder();
            foreach (char c in name.Trim())
            {
                if (c == '"' || c == '\\' || char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= 24) break;
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "Player" : s;
        }

        public static string CleanAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return "127.0.0.1";
            var sb = new StringBuilder();
            foreach (char c in address.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == ':' || c == '-' || c == '_') sb.Append(c);
            }
            string s = sb.ToString();
            return s.Length == 0 ? "127.0.0.1" : s;
        }

        private static string Quote(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
