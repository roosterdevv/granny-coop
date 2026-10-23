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
        public const byte KindPlacement = 3;       // both ways, where items lie in the world
        public const byte KindPlacementRequest = 4; // client -> host, ask for the placement table

        private const float PollInterval = 0.2f;
        private const float SnapshotInterval = 10f;
        private const float SnapshotRequestInterval = 5f;
        private const float SettleDelay = 1.5f;
        /// <summary>v0.8.0: the two ends roll their own random item placement,
        /// so the host re-sends its world layout on this cadence; a local drop
        /// also sends one placement straight away.</summary>
        private const float PlacementInterval = 15f;
        private const int PlacementChunk = 20;      // 2 + n*33 = 662 bytes
        private const float DropPlaceDelay = 0.4f;  // let the game move it first

        /// <summary>Owner byte on the wire (protocol v2): which end's hands the
        /// item is in. 0 is "nobody / not attributable" — the item was already
        /// held when the session started — and keeps the v0.6.0 behaviour of
        /// mirroring it into the receiving end's own hand.</summary>
        public const byte OwnerNone = 0;
        public const byte OwnerHost = 1;
        public const byte OwnerGuest = 2;

        private sealed class Entry
        {
            public string Name;
            public uint Hash;
            public PropertyInfo Have;
            public PropertyInfo Obj;
            public PropertyInfo Tr;
            public bool Last;
            /// <summary>Which end holds the item, as last agreed on the wire.</summary>
            public byte Owner;
            /// <summary>Copy of the world model placed in the hand while this
            /// end has the item. Only the end that applies a state creates one
            /// (see ApplyVisual); the end that really picked the item up has the
            /// game's own model there.</summary>
            public GameObject Spawned;
            /// <summary>Last placement agreed on the wire (v0.8.0). The game
            /// rolls its item positions from its own RNG at scene start, so the
            /// two ends can disagree even with the flags in sync; the host's
            /// layout is the authority.</summary>
            public Vector3 Pos;
            public Quaternion Rot;
            public bool Active;
            public bool HavePos;
            /// <summary>Set when this end dropped the item; the world object is
            /// sent once the game has had a moment to put it back (v0.8.0).</summary>
            public float PlaceAt;
        }

        /// <summary>The remote player's avatar, so an item a peer picked up is
        /// shown in the peer's hands instead of in ours (v0.7.0).</summary>
        public RemoteAvatar Avatar { get; set; }

        private readonly List<Entry> _items = new List<Entry>();
        private MonoBehaviour _inv;
        private bool _prepared;
        private float _prepAccum;
        private float _pollAccum;
        private float _snapAccum;
        private float _reqSnapAccum;
        private bool _gotSnapshot;
        private float _clock;          // seconds since the join/host started ticking
        private float _placeAccum;
        private float _reqPlaceAccum;
        private bool _gotPlacement;
        private bool _placePending;    // host: send the table on the next tick

        public int Sent { get; private set; }
        public int Recv { get; private set; }
        public int Applied { get; private set; }
        public int Placed { get; private set; }

        /// <summary>QA only (--coop-itemtest): the host flips one have-flag on
        /// and off through the same property the game uses, so the observe ->
        /// send -> apply path runs without a human pickup. Around each write it
        /// logs whether the value stuck and what the item's own objects did,
        /// which is also how we learn what a flag alone does to the game.</summary>
        public bool ItemTest;

        /// <summary>QA only (--coop-placetest): once the host's layout has
        /// landed, move one free item on this end by 2.5 m and ask for the
        /// table again, so the host's placement has to pull it back. A paired
        /// run on one PC rolls the same layout on both ends, so without a
        /// forced divergence the correction path is never exercised.</summary>
        public bool PlaceTest;
        private bool _nudged;
        private const float TestPeriod = 10f;
        private float _testClock = -1f;
        private Entry _testItem;

        /// <summary>QA only (--coop-itemprobe): wrap every InventoryController
        /// method so the one that reverts a written have-flag names itself.</summary>
        public bool ItemProbe;

        private readonly ItemProbe _probe = new ItemProbe();

        public void Reset()
        {
            _probe.Detach();
            if (Avatar != null) Avatar.ClearHeldItem();
            foreach (var e in _items)
            {
                try { if (e.Spawned != null) UnityEngine.Object.Destroy(e.Spawned); }
                catch (Exception) { }
                e.Spawned = null;
            }
            _items.Clear();
            _inv = null;
            _prepared = false;
            _prepAccum = _pollAccum = _snapAccum = _reqSnapAccum = 0f;
            _gotSnapshot = false;
            _clock = _placeAccum = _reqPlaceAccum = 0f;
            _gotPlacement = false;
            _placePending = false;
            _nudged = false;
            Sent = Recv = Applied = Placed = 0;
            _testClock = -1f;
            _testItem = null;
        }

        public void Tick(float dt, GameObject player, CoopRole role, Func<byte[], bool> send)
        {
            if (role == CoopRole.Off) return;
            if (player == null) return;

            _clock += dt;

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
                    if (send(BuildKindBody(KindPlacementRequest))) Sent++;
                }
                // The host's own layout is the authority; publish it as soon as
                // the scene is mapped, not 15 s later.
                if (role == CoopRole.Host) _placePending = true;
            }
            // Past this point keep polling while the Player root is inactive: the
            // game reconciles have-flags on its own day transitions, and pausing
            // here left the other end holding flags we had already dropped.

            _pollAccum += dt;
            if (_pollAccum >= PollInterval)
            {
                _pollAccum = 0f;
                // Both ends poll: the host owns the table, but a guest that
                // picks something up has to be able to say so, or its own
                // pickup is undone by the next table. Until the first table
                // arrives the guest must stay quiet — its freshly scanned flags
                // are local defaults, not news.
                if (!(role == CoopRole.Join && !_gotSnapshot)) Poll(role, send);

                // A drop we made ourselves: the game has just put the object
                // back where it belongs, so tell the peer where that is.
                for (int i = 0; i < _items.Count; i++)
                {
                    var e = _items[i];
                    if (e.PlaceAt <= 0f || _clock < e.PlaceAt) continue;
                    e.PlaceAt = 0f;
                    if (Read(e)) continue;
                    if (SendOnePlacement(e, send)) Sent++;
                }

                if (role == CoopRole.Host)
                {
                    _placeAccum += PollInterval;
                    if (_placePending || _placeAccum >= PlacementInterval)
                    {
                        _placeAccum = 0f;
                        _placePending = false;
                        SendPlacements(send);
                    }

                    _snapAccum += PollInterval;
                    if (_snapAccum >= SnapshotInterval)
                    {
                        _snapAccum = 0f;
                        if (send(BuildSnapshotBody())) Sent++;
                    }
                }
            }

            // The QA cycle runs on either end: on the host it is the ordinary
            // table direction, on the guest it checks the pickup upstream.
            if (ItemTest) TickItemTest(dt);
            if (PlaceTest) TickPlaceTest(role);

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

            if (role == CoopRole.Join && !_gotPlacement)
            {
                _reqPlaceAccum += dt;
                if (_reqPlaceAccum >= SnapshotRequestInterval)
                {
                    _reqPlaceAccum = 0f;
                    if (send(BuildKindBody(KindPlacementRequest))) Sent++;
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

            // The two ends roll their own item layout from their own RNG, so
            // this line is the QA evidence of divergence: it is printed before
            // the first placement table is applied.
            LoggerUtil.Log("COOP items: world at start " + WorldDump());

            _testItem = FindItem("hammare") ?? (_items.Count > 0 ? _items[0] : null);

            if (ItemProbe)
            {
                var flags = new List<KeyValuePair<string, PropertyInfo>>(_items.Count);
                foreach (var e in _items) flags.Add(new KeyValuePair<string, PropertyInfo>(e.Name, e.Have));
                _probe.Attach((InventoryController)_inv, flags);
            }
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

        /// <summary>Which side a role is, as the wire owner byte spells it.</summary>
        private static byte SideOf(CoopRole role)
        {
            if (role == CoopRole.Host) return OwnerHost;
            if (role == CoopRole.Join) return OwnerGuest;
            return OwnerNone;
        }

        private static string OwnerName(byte owner)
        {
            if (owner == OwnerHost) return "host";
            if (owner == OwnerGuest) return "guest";
            return "-";
        }

        private void Poll(CoopRole role, Func<byte[], bool> send)
        {
            foreach (var e in _items)
            {
                bool v = Read(e);
                if (v == e.Last) continue;
                e.Last = v;
                // A local pickup is ours, a local drop belongs to nobody; the
                // peer applies whichever the message says.
                e.Owner = v ? SideOf(role) : OwnerNone;
                if (send(BuildStateBody(e.Hash, v, e.Owner))) Sent++;
                // A drop lands somewhere; say where once the game has moved it.
                if (!v) e.PlaceAt = _clock + DropPlaceDelay;
                LoggerUtil.Log("COOP items: local " + e.Name + " -> " +
                               (v ? "have" : "none") + " owner=" + OwnerName(e.Owner));
            }
        }

        private void Apply(Entry e, bool value, byte owner, CoopRole role)
        {
            bool before = Read(e);
            bool ok = Write(e, value);
            e.Last = value;
            e.Owner = value ? owner : OwnerNone;
            Applied++;

            // The flag alone is durable (QA: it held for 60 s untouched) but it
            // does not move a single object — the game's Update ignores it — so
            // the receiving end has to mirror the objects too, or the item stays
            // in the world and never shows in the hand. CheckInventory stays out
            // of this path: it re-derives every flag from the world objects and
            // is the one thing that undoes the write (Notes §27).
            //
            // v0.7.0: an item the peer picked up goes into the peer's hands
            // (the avatar), not ours. Owner 0 means "unknown" (held before the
            // session started) and keeps the old local-hand mirror.
            var world = WorldOf(e);
            bool mine = !value || owner == OwnerNone || owner == SideOf(role);
            if (mine)
            {
                // Covers both "we own it now" and "nobody owns it any more":
                // either way the peer's avatar must let go of this item.
                if (Avatar != null && world != null) Avatar.ClearHeldItem(world.gameObject);
                ApplyVisual(e, value);
            }
            else
            {
                HideLocal(e);
                // Clone before hiding the world object: an inactive source
                // produces an inactive clone that would never render.
                if (Avatar != null) Avatar.SetHeldItem(world != null ? world.gameObject : null, e.Name);
                try { if (world != null) world.gameObject.SetActive(false); }
                catch (Exception ex) { LoggerUtil.Log("COOP items: world FAILED on " + e.Name + ": " + ex.GetType().Name + ": " + ex.Message); }
            }

            LoggerUtil.Log("COOP items: apply " + e.Name + "=" + (value ? "have" : "none") +
                           " owner=" + OwnerName(e.Owner) +
                           " was=" + (before ? "have" : "none") +
                           " now=" + (Read(e) ? "have" : "none") +
                           (ok ? "" : " (not writable)") + " " + Describe(e));
        }

        private Transform WorldOf(Entry e)
        {
            try { return e.Tr != null ? e.Tr.GetValue(_inv) as Transform : null; }
            catch { return null; }
        }

        // ---- placement (v0.8.0) ---------------------------------------------
        //
        // The game rolls its item layout from its own RNG when the scene starts,
        // and the two instances do that independently, so the same logical item
        // sits in two different places. Replaying the seed is not possible from
        // outside (the RNG state and the call order are not observable, and the
        // host's scene is already rolled when the guest joins), so the host's
        // layout is replicated instead: each end moves its own copy of the
        // item's world object to the host's position, rotation and active state.

        /// <summary>Copy one item's world placement into the entry. Held items
        /// are skipped — while an item is in a hand there is no world placement
        /// to share — and the drop path sends where it lands.</summary>
        private void Capture(Entry e)
        {
            var world = WorldOf(e);
            if (world == null) return;
            if (Read(e)) return;
            try
            {
                var q = world.rotation;
                // q and -q are the same rotation; normalise so the two ends'
                // fingerprints agree.
                if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                e.Pos = world.position;
                e.Rot = q;
                e.Active = world.gameObject.activeSelf;
                e.HavePos = true;
            }
            catch (Exception) { }
        }

        private void CaptureAll()
        {
            for (int i = 0; i < _items.Count; i++) Capture(_items[i]);
        }

        /// <summary>Move this end's copy of an item to the host's placement.
        /// Items in somebody's hand are left alone: the have-flag path owns
        /// those, and the next table after the drop puts the object back.</summary>
        private void ApplyPlacement(Entry e, Vector3 pos, Quaternion rot, bool active)
        {
            e.Pos = pos;
            e.Rot = rot;
            e.Active = active;
            e.HavePos = true;

            var world = WorldOf(e);
            if (world == null) return;
            if (Read(e)) return;

            bool moved;
            try
            {
                moved = (world.position - pos).sqrMagnitude > 0.0001f ||
                        Quaternion.Angle(world.rotation, rot) > 1f;
                world.position = pos;
                world.rotation = rot;
                var go = world.gameObject;
                if (go.activeSelf != active) go.SetActive(active);
            }
            catch (Exception ex)
            {
                LoggerUtil.Log("COOP items: place FAILED on " + e.Name + ": " +
                               ex.GetType().Name + ": " + ex.Message);
                return;
            }

            Placed++;
            if (moved)
            {
                LoggerUtil.Log("COOP items: place " + e.Name + " -> " + Fmt(pos) +
                               " active=" + (active ? "y" : "n"));
            }
        }

        private static string Fmt(Vector3 p)
        {
            return "(" + p.x.ToString("0.00") + ", " + p.y.ToString("0.00") +
                   ", " + p.z.ToString("0.00") + ")";
        }

        private static uint Mix(uint h, uint v)
        {
            h ^= v;
            h *= 16777619u;
            return h;
        }

        /// <summary>Fingerprint of where this end's item objects actually are
        /// right now, in centimetres, held ones included (they are inactive on
        /// both ends). The two status lines must print the same value once the
        /// placement tables have been applied; before that they should differ,
        /// which is the bug v0.8.0 fixes.</summary>
        public string LivePlacementFingerprint()
        {
            if (!_prepared) return "not-ready";
            uint h = 2166136261u;
            int n = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var e = _items[i];
                var t = WorldOf(e);
                if (t == null) continue;
                n++;
                var p = t.position;
                h = Mix(h, e.Hash);
                h = Mix(h, (uint)Mathf.RoundToInt(p.x * 100f));
                h = Mix(h, (uint)Mathf.RoundToInt(p.y * 100f));
                h = Mix(h, (uint)Mathf.RoundToInt(p.z * 100f));
                h = Mix(h, t.gameObject.activeSelf ? 1u : 0u);
            }
            return n + ":" + h.ToString("X8");
        }

        private string WorldDump()
        {
            var sb = new System.Text.StringBuilder();
            int n = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var t = WorldOf(_items[i]);
                if (t == null) continue;
                n++;
                if (n > 12) continue;
                if (n > 1) sb.Append("; ");
                sb.Append(_items[i].Name).Append('@').Append(Fmt(t.position));
            }
            if (n > 12) sb.Append("; +").Append(n - 12);
            return "n=" + n + " fp=" + LivePlacementFingerprint() + " " + sb;
        }

        private byte[] BuildPlacementBody(int start, int count)
        {
            using (var ms = new System.IO.MemoryStream(2 + count * 33))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(KindPlacement);
                w.Write((byte)count);
                for (int i = start; i < start + count; i++)
                {
                    var e = _items[i];
                    w.Write(e.Hash);
                    if (e.HavePos)
                    {
                        w.Write(e.Pos.x); w.Write(e.Pos.y); w.Write(e.Pos.z);
                        w.Write(e.Rot.x); w.Write(e.Rot.y); w.Write(e.Rot.z); w.Write(e.Rot.w);
                        w.Write((byte)(e.Active ? 1 : 0));
                    }
                    else
                    {
                        // 7 floats + a marker byte keeps every record the same
                        // size, so a chunk can be rebuilt from an offset.
                        for (int k = 0; k < 7; k++) w.Write(0f);
                        w.Write((byte)2);   // 2 = no placement on this end
                    }
                }
                return ms.ToArray();
            }
        }

        private void SendPlacements(Func<byte[], bool> send)
        {
            if (_items.Count == 0) return;
            CaptureAll();
            int chunks = 0;
            for (int i = 0; i < _items.Count; i += PlacementChunk)
            {
                int n = Math.Min(PlacementChunk, _items.Count - i);
                if (send(BuildPlacementBody(i, n))) { Sent++; chunks++; }
            }
            LoggerUtil.Log("COOP items: placement out n=" + _items.Count +
                           " chunks=" + chunks + " fp=" + LivePlacementFingerprint());
        }

        /// <summary>One item's placement, sent right after a local drop so the
        /// peer does not have to wait for the next full table.</summary>
        private bool SendOnePlacement(Entry e, Func<byte[], bool> send)
        {
            Capture(e);
            if (!e.HavePos) return false;
            using (var ms = new System.IO.MemoryStream(35))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(KindPlacement);
                w.Write((byte)1);
                w.Write(e.Hash);
                w.Write(e.Pos.x); w.Write(e.Pos.y); w.Write(e.Pos.z);
                w.Write(e.Rot.x); w.Write(e.Rot.y); w.Write(e.Rot.z); w.Write(e.Rot.w);
                w.Write((byte)(e.Active ? 1 : 0));
                if (!send(ms.ToArray())) return false;
            }
            LoggerUtil.Log("COOP items: dropped " + e.Name + " at " + Fmt(e.Pos) +
                           " active=" + (e.Active ? "y" : "n"));
            return true;
        }

        /// <summary>Clear the local hand slot this end filled for someone
        /// else's pickup. Only touches objects this end created (Spawned) plus
        /// the (empty on this end) hand slot itself.</summary>
        private void HideLocal(Entry e)
        {
            try { if (e.Spawned != null) UnityEngine.Object.Destroy(e.Spawned); }
            catch (Exception) { }
            e.Spawned = null;
            try
            {
                var hand = e.Obj != null ? e.Obj.GetValue(_inv) as GameObject : null;
                if (hand != null) hand.SetActive(false);
            }
            catch (Exception) { }
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

        /// <summary>Move the objects a real pickup moves: show the model in the
        /// hand and take it out of the world. The game does this itself only on
        /// a real pickup, so the end that mirrors a state has to do it by hand.
        /// The hand slot starts empty and is filled with a copy of the world
        /// model, which is what the game appears to do as well (HammerHand has
        /// no children before the first pickup). Safe to call repeatedly.</summary>
        private void ApplyVisual(Entry e, bool held)
        {
            var hand = e.Obj != null ? e.Obj.GetValue(_inv) as GameObject : null;
            var world = e.Tr != null ? e.Tr.GetValue(_inv) as Transform : null;

            if (held)
            {
                try
                {
                    if (hand != null) hand.SetActive(true);
                }
                catch (Exception ex) { LoggerUtil.Log("COOP items: hand FAILED on " + e.Name + ": " + ex.GetType().Name + ": " + ex.Message); }

                try
                {
                    if (hand != null && hand.transform.childCount == 0 && world != null)
                    {
                        e.Spawned = UnityEngine.Object.Instantiate(world.gameObject, hand.transform);
                        e.Spawned.name = "Coop" + e.Name;
                        e.Spawned.SetActive(true);
                        e.Spawned.transform.localPosition = Vector3.zero;
                        e.Spawned.transform.localRotation = Quaternion.identity;
                    }
                }
                catch (Exception ex) { LoggerUtil.Log("COOP items: spawn FAILED on " + e.Name + ": " + ex.GetType().Name + ": " + ex.Message); }

                try
                {
                    if (world != null) world.gameObject.SetActive(false);
                }
                catch (Exception ex) { LoggerUtil.Log("COOP items: world FAILED on " + e.Name + ": " + ex.GetType().Name + ": " + ex.Message); }
            }
            else
            {
                try { if (e.Spawned != null) UnityEngine.Object.Destroy(e.Spawned); e.Spawned = null; }
                catch (Exception) { e.Spawned = null; }
                try { if (hand != null) hand.SetActive(false); }
                catch (Exception) { }
                try { if (world != null) world.gameObject.SetActive(true); }
                catch (Exception) { }
            }
        }

        // ---- QA hook ---------------------------------------------------------

        private int _testPhase;
        private float _testAt;

        /// <summary>QA only: force a divergence the paired-on-one-PC setup does
        /// not produce, then let the host's placement table undo it.</summary>
        private void TickPlaceTest(CoopRole role)
        {
            if (_nudged || role != CoopRole.Join || !_prepared || !_gotPlacement) return;
            if (_clock < 8f) return;

            for (int i = 0; i < _items.Count; i++)
            {
                var e = _items[i];
                if (Read(e)) continue;          // in somebody's hand: no world pose
                var t = WorldOf(e);
                if (t == null) continue;
                Vector3 was;
                try
                {
                    was = t.position;
                    t.position = was + new Vector3(2.5f, 0f, 2.5f);
                }
                catch (Exception) { continue; }

                _nudged = true;
                // Ask now rather than waiting up to 15 s for the host's cadence.
                _gotPlacement = false;
                _reqPlaceAccum = SnapshotRequestInterval;
                LoggerUtil.Log("COOP itemtest: nudged " + e.Name + " " + Fmt(was) +
                               " -> " + Fmt(t.position) + " and asked for the host layout");
                return;
            }
        }

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
                    LoggerUtil.Log("COOP itemtest: after write only " + Describe(_testItem));
                    _testAt = _testClock + 1f;
                    _testPhase = 1;
                    break;

                case 1:
                    // Flag written, visuals untouched: if the game's own Update
                    // shows the item in hand from the flag, the mod needs no
                    // visual code on the receiving end at all.
                    LoggerUtil.Log("COOP itemtest: +1s flag-only have=" +
                                   (Read(_testItem) ? "have" : "none") + " " + Describe(_testItem));
                    ApplyVisual(_testItem, true);
                    LoggerUtil.Log("COOP itemtest: after visual " + Describe(_testItem));
                    // Hold without touching anything for a minute: does the game's
                    // own code revert the flag, or only our explicit CheckInventory?
                    // The 8 s hold this used to do was too short to tell, because
                    // the refresh below was itself what flipped the flag back.
                    _testAt = _testClock + 59f;
                    _testPhase = 5;
                    break;

                case 5:
                    LoggerUtil.Log("COOP itemtest: +60s hold have=" +
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
                    ApplyVisual(_testItem, false);
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

        private static byte[] BuildStateBody(uint hash, bool value, byte owner)
        {
            using (var ms = new System.IO.MemoryStream(8))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(KindState);
                w.Write(hash);
                w.Write((byte)(value ? 1 : 0));
                w.Write(value ? owner : OwnerNone);
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
                    w.Write(v ? e.Owner : OwnerNone);
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
                    // Host -> client is the table; client -> host is a local
                    // pickup the host adopts and then republishes on its own poll.
                    if (_inv == null || !_prepared) break;
                    uint hash = r.U32();
                    bool value = r.Byte() != 0;
                    byte owner = r.Byte();
                    var e = Find(hash);
                    if (e == null) break;
                    Apply(e, value, owner, role);
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
                        byte owner = r.Byte();
                        var e = Find(hash);
                        if (e == null) continue;
                        // Ownership can differ even when the flag already
                        // matches (the item was in our hand before we joined),
                        // and that is what decides whether we show it on the
                        // peer's avatar, so compare it too.
                        if (Read(e) != value || e.Owner != (value ? owner : OwnerNone))
                        {
                            Apply(e, value, owner, role);
                            applied++;
                        }
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

                case KindPlacement:
                {
                    if (_inv == null || !_prepared) break;
                    // Host -> guest is the whole layout table; guest -> host is
                    // only the one-item message sent after a drop, which the
                    // host adopts and republishes in its next table.
                    int n = r.Byte();
                    if (role == CoopRole.Host && n != 1) break;
                    for (int i = 0; i < n; i++)
                    {
                        uint hash = r.U32();
                        float px = r.Float(), py = r.Float(), pz = r.Float();
                        float rx = r.Float(), ry = r.Float(), rz = r.Float(), rw = r.Float();
                        byte state = r.Byte();
                        if (state == 2) continue;   // this end has no placement
                        var e = Find(hash);
                        if (e == null) continue;
                        ApplyPlacement(e, new Vector3(px, py, pz),
                                       new Quaternion(rx, ry, rz, rw), state != 0);
                    }
                    if (role == CoopRole.Join) _gotPlacement = true;
                    break;
                }

                case KindPlacementRequest:
                {
                    if (role != CoopRole.Host || !_prepared) break;
                    SendPlacements(send);
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
                   " iPlaced=" + Placed + " ipos=" + LivePlacementFingerprint() +
                   " iRefresh=" + _refreshOk + "/" + (_refreshOk + _refreshFails) +
                   (ItemProbe ? " " + _probe.Status() : "");
        }
    }
}
