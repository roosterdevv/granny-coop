using System;
using System.Collections.Generic;
using System.Reflection;
using Il2Cpp;
using UnityEngine;

namespace GrannyCoop
{
    /// <summary>
    /// Shared inventory (NOTES.md section 5). InventoryController keeps one
    /// (world object, hand transform, have-bool) triple per item; the IL2CPP
    /// fields come through Il2CppInterop as properties, the same way the doors'
    /// DoorOpen does. Only the have-bools are replicated: they are what the
    /// game's own Update/CheckInventory reads, and they are cheap enough to
    /// send as a whole table (60 items, 5 bytes each).
    ///
    /// Host-authoritative: the host polls the flags at 5 Hz, sends a (hash,
    /// state) pair on each change and a full table at connect and every 10 s.
    /// The client requests the table on join, then writes what it receives.
    /// </summary>
    internal sealed class ItemSync
    {
        public const byte KindState = 0;           // host -> client, one item
        public const byte KindSnapshot = 1;        // host -> client, the whole table
        public const byte KindSnapshotRequest = 2; // client -> host, ask for it

        private const float PollInterval = 0.2f;
        private const float SnapshotInterval = 10f;
        private const float SnapshotRequestInterval = 5f;
        private const float SettleDelay = 1.5f;

        private sealed class Entry
        {
            public string Name;
            public uint Hash;
            public PropertyInfo Have;
            public PropertyInfo Obj;
            public PropertyInfo Tr;
            public bool Last;
        }

        private readonly List<Entry> _items = new List<Entry>();
        private MonoBehaviour _inv;
        private bool _prepared;
        private float _prepAccum;
        private float _pollAccum;
        private float _snapAccum;
        private float _reqSnapAccum;
        private bool _gotSnapshot;

        public int Sent { get; private set; }
        public int Recv { get; private set; }
        public int Applied { get; private set; }

        /// <summary>QA only (--coop-itemtest): the host flips one have-flag on
        /// and off through the same property the game uses, so the observe ->
        /// send -> apply path runs without a human pickup. Around each write it
        /// logs whether the value stuck and what the item's own objects did,
        /// which is also how we learn what a flag alone does to the game.</summary>
        public bool ItemTest;
        private const float TestPeriod = 10f;
        private float _testClock = -1f;
        private Entry _testItem;

        public void Reset()
        {
            _items.Clear();
            _inv = null;
            _prepared = false;
            _prepAccum = _pollAccum = _snapAccum = _reqSnapAccum = 0f;
            _gotSnapshot = false;
            Sent = Recv = Applied = 0;
            _testClock = -1f;
            _testItem = null;
        }

        public void Tick(float dt, GameObject player, CoopRole role, Func<byte[], bool> send)
        {
            if (role == CoopRole.Off) return;
            if (player == null) return;

            if (!_prepared)
            {
                // Only world discovery waits for an active player: the game keeps
                // the Player root inactive through its intro, and the inventory
                // may still be filling in. Once mapped, sync does not depend on it.
                if (!player.activeSelf) return;
                _prepAccum += dt;
                if (_prepAccum < SettleDelay) return;
                Prepare(role);
                if (!_prepared) return;
                if (role == CoopRole.Join)
                {
                    if (send(BuildKindBody(KindSnapshotRequest))) Sent++;
                }
            }
            // Past this point keep polling while the Player root is inactive: the
            // game reconciles have-flags on its own day transitions, and pausing
            // here left the other end holding flags we had already dropped.

            _pollAccum += dt;
            if (_pollAccum >= PollInterval)
            {
                _pollAccum = 0f;
                if (role == CoopRole.Host) Poll(send);

                if (role == CoopRole.Host)
                {
                    _snapAccum += PollInterval;
                    if (_snapAccum >= SnapshotInterval)
                    {
                        _snapAccum = 0f;
                        if (send(BuildSnapshotBody())) Sent++;
                    }
                }
            }

            if (role == CoopRole.Host && ItemTest) TickItemTest(dt);

            // Until the first table arrives, ask again: the host may still have
            // been in its menu when the join handshake completed.
            if (role == CoopRole.Join && !_gotSnapshot)
            {
                _reqSnapAccum += dt;
                if (_reqSnapAccum >= SnapshotRequestInterval)
                {
                    _reqSnapAccum = 0f;
                    if (send(BuildKindBody(KindSnapshotRequest))) Sent++;
                }
            }
        }

