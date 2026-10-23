using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;

namespace GrannyCoop
{
    /// <summary>
    /// QA only (--coop-itemprobe). Notes §15/§23 leave one question open: a
    /// have-flag written from outside reverts by itself about 7–8 s later, on an
    /// end that never called CheckInventory. Guessing which code does it costs
    /// more than asking: every method of InventoryController is wrapped so its
    /// entry and exit can be compared, and a method whose exit leaves the flags
    /// changed is named in the log. The same log counts the calls, so a periodic
    /// reconciler and a one-shot do the same.
    ///
    /// Reading a flag goes through a property getter, and those getters are on
    /// the same object, so the probe must survive re-entering itself: snapshots
    /// live on a stack (one per nesting level), and both the prefix and the
    /// postfix bail out while a snapshot is being taken. Field accessors are
    /// skipped entirely — Il2CppInterop cannot patch them and only warns.
    /// </summary>
    internal sealed class ItemProbe
    {
        internal static ItemProbe Active;

        private HarmonyLib.Harmony _harmony;
        private InventoryController _inv;
        private List<Flag> _flags;
        private readonly List<bool[]> _stack = new List<bool[]>(8);
        private bool _busy;
        private int _updateCalls;
        private readonly Dictionary<string, int> _calls = new Dictionary<string, int>();

        private sealed class Flag
        {
            public string Name;
            public PropertyInfo P;
        }

        public bool Patched { get; private set; }
        public int Methods { get; private set; }
        public int ChangedByGame { get; private set; }

        public void Attach(InventoryController inv, List<KeyValuePair<string, PropertyInfo>> flags)
        {
            _inv = inv;
            _flags = new List<Flag>(flags.Count);
            foreach (var kv in flags) _flags.Add(new Flag { Name = kv.Key, P = kv.Value });

            try
            {
                var t = inv.GetType();
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Instance | BindingFlags.DeclaredOnly);
                _harmony = new HarmonyLib.Harmony("grannycoop.itemprobe");
                Active = this;

                var pre = new HarmonyMethod(typeof(ItemProbe).GetMethod(nameof(Before),
                    BindingFlags.Public | BindingFlags.Static));
                var post = new HarmonyMethod(typeof(ItemProbe).GetMethod(nameof(After),
                    BindingFlags.Public | BindingFlags.Static));

                int ok = 0, skipped = 0;
                var failed = new System.Text.StringBuilder();
                var names = new System.Text.StringBuilder();
                foreach (var m in methods)
                {
                    if (m.IsAbstract || m.ContainsGenericParameters) continue;
                    if (m.IsSpecialName) { skipped++; continue; }   // property accessors
                    try
                    {
                        _harmony.Patch(m, prefix: pre, postfix: post);
                        ok++;
                        names.Append(' ').Append(Sig(m));
                    }
                    catch (Exception ex)
                    {
                        if (failed.Length > 0) failed.Append(", ");
                        failed.Append(m.Name).Append('(').Append(ex.GetType().Name).Append(')');
                    }
                }

                Methods = ok;
                Patched = ok > 0;
                LoggerUtil.Log("COOP itemprobe: patched " + ok + "/" + methods.Length +
                               " InventoryController methods, skipped " + skipped + " accessors" +
                               (failed.Length > 0 ? " failed=" + failed : "") +
                               " flags=" + _flags.Count);
                LoggerUtil.Log("COOP itemprobe: real methods:" + names);
                DumpPickUps(t);
            }
            catch (Exception ex)
            {
                LoggerUtil.Log("COOP itemprobe: setup FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string Sig(MethodBase m)
        {
            var ps = m.GetParameters();
            var sb = new System.Text.StringBuilder();
            sb.Append(m.Name).Append('(');
            for (int i = 0; i < ps.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ps[i].ParameterType.Name);
            }
            return sb.Append(')').ToString();
        }

        /// <summary>
        /// A flag written from outside is reverted by CheckInventory, so the way
        /// to give a guest an item is the game's own pickup entry point, not the
        /// flag. Find it: every type in the game assembly is scanned for a
        /// method with "pickup" in its name and reported with its signature.
        /// </summary>
        private static void DumpPickUps(Type invType)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                int n = 0;
                foreach (var t in invType.Assembly.GetTypes())
                {
                    MethodInfo[] ms;
                    try { ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                             BindingFlags.Instance | BindingFlags.Static |
                                             BindingFlags.DeclaredOnly); }
                    catch (Exception) { continue; }
                    foreach (var m in ms)
                    {
                        if (m.Name.IndexOf("picked", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (m.Name.IndexOf("pickup", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (n++ >= 30) continue;
                        sb.Append(" | ").Append(t.Name).Append('.').Append(Sig(m));
                    }
                }
                LoggerUtil.Log("COOP itemprobe: pickup methods (" + n + "):" + sb);
            }
            catch (Exception ex)
            {
                LoggerUtil.Log("COOP itemprobe: pickup scan FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public void Detach()
        {
            try { _harmony?.UnpatchSelf(); }
            catch (Exception) { }
            _harmony = null;
            _inv = null;
            _flags = null;
            _stack.Clear();
            if (Active == this) Active = null;
        }

        public static void Before(MethodBase __originalMethod)
        {
            var self = Active;
            if (self == null || self._busy) return;
            self.OnEnter(__originalMethod);
        }

        public static void After(MethodBase __originalMethod)
        {
            var self = Active;
            if (self == null || self._busy) return;
            self.OnExit(__originalMethod);
        }

        private void OnEnter(MethodBase m)
        {
            string name = m.Name;
            int n;
            _calls.TryGetValue(name, out n);
            _calls[name] = n + 1;
            if (n == 0)
                LoggerUtil.Log("COOP itemprobe: first call " + name);

            // Update runs every frame; sampling it keeps the reflection cost off
            // the frame budget without missing a reverting tick (10 frames < 0.2 s).
            if (name == "Update")
            {
                _updateCalls++;
                if (_updateCalls % 10 != 0) { _stack.Add(null); return; }
            }

            _busy = true;
            var snap = Snapshot();
            _busy = false;
            _stack.Add(snap);
        }

        private void OnExit(MethodBase m)
        {
            if (_stack.Count == 0) return;
            var before = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            if (before == null) return;

            _busy = true;
            var now = Snapshot();
            _busy = false;
            if (now == null || now.Length != before.Length) return;

            string changed = null;
            for (int i = 0; i < now.Length; i++)
            {
                if (now[i] == before[i]) continue;
                if (changed == null) changed = "";
                else changed += ", ";
                changed += _flags[i].Name + " " + (before[i] ? "have" : "none") + "->" +
                           (now[i] ? "have" : "none");
            }
            if (changed == null) return;

            ChangedByGame++;
            LoggerUtil.Log("COOP itemprobe: " + m.Name + " changed flags: " + changed +
                           " (call #" + _calls[m.Name] + ")");
        }

        private bool[] Snapshot()
        {
            if (_inv == null || _flags == null) return null;
            var a = new bool[_flags.Count];
            for (int i = 0; i < _flags.Count; i++)
            {
                try { a[i] = (bool)_flags[i].P.GetValue(_inv); }
                catch { a[i] = false; }
            }
            return a;
        }

        public string Status()
        {
            return "iprobe=" + (Patched ? "on" : "off") + "/" + Methods +
                   " iChg=" + ChangedByGame;
        }

        public string CallCounts()
        {
            var s = new System.Text.StringBuilder();
            foreach (var kv in _calls) s.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            return s.ToString();
        }
    }
}
