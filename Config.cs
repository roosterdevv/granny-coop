using System;
using MelonLoader;

namespace GrannyCoop
{
    /// <summary>
    /// Values live in MelonLoader preferences (UserData/MelonPreferences.cfg,
    /// private to each game folder) and can be overridden per launch with
    /// command-line flags, which is how the two-instance tests run:
    ///   Granny.exe --coop-host --coop-name=Host --coop-autoplay
    ///   Granny.exe --coop-join=127.0.0.1 --coop-name=Guest --coop-autoplay
    /// </summary>
    internal sealed class CoopConfig
    {
        public CoopRole Role = CoopRole.Off;
        public string Address = "127.0.0.1";
        public int Port = 47777;
        public string PlayerName = "Player";
        public bool AutoPlay;
        public bool TestMove;
        public bool Probe;
        public bool DoorTest;
        public bool ItemTest;
        public bool ItemProbe;  // QA: name the InventoryController method that reverts a flag
        public bool Spectate;   // QA: force the spectator view without dying
        public bool CatchTest;  // QA: teleport onto Granny so she catches us
        public bool PauseTest;  // QA: simulate the settings menu's Time.timeScale = 0

        public static CoopConfig Load()
        {
            var cfg = new CoopConfig();

            try
            {
                var cat = MelonPreferences.CreateCategory("GrannyCoop");
                var mode = cat.CreateEntry("Mode", "Off",
                    "Mode", "Off, Host or Join");
                var addr = cat.CreateEntry("Address", "127.0.0.1",
                    "Address", "Host address to join (direct IP or virtual LAN)");
                var port = cat.CreateEntry("Port", 47777,
                    "Port", "UDP port the host listens on");
                var name = cat.CreateEntry("PlayerName", "Player",
                    "Player name", "Shown in logs and on other players' screens");
                var auto = cat.CreateEntry("AutoPlay", false,
                    "Auto play", "DA-0: drive the menu automatically (test harness only)");

                cfg.Role = ParseRole(mode.Value);
                cfg.Address = addr.Value;
                cfg.Port = port.Value;
                cfg.PlayerName = name.Value;
                cfg.AutoPlay = auto.Value;
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP preferences FAILED: " + e.GetType().Name + ": " + e.Message);
            }

            // Command-line overrides win: they let two copies of the game on one
            // PC (or a scripted test) choose a role without editing preferences.
            foreach (var raw in Environment.GetCommandLineArgs())
            {
                var a = raw.TrimStart('-');
                if (a == "coop-host") cfg.Role = CoopRole.Host;
                else if (a == "coop-join") cfg.Role = CoopRole.Join;
                else if (a == "coop-off") cfg.Role = CoopRole.Off;
                else if (a == "coop-autoplay") cfg.AutoPlay = true;
                else if (a == "coop-testmove") cfg.TestMove = true;
                else if (a == "coop-probe") cfg.Probe = true;
                else if (a == "coop-doortest") cfg.DoorTest = true;
                else if (a == "coop-itemtest") cfg.ItemTest = true;
                else if (a == "coop-itemprobe") cfg.ItemProbe = true;
                else if (a == "coop-spectate") cfg.Spectate = true;
                else if (a == "coop-catchtest") cfg.CatchTest = true;
                else if (a == "coop-pausetest") cfg.PauseTest = true;
                else if (a.StartsWith("coop-join=", StringComparison.OrdinalIgnoreCase))
                {
                    cfg.Role = CoopRole.Join;
                    cfg.Address = a.Substring("coop-join=".Length);
                }
                else if (a.StartsWith("coop-name=", StringComparison.OrdinalIgnoreCase))
                    cfg.PlayerName = a.Substring("coop-name=".Length);
                else if (a.StartsWith("coop-port=", StringComparison.OrdinalIgnoreCase) &&
                         int.TryParse(a.Substring("coop-port=".Length), out var p))
                    cfg.Port = p;
            }

            return cfg;
        }

        private static CoopRole ParseRole(string s)
        {
            if (string.Equals(s, "Host", StringComparison.OrdinalIgnoreCase)) return CoopRole.Host;
            if (string.Equals(s, "Join", StringComparison.OrdinalIgnoreCase)) return CoopRole.Join;
            return CoopRole.Off;
        }
    }
}