        // ---- discovery -------------------------------------------------------

        private void Prepare(CoopRole role)
        {
            _prepared = true;

            try
            {
                var all = UnityEngine.Object.FindObjectsByType<InventoryController>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    _inv = all[i];
                    break;
                }
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP items: scan FAILED: " + e.GetType().Name + ": " + e.Message);
                return;
            }

            if (_inv == null)
            {
                LoggerUtil.Log("COOP items: no InventoryController in the scene");
                return;
            }

            PropertyInfo[] props;
            FieldInfo[] fields;
            try
            {
                var t = _inv.GetType();
                props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP items: reflect FAILED: " + e.GetType().Name + ": " + e.Message);
                return;
            }

            var byName = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in props) byName[p.Name] = p;

            int haveProps = 0, writable = 0, trueNow = 0, objOk = 0, trOk = 0, dupes = 0;
            var trues = new System.Text.StringBuilder();
            var seen = new HashSet<uint>();

            foreach (var p in props)
            {
                if (!p.Name.StartsWith("have", StringComparison.OrdinalIgnoreCase)) continue;
                haveProps++;

                var e = new Entry { Name = p.Name.Substring(4), Have = p };
                e.Hash = Fnv(e.Name.ToLowerInvariant());
                if (!seen.Add(e.Hash)) { dupes++; continue; }

                byName.TryGetValue(e.Name, out e.Obj);
                byName.TryGetValue("new" + e.Name, out e.Tr);

                if (p.CanWrite) writable++;

                bool v = false;
                if (p.CanRead)
                {
                    try { v = (bool)p.GetValue(_inv); }
                    catch { }
                }
                e.Last = v;
                if (v)
                {
                    trueNow++;
                    trues.Append(' ').Append(e.Name);
                }

                try { if (e.Obj != null && e.Obj.GetValue(_inv) != null) objOk++; } catch { }
                try { if (e.Tr != null && e.Tr.GetValue(_inv) as Transform != null) trOk++; } catch { }

                _items.Add(e);
            }

            LoggerUtil.Log("COOP items: prepared " + _items.Count + " items as " + role +
                           " path=" + PathOf(_inv.transform) +
                           " type=" + _inv.GetType().FullName +
                           " props=" + props.Length + " fields=" + fields.Length +
                           " haveProps=" + haveProps + " writable=" + writable +
                           " objOk=" + objOk + " trOk=" + trOk + " dupes=" + dupes +
                           " trueAtStart=" + trueNow + ":" + trues);

            _testItem = FindItem("hammare") ?? (_items.Count > 0 ? _items[0] : null);
        }

        private Entry FindItem(string name)
        {
            foreach (var e in _items)
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        // ---- state -----------------------------------------------------------

        private bool Read(Entry e)
        {
            try { return e.Have != null && e.Have.CanRead && (bool)e.Have.GetValue(_inv); }
            catch { return false; }
        }

        private bool Write(Entry e, bool value)
        {
            try
            {
                if (e.Have == null || !e.Have.CanWrite) return false;
                e.Have.SetValue(_inv, value);
                return true;
            }
            catch (Exception ex)
            {
                LoggerUtil.Log("COOP items: write FAILED on " + e.Name + ": " +
                               ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private void Poll(Func<byte[], bool> send)
        {
            foreach (var e in _items)
            {
                bool v = Read(e);
                if (v == e.Last) continue;
                e.Last = v;
                if (send(BuildStateBody(e.Hash, v))) Sent++;
                LoggerUtil.Log("COOP items: local " + e.Name + " -> " +
                               (v ? "have" : "none"));
            }
        }

        private void Apply(Entry e, bool value)
        {
            bool before = Read(e);
            bool ok = Write(e, value);
            e.Last = value;
            Applied++;

            // The have-flag alone is the reliable part (NOTES 15): calling
            // CheckInventory here would re-derive the flag from the objects and
            // undo the write, so it stays out of the apply path until the
            // object side is understood.
            LoggerUtil.Log("COOP items: apply " + e.Name + "=" + (value ? "have" : "none") +
                           " was=" + (before ? "have" : "none") +
                           " now=" + (Read(e) ? "have" : "none") +
                           (ok ? "" : " (not writable)") + " " + Describe(e));
        }

        /// <summary>Run InventoryController.CheckInventory so the game itself
        /// reconciles the hand objects with the flags we just wrote. Failure is
        /// logged once, then quietly retried on the next apply.</summary>
        private bool Refresh()
        {
            if (_inv == null) return false;
            try
            {
                ((InventoryController)_inv).CheckInventory();
                _refreshOk++;
                return true;
            }
            catch (Exception ex)
            {
                if (_refreshFails++ == 0)
                    LoggerUtil.Log("COOP items: CheckInventory FAILED: " +
                                   ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private int _refreshOk;
        private int _refreshFails;

        /// <summary>Read-only picture of the objects behind one flag, so a log
        /// can show whether a have-write alone moved anything.</summary>
        private string Describe(Entry e)
        {
            return "obj=" + DescribeObj(e) + " tr=" + DescribeTr(e);
        }

        private string DescribeObj(Entry e)
        {
            try
            {
                var go = e.Obj != null ? e.Obj.GetValue(_inv) as GameObject : null;
                if (go == null) return e.Obj != null ? "null" : "-";
                return go.name + "(active=" + (go.activeSelf ? "y" : "n") +
                       " inHierarchy=" + (go.activeInHierarchy ? "y" : "n") +
                       " children=" + go.transform.childCount +
                       " path=" + PathOf(go.transform) + ")";
            }
            catch { return "!"; }
        }

        private string DescribeTr(Entry e)
        {
            try
            {
                var tr = e.Tr != null ? e.Tr.GetValue(_inv) as Transform : null;
                if (tr == null) return e.Tr != null ? "null" : "-";
                string parent = tr.parent != null ? tr.parent.name : "-";
                return tr.name + "(active=" + (tr.gameObject.activeSelf ? "y" : "n") +
                       " inHierarchy=" + (tr.gameObject.activeInHierarchy ? "y" : "n") +
                       " parent=" + parent + " pos=" + tr.position +
                       " path=" + PathOf(tr) + ")";
            }
            catch { return "!"; }
        }

        /// <summary>QA only: move the two objects a pickup moves, so we can see
        /// which of them the game derives the have-flag from.</summary>
        private void SetVisual(Entry e, bool held)
        {
            var hand = e.Obj != null ? e.Obj.GetValue(_inv) as GameObject : null;
            var world = e.Tr != null ? e.Tr.GetValue(_inv) as Transform : null;

            if (held)
            {
                try
                {
                    if (hand != null) hand.SetActive(true);
                }
                catch (Exception ex) { LoggerUtil.Log("COOP itemtest: hand FAILED: " + ex.GetType().Name + ": " + ex.Message); }

                // The hand slot starts empty; a real pickup presumably puts the
                // item's model in it. Try that, and see whether CheckInventory
                // then keeps the flag we wrote.
                try
                {
                    if (hand != null && hand.transform.childCount == 0 && world != null)
                    {
                        _spawned = UnityEngine.Object.Instantiate(world.gameObject, hand.transform);
                        _spawned.name = "Coop" + e.Name;
                        _spawned.SetActive(true);
                        _spawned.transform.localPosition = Vector3.zero;
                        _spawned.transform.localRotation = Quaternion.identity;
                    }
                }
                catch (Exception ex) { LoggerUtil.Log("COOP itemtest: spawn FAILED: " + ex.GetType().Name + ": " + ex.Message); }

                try
                {
                    if (world != null) world.gameObject.SetActive(false);
                }
                catch (Exception ex) { LoggerUtil.Log("COOP itemtest: world FAILED: " + ex.GetType().Name + ": " + ex.Message); }
            }
            else
            {
                try { if (_spawned != null) UnityEngine.Object.Destroy(_spawned); _spawned = null; }
                catch (Exception) { _spawned = null; }
                try { if (hand != null) hand.SetActive(false); }
                catch (Exception) { }
                try { if (world != null) world.gameObject.SetActive(true); }
                catch (Exception) { }
            }
        }

        private GameObject _spawned;

        // ---- QA hook ---------------------------------------------------------

        private int _testPhase;
        private float _testAt;

        private void TickItemTest(float dt)
        {
            if (_testItem == null) return;

            if (_testClock < 0f)
            {
                _testClock = 0f;
                _testAt = 2f;
                LoggerUtil.Log("COOP itemtest: target=" + _testItem.Name +
                               " writable=" + (_testItem.Have != null && _testItem.Have.CanWrite) +
                               " start " + Describe(_testItem));
            }

            _testClock += dt;
            if (_testClock < _testAt) return;

            switch (_testPhase)
            {
                case 0:
                    LoggerUtil.Log("COOP itemtest: writing have=true, before=" +
                                   (Read(_testItem) ? "have" : "none") + " " + Describe(_testItem));
                    Write(_testItem, true);
                    SetVisual(_testItem, true);
                    LoggerUtil.Log("COOP itemtest: after write+visual " + Describe(_testItem));
                    _testAt = _testClock + 1f;
                    _testPhase = 1;
                    break;

                case 1:
                    LoggerUtil.Log("COOP itemtest: +1s have=" +
                                   (Read(_testItem) ? "have" : "none") + " " + Describe(_testItem));
                    // Hold without touching anything for 8 s: does the game's own
                    // code revert the flag, or only our explicit CheckInventory?
                    _testAt = _testClock + 7f;
                    _testPhase = 5;
                    break;

                case 5:
                    LoggerUtil.Log("COOP itemtest: +8s hold have=" +
                                   (Read(_testItem) ? "have" : "none") + " " + Describe(_testItem));
                    LoggerUtil.Log("COOP itemtest: refresh -> " +
                                   (Refresh() ? "ok" : "no") + " have=" +
                                   (Read(_testItem) ? "have" : "none") + " " + Describe(_testItem));
                    _testAt = _testClock + 3f;
                    _testPhase = 2;
                    break;

                case 2:
                    LoggerUtil.Log("COOP itemtest: writing have=false, before=" +
                                   (Read(_testItem) ? "have" : "none") + " " + Describe(_testItem));
                    Write(_testItem, false);
                    SetVisual(_testItem, false);
                    LoggerUtil.Log("COOP itemtest: after write+visual " + Describe(_testItem));
                    _testAt = _testClock + 1f;
                    _testPhase = 3;
                    break;

                case 3:
                    LoggerUtil.Log("COOP itemtest: +1s have=" +
                                   (Read(_testItem) ? "have" : "none") + " " + Describe(_testItem));
                    LoggerUtil.Log("COOP itemtest: refresh -> " +
                                   (Refresh() ? "ok" : "no") + " have=" +
                                   (Read(_testItem) ? "have" : "none") + " " + Describe(_testItem));
                    _testAt = _testClock + (TestPeriod - 6f);
                    _testPhase = 4;
                    break;

                default:
                    _testPhase = 0;
                    _testAt = _testClock + 2f;
                    break;
            }
        }

        // ---- wire -------------------------------------------------------------

        private static byte[] BuildStateBody(uint hash, bool value)
        {
            using (var ms = new System.IO.MemoryStream(8))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(KindState);
                w.Write(hash);
                w.Write((byte)(value ? 1 : 0));
                return ms.ToArray();
            }
        }

        private byte[] BuildSnapshotBody()
        {
            using (var ms = new System.IO.MemoryStream(512))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(KindSnapshot);
                w.Write((byte)Math.Min(255, _items.Count));
                foreach (var e in _items)
                {
                    // Publish the live flags, not Last: Poll stops while the
                    // Player root is inactive, so Last can be seconds behind
                    // exactly when a client asks for the table.
                    bool v = e.Have != null && e.Have.CanRead ? Read(e) : e.Last;
                    w.Write(e.Hash);
                    w.Write((byte)(v ? 1 : 0));
                }
                return ms.ToArray();
            }
        }

        private static byte[] BuildKindBody(byte kind)
        {
            using (var ms = new System.IO.MemoryStream(4))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(kind);
                return ms.ToArray();
            }
        }

        public void OnMessage(MsgReader r, CoopRole role, Func<byte[], bool> send)
        {
            Recv++;
            byte kind = r.Byte();

            switch (kind)
            {
                case KindState:
                {
                    if (role != CoopRole.Join || _inv == null) break;
                    uint hash = r.U32();
                    bool value = r.Byte() != 0;
                    var e = Find(hash);
                    if (e == null) break;
                    Apply(e, value);
                    break;
                }

                case KindSnapshot:
                {
                    if (role != CoopRole.Join || _inv == null) break;
                    int n = r.Byte();
                    int applied = 0;
                    for (int i = 0; i < n; i++)
                    {
                        uint hash = r.U32();
                        bool value = r.Byte() != 0;
                        var e = Find(hash);
                        if (e == null) continue;
                        if (Read(e) != value) { Apply(e, value); applied++; }
                        else e.Last = value;
                    }
                    _gotSnapshot = true;
                    LoggerUtil.Log("COOP items: snapshot n=" + n + " changed=" + applied);
                    break;
                }

                case KindSnapshotRequest:
                {
                    if (role != CoopRole.Host || !_prepared) break;
                    send(BuildSnapshotBody());
                    break;
                }
            }
        }

        private Entry Find(uint hash)
        {
            foreach (var e in _items) if (e.Hash == hash) return e;
            return null;
        }

        private static uint Fnv(string s)
        {
            uint h = 2166136261u;
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 16777619u;
            }
            return h;
        }

        private static string PathOf(Transform t)
        {
            string s = t.name;
            var p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 12) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }

        /// <summary>QA only (soak runs): a fingerprint of the live have-flags in
        /// the same canonical form as DoorSync.StateFingerprint, so the two
        /// ends' status lines can be diffed for divergence.</summary>
        public string StateFingerprint()
        {
            // See DoorSync.StateFingerprint: a blind end is not a match.
            if (!_prepared) return "not-ready";
            var pairs = new List<KeyValuePair<uint, byte>>();
            if (_inv != null)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    var e = _items[i];
                    if (e.Have == null || !e.Have.CanRead) continue;
                    bool v;
                    try { v = (bool)e.Have.GetValue(_inv); }
                    catch { continue; }
                    pairs.Add(new KeyValuePair<uint, byte>(e.Hash, v ? (byte)1 : (byte)0));
                }
            }
            return Protocol.Fingerprint(pairs);
        }

        public string Status()
        {
            return "items=" + _items.Count + " iSent=" + Sent +
                   " iRecv=" + Recv + " iApplied=" + Applied +
                   " iRefresh=" + _refreshOk + "/" + (_refreshOk + _refreshFails);
        }
    }
}
