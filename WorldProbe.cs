using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyCoop
{
    /// <summary>
    /// QA-only world inspection (--coop-probe). The generic
    /// FindObjectsByType&lt;MonoBehaviour&gt; returns base-typed proxies, so every
    /// scan here uses the concrete game type. Logs door scripts, day seeding,
    /// the inventory's have-flags and the world item transforms, then flips one
    /// nearby door to learn whether writing DoorOpen animates it (and if not,
    /// whether the game's own timerDooropen coroutine does).
    /// Read-only apart from that one door state change.
    /// </summary>
    internal sealed class WorldProbe
    {
        private float _t;
        private int _stage;
        private float _doorT;
        private MonoBehaviour _door;
        private Transform _part;
        private Vector3 _baseRot;
        private Vector3 _basePos;
        private readonly List<MonoBehaviour> _doors = new List<MonoBehaviour>();
        private readonly List<string> _clips = new List<string>();
        private Il2CppSystem.Collections.IEnumerator _coroutine;
        private int _sample;
        private static readonly float[] Samples = { 0.9f, 1.2f, 1.5f, 1.8f, 2.2f, 2.8f, 3.6f, 4.6f };

        // --coop-dropvis: after the door test, run one vase through the game's
        // own inventory/drop path and diff every item transform around it.
        public bool DropVis;
        /// <summary>Set by the mod each tick: the QA drop must run with a peer
        /// connected, or the placement it produces is never observed.</summary>
        public bool NetConnected;

        /// <summary>Set by the mod once the item table is mapped. The drop test
        /// must not start before that, or the pickup it produces is not seen by
        /// the sync and the run proves nothing.</summary>
        public bool ItemsReady;

        /// <summary>Seconds both gates above have held. The peer's own item
        /// table is mapped later than this end's (it maps after its scene
        /// finishes loading), so starting the moment the local table is ready
        /// would run the whole pickup before the peer could see it.</summary>
        private float _dvReadyT;
        private const float DvSettle = 35f;
        private float _dvT;
        private int _dvStage;
        private InventoryController _dvInv;
        private PickUp _dvPick;
        private Transform _dvWorld;
        private Dictionary<string, ItemSnap> _dvBefore;
        private readonly HashSet<int> _dvVasIds = new HashSet<int>();

        private struct ItemSnap
        {
            public int Id;
            public Vector3 Pos;
            public bool Active;
            public string Path;
        }

        public void Reset()
        {
            _t = 0f;
            _stage = 0;
            _doorT = 0f;
            _door = null;
            _part = null;
            _coroutine = null;
            _sample = 0;
            _doors.Clear();
            _clips.Clear();
            _dvT = 0f;
            _dvStage = 0;
            _dvReadyT = 0f;
            _dvInv = null;
            _dvPick = null;
            _dvWorld = null;
            _dvBefore = null;
            _dvVasIds.Clear();
        }

        public void Tick(GameObject player, float dt)
        {
            if (_stage >= 7)
            {
                if (DropVis) DropVisTick(player, dt);
                return;
            }
            if (player == null || !player.activeSelf) return;

            _t += dt;

            if (_stage == 0 && _t > 2f) { _stage = 1; Census(player); }
            if (_stage == 1 && _t > 2.5f) { _stage = 2; ArmDoor(player); }

            if (_stage >= 2 && _door != null)
            {
                _doorT += dt;
                if (_stage == 2)
                {
                    _stage = 3;
                    LogDoor("before");
                    // Minimal trigger first: does Update() start the game's own
                    // sequence from DoorOpen alone?
                    SetDoorOpenOnly();
                }
                else if (_stage == 3 && _doorT > 2.0f)
                {
                    _stage = 4;
                    LogDoor("after-DoorOpen-only");
                    // The moving flag is what the state machine may watch.
                    SetMovingFlag("DoorMoving");
                }
                else if (_stage == 4 && _doorT > 4.0f)
                {
                    _stage = 5;
                    LogDoor("after-DoorMoving");
                    // Native playback with the exact state enabled and weighted.
                    PlayNamed("InnerdoorOpen");
                    _sample = 0;
                }
                else if (_stage == 5)
                {
                    float[] beats = { 4.2f, 4.7f, 5.3f, 6.0f };
                    while (_sample < beats.Length && _doorT >= beats[_sample])
                    {
                        LogDoor("play+" + (beats[_sample] - 4.0f).ToString("F1"));
                        _sample++;
                    }
                    if (_sample >= beats.Length)
                    {
                        _stage = 6;
                        RestoreClosed();
                        DumpParts("after");
                        DumpParent("after");
                    }
                }
                else if (_stage == 6 && _doorT > 7.5f)
                {
                    LogDoor("after-close");
                    DumpParts("closed");
                    _stage = 7;
                }
            }
        }

        // ---- discovery ------------------------------------------------------

        private static void Add<T>(List<MonoBehaviour> into) where T : MonoBehaviour
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<T>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    var o = all[i];
                    if (o != null) into.Add(o);
                }
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe: scan " + typeof(T).Name + " FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void Census(GameObject player)
        {
            var p = player.transform.position;
            LoggerUtil.Log($"COOP probe: census from player {p.x:F1},{p.y:F1},{p.z:F1}");

            ScanDoors();
            DumpSeed();
            DumpInventory();
            DumpItems();
            DumpItemWorld();

            LoggerUtil.Log($"COOP probe: door components total={_doors.Count}");
            int shown = 0;
            foreach (var d in _doors)
            {
                if (shown++ >= 60) break;
                LoggerUtil.Log("  door " + Describe(d) + " path=" + PathOf(d.transform));
            }

            var counts = new StringBuilder();
            var byType = new Dictionary<string, int>();
            foreach (var d in _doors)
            {
                var n = d.GetType().Name;
                byType.TryGetValue(n, out int c);
                byType[n] = c + 1;
            }
            var keys = new List<string>(byType.Keys);
            keys.Sort();
            foreach (var k in keys) counts.Append(k).Append('=').Append(byType[k]).Append(' ');
            LoggerUtil.Log("COOP probe: door type counts: " + counts);
            SurveyDoors();
            LoggerUtil.Log("COOP probe: census done");
        }

        private void ScanDoors()
        {
            _doors.Clear();
            // Every door-ish script the metadata dump lists, except pure triggers.
            Add<OpenCloseDoors>(_doors);
            Add<OpenCloseDoorV>(_doors);
            Add<OpenCloseDoorVind>(_doors);
            Add<OpenCloseGarde1>(_doors);
            Add<OpenCloseKitchenDoor1>(_doors);
            Add<OpenCloseKitchenDoor2>(_doors);
            Add<OpenCloseKyl>(_doors);
            Add<OpenCloseLeftOldDoor>(_doors);
            Add<OpenCloseLoda1>(_doors);
            Add<OpenCloseLoda2>(_doors);
            Add<openCloseMotorhuv>(_doors);
            Add<OpenCloseOldDoor>(_doors);
            Add<OpenCloseSmallDoors>(_doors);
            Add<openCloseVitrindoorL>(_doors);
            Add<openCloseVitrindoorR>(_doors);
            Add<openCloseVitrindoorVRL>(_doors);
            Add<openCloseVitrindoorVRR>(_doors);
            Add<OpenCloseBackdoor>(_doors);
            Add<openCloseBaklucka>(_doors);
            Add<doorSlide>(_doors);
            Add<prisonDoorOpenClose>(_doors);
            Add<closePrisonDoorSimple>(_doors);
            Add<grannyClosePrisondoor>(_doors);
            Add<destroyCarDoor>(_doors);
            Add<SkjutplattaDoor>(_doors);
            Add<seeDoors>(_doors);
            Add<openDoors>(_doors);
            Add<SeeDooropener>(_doors);
            Add<pressOpenDoor>(_doors);
            Add<checkIronDoors>(_doors);
            Add<CheckExitDoor>(_doors);
        }

        private static string Describe(MonoBehaviour d)
        {
            var sb = new StringBuilder();
            sb.Append(d.GetType().Name).Append(" active=").Append(SafeActive(d));
            try
            {
                var p = d.transform.position;
                sb.Append(" pos=").Append(p.x.ToString("F1")).Append(',').Append(p.y.ToString("F1")).Append(',').Append(p.z.ToString("F1"));
            }
            catch { sb.Append(" pos=?"); }
            sb.Append(" DoorOpen=").Append(Prop(d, "DoorOpen"))
              .Append(" DoorMoving=").Append(Prop(d, "DoorMoving"))
              .Append(" locked=").Append(Prop(d, "doorLocked"));
            return sb.ToString();
        }

        // ---- day seeding ----------------------------------------------------

        private void DumpSeed()
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var s in FindAll<startNewDay>())
                {
                    sb.Append("\n  startNewDay daysCounter=").Append(Prop(s, "daysCounter"))
                      .Append(" RandomGrannyPos=").Append(Prop(s, "RandomGrannyPos"))
                      .Append(" RandomNR=").Append(Prop(s, "RandomNR"))
                      .Append(" RandomPlaceObjects=").Append(Prop(s, "RandomPlaceObjects"))
                      .Append(" RandomNRTavelbitar=").Append(Prop(s, "RandomNRTavelbitar"))
                      .Append(" @ ").Append(PathOf(s.transform));
                }

                foreach (var k in FindAll<RandomKeyPlace>())
                {
                    sb.Append("\n  RandomKeyPlace number=").Append(Prop(k, "number"));
                    AppendTransformProp(sb, k, "keyPlace1");
                    AppendTransformProp(sb, k, "keyPlace2");
                    AppendTransformProp(sb, k, "keyPlace3");
                }

                foreach (var k in FindAll<RandomKofotPlace>())
                {
                    sb.Append("\n  RandomKofotPlace number=").Append(Prop(k, "number"));
                    AppendTransformProp(sb, k, "kofotPlace1");
                    AppendTransformProp(sb, k, "kofotPlace2");
                    AppendTransformProp(sb, k, "kofotPlace3");
                }
            }
            catch (Exception e)
            {
                sb.Append(" FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            LoggerUtil.Log("COOP probe: day seeding" + sb);
        }

        private static void AppendTransformProp(StringBuilder sb, object o, string name)
        {
            try
            {
                var p = o.GetType().GetProperty(name);
                var t = p?.GetValue(o) as Transform;
                if (t == null) { sb.Append(' ').Append(name).Append("=-"); return; }
                var v = t.position;
                sb.Append(' ').Append(name).Append('=').Append(v.x.ToString("F1")).Append(',')
                  .Append(v.y.ToString("F1")).Append(',').Append(v.z.ToString("F1"));
            }
            catch { sb.Append(' ').Append(name).Append("=?"); }
        }

        // ---- inventory and items -------------------------------------------

        private void DumpInventory()
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var inv in FindAll<InventoryController>())
                {
                    int have = 0;
                    var names = new StringBuilder();
                    foreach (var prop in inv.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!prop.Name.StartsWith("have", StringComparison.OrdinalIgnoreCase)) continue;
                        string v;
                        if (!TryValue(inv, prop, out v)) continue;
                        if (v == "True") { have++; names.Append(' ').Append(prop.Name); }
                    }
                    sb.Append("\n  InventoryController haveTrue=").Append(have)
                      .Append(" @ ").Append(PathOf(inv.transform)).Append(':').Append(names);
                }
            }
            catch (Exception e)
            {
                sb.Append(" FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            LoggerUtil.Log("COOP probe: inventory" + sb);
        }

        private void DumpItems()
        {
            var sb = new StringBuilder();
            try
            {
                int n = 0;
                foreach (var pu in FindAll<PickUp>())
                {
                    sb.Append("\n  PickUp @ ").Append(PathOf(pu.transform));
                    foreach (var prop in pu.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!prop.Name.StartsWith("new", StringComparison.Ordinal)) continue;
                        var t = SafeGet(prop, pu) as Transform;
                        if (t == null) continue;
                        if (n++ >= 70) break;
                        var v = t.position;
                        sb.Append("\n    ").Append(prop.Name).Append(' ').Append(t.name)
                          .Append(" activeSelf=").Append(t.gameObject.activeSelf ? "y" : "n")
                          .Append(" pos=").Append(v.x.ToString("F1")).Append(',').Append(v.y.ToString("F1")).Append(',').Append(v.z.ToString("F1"));
                    }
                }
                sb.Append("\n  item transforms logged=").Append(n);
            }
            catch (Exception e)
            {
                sb.Append(" FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            LoggerUtil.Log("COOP probe: items" + sb);
        }

        // The have-flags alone never said which scene object the player actually
        // carried and dropped. Log the drop plumbing (private dropObject /
        // objectRB), then every item transform with its instance id and the
        // other scene objects that share its name, so InventoryController.newX
        // and PickUp.newX can be compared at runtime.
        private void DumpItemWorld()
        {
            var byName = new Dictionary<string, List<Transform>>();
            int total = 0;
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<Transform>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                total = all.Length;
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    List<Transform> list;
                    if (!byName.TryGetValue(t.name, out list))
                    {
                        list = new List<Transform>(4);
                        byName[t.name] = list;
                    }
                    if (list.Count < 5) list.Add(t);
                }
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe: itemworld scan FAILED: " + e.GetType().Name + ": " + e.Message);
            }
            LoggerUtil.Log("COOP probe: itemworld transforms=" + total);

            var invRefs = new Dictionary<string, Transform>();
            foreach (var inv in FindAll<InventoryController>())
            {
                var sb = new StringBuilder();
                sb.Append("\n  inv @ ").Append(PathOf(inv.transform));
                AppendPart(sb, inv, "dropObject", true);
                AppendPart(sb, inv, "objectRB", true);
                AppendPart(sb, inv, "dropPoint", true);
                sb.Append(" dropForce=").Append(Prop(inv, "dropForce"));
                LoggerUtil.Log("COOP probe: drop plumbing" + sb);

                foreach (var prop in inv.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.Name.StartsWith("new", StringComparison.Ordinal)) continue;
                    var t = SafeGet(prop, inv) as Transform;
                    if (t != null) invRefs[prop.Name] = t;
                }
            }

            foreach (var pu in FindAll<PickUp>())
            {
                var sb = new StringBuilder();
                sb.Append("\n  PickUp @ ").Append(PathOf(pu.transform));
                foreach (var prop in pu.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.Name.StartsWith("new", StringComparison.Ordinal)) continue;
                    var t = SafeGet(prop, pu) as Transform;
                    if (t == null) continue;
                    AppendItem(sb, prop.Name, t, byName);

                    Transform other;
                    if (invRefs.TryGetValue(prop.Name, out other) && other != null)
                        sb.Append(other.GetInstanceID() == t.GetInstanceID()
                            ? " sameAsInv=y"
                            : " sameAsInv=n invId=" + other.GetInstanceID());
                }
                LoggerUtil.Log("COOP probe: pickup refs" + sb);
            }
        }

        private static void AppendPart(StringBuilder sb, object o, string name, bool isTransform)
        {
            try
            {
                var p = o.GetType().GetProperty(name);
                var v = p != null ? p.GetValue(o) : null;
                if (v == null) { sb.Append(' ').Append(name).Append("=-"); return; }
                var t = v as Transform;
                if (t != null)
                {
                    var q = t.position;
                    sb.Append(' ').Append(name).Append('=').Append(t.name)
                      .Append(" id=").Append(t.GetInstanceID())
                      .Append(" activeSelf=").Append(t.gameObject.activeSelf ? "y" : "n")
                      .Append(" pos=").Append(q.x.ToString("F1")).Append(',').Append(q.y.ToString("F1")).Append(',').Append(q.z.ToString("F1"))
                      .Append(" path=").Append(PathOf(t));
                    return;
                }
                sb.Append(' ').Append(name).Append('=').Append(v.GetType().Name).Append(':').Append(v);
            }
            catch { sb.Append(' ').Append(name).Append("=?"); }
        }

        private static void AppendItem(StringBuilder sb, string prop, Transform t, Dictionary<string, List<Transform>> byName)
        {
            var p = t.position;
            sb.Append("\n    ").Append(prop).Append(' ').Append(t.name)
              .Append(" id=").Append(t.GetInstanceID())
              .Append(" activeSelf=").Append(t.gameObject.activeSelf ? "y" : "n")
              .Append(" pos=").Append(p.x.ToString("F1")).Append(',').Append(p.y.ToString("F1")).Append(',').Append(p.z.ToString("F1"))
              .Append(" path=").Append(PathOf(t));
            List<Transform> dups;
            if (!byName.TryGetValue(t.name, out dups) || dups.Count <= 1) return;
            sb.Append(" dups=").Append(dups.Count);
            for (int i = 0; i < dups.Count; i++)
            {
                var d = dups[i];
                var q = d.position;
                sb.Append("\n      ").Append(i == 0 ? "*" : "-")
                  .Append(d.name).Append(" id=").Append(d.GetInstanceID())
                  .Append(" activeSelf=").Append(d.gameObject.activeSelf ? "y" : "n")
                  .Append(" pos=").Append(q.x.ToString("F1")).Append(',').Append(q.y.ToString("F1")).Append(',').Append(q.z.ToString("F1"))
                  .Append(" path=").Append(PathOf(d));
            }
        }

        // ---- one-door test --------------------------------------------------

        private void ArmDoor(GameObject player)
        {
            var origin = player.transform.position;
            float best = 40f;
            float bestAnimated = 40f;
            MonoBehaviour animated = null;

            foreach (var d in _doors)
            {
                var p = d.GetType().GetProperty("DoorOpen");
                if (p == null || !p.CanWrite) continue;
                float dist = Vector3.Distance(origin, d.transform.position);
                if (dist < best) { best = dist; _door = d; }

                // Prefer a plain interior door: OpenCloseDoors is the most common
                // animating type (9 instances) and has both DoorMoving and Innerdoor.
                if (string.Equals(d.GetType().Name, "OpenCloseDoors", StringComparison.Ordinal) &&
                    dist < bestAnimated)
                {
                    bestAnimated = dist;
                    animated = d;
                }
            }

            if (animated != null) { _door = animated; best = bestAnimated; }

            if (_door == null)
            {
                LoggerUtil.Log("COOP probe: no writable DoorOpen door within 40 m");
                _stage = 7;
                return;
            }

            _part = null;
            try
            {
                var inner = _door.GetType().GetProperty("Innerdoor");
                if (inner != null) _part = inner.GetValue(_door) as Transform;
            }
            catch { }
            if (_part == null) _part = _door.transform;
            _baseRot = _part.localEulerAngles;
            _basePos = _part.localPosition;

            LoggerUtil.Log($"COOP probe: test door '{_door.GetType().Name}' dist={best:F1} " +
                           $"path={PathOf(_door.transform)} part='{_part.name}'");
            DumpParts("rest");
            CollectClips();
            DumpFields();
            DumpParent("rest");
            DumpAnimators();
        }

        /// <summary>
        /// The game opens a door from its own OnTriggerEnter(Collider), so the
        /// honest test is to deliver that call with the local player's collider
        /// rather than inventing a transform animation. If this works the co-op
        /// layer can "use" a door exactly the way the player does.
        /// </summary>
        private void TryTrigger(GameObject player)
        {
            try
            {
                var m = _door.GetType().GetMethod("OnTriggerEnter",
                                                  BindingFlags.Public | BindingFlags.Instance);
                if (m == null) { LoggerUtil.Log("COOP probe: no OnTriggerEnter method"); return; }

                Collider col = null;
                try { col = player.GetComponentInChildren<Collider>(); } catch { }
                if (col == null) { LoggerUtil.Log("COOP probe: player has no Collider"); return; }

                LoggerUtil.Log($"COOP probe: OnTriggerEnter with '{PathOf(col.transform)}' " +
                               $"tag={col.gameObject.tag} enabled={col.enabled} isTrigger={col.isTrigger}");
                m.Invoke(_door, new object[] { col });
                LoggerUtil.Log("COOP probe: OnTriggerEnter returned");
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                LoggerUtil.Log("COOP probe: OnTriggerEnter FAILED: " +
                               inner.GetType().Name + ": " + inner.Message);
            }
        }

        /// <summary>The clip names on the door's legacy Animation component. The
        /// game probably plays one of these by name when a door opens.</summary>
        /// <summary>One line per door script: type, tag, DoorOpen and the
        /// clips available on the Animation component that drives it. Used to
        /// pick a generic open/close clip per door type for replication.</summary>
        private void SurveyDoors()
        {
            LoggerUtil.Log("COOP probe: door survey begin");
            foreach (var d in _doors)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.Append("door-survey i=").Append(_doors.IndexOf(d))
                      .Append(' ').Append(d.GetType().Name)
                      .Append(" path=").Append(PathOf(d.transform))
                      .Append(" active=").Append(SafeActive(d))
                      .Append(" DoorOpen=").Append(Prop(d, "DoorOpen"))
                      .Append(" tag=").Append(SafeTag(d.gameObject))
                      .Append(' ').Append(AnimClips(d.gameObject));
                    LoggerUtil.Log(sb.ToString());
                }
                catch (Exception e)
                {
                    LoggerUtil.Log("COOP probe: door-survey FAILED: " + e.GetType().Name + ": " + e.Message);
                }
            }
            LoggerUtil.Log("COOP probe: door survey end");
        }

        private static string SafeTag(GameObject go)
        {
            try { return go.tag; }
            catch (Exception e) { return "!" + e.GetType().Name; }
        }

        private static string AnimClips(GameObject go)
        {
            var sb = new StringBuilder();
            Animation a = null;
            GameObject at = null;
            try { a = go.GetComponent<Animation>(); } catch (Exception e) { sb.Append("animErr=").Append(e.GetType().Name); }
            if (a != null) at = go;

            if (a == null)
            {
                try
                {
                    var kids = go.GetComponentsInChildren<Animation>(true);
                    if (kids != null)
                        for (int i = 0; i < kids.Length; i++)
                            if (kids[i] != null) { a = kids[i]; at = a.gameObject; break; }
                }
                catch (Exception e) { sb.Append(" animChildErr=").Append(e.GetType().Name); }
            }

            if (a == null) { sb.Append(" anim=-"); return sb.ToString(); }

            sb.Append(" animAt=").Append(at == go ? "." : PathOf(at.transform));
            try { sb.Append(" enabled=").Append(a.enabled ? "y" : "n"); }
            catch (Exception e) { sb.Append(" enabled=!").Append(e.GetType().Name); }
            sb.Append(" clips=[");
            try
            {
                var en = a.GetEnumerator();
                int n = 0;
                while (en.MoveNext() && n < 12)
                {
                    var st = (en.Current as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<AnimationState>();
                    if (st != null)
                    {
                        sb.Append(st.name).Append('(').Append(st.length.ToString("F2")).Append("s) ");
                        n++;
                    }
                }
            }
            catch (Exception e) { sb.Append('!').Append(e.GetType().Name); }
            sb.Append(']');
            return sb.ToString();
        }

        private void CollectClips()
        {
            _clips.Clear();
            try
            {
                var a = _door.GetComponent<Animation>();
                if (a == null) { LoggerUtil.Log("COOP probe: door has no Animation component"); return; }

                var en = a.GetEnumerator();
                while (en.MoveNext())
                {
                    var name = ReadName(en.Current);
                    if (!string.IsNullOrEmpty(name)) _clips.Add(name);
                    if (_clips.Count > 32) break;
                }
                LoggerUtil.Log("COOP probe: door clips: " +
                               (_clips.Count == 0 ? "(none)" : string.Join(", ", _clips)));
                LoggerUtil.Log("COOP probe: Animation api: " + ApiOf(typeof(Animation)));
                try { LoggerUtil.Log("COOP probe: clip count=" + a.GetClipCount()); }
                catch (Exception e6) { LoggerUtil.Log("COOP probe: GetClipCount FAILED: " + e6.GetType().Name); }
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe: clip collection FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Play the door's first clip directly: is the Animation
        /// component the thing that actually moves a door mesh?</summary>
        private void TryPlayClip()
        {
            try
            {
                var a = _door.GetComponent<Animation>();
                if (a == null) { LoggerUtil.Log("COOP probe: no Animation to play"); return; }

                // The default clip is readable even when the state names are not.
                string raw = null;
                try { if (a.clip != null) raw = a.clip.name; } catch { }
                if (string.IsNullOrEmpty(raw) && _clips.Count > 0)
                {
                    // Clip names carry the ReadName decorations; strip them back off.
                    raw = _clips[0].TrimEnd('*');
                    int at = raw.IndexOf('@');
                    if (at > 0) raw = raw.Substring(0, at);
                }
                if (string.IsNullOrEmpty(raw)) { LoggerUtil.Log("COOP probe: no clips to play"); return; }

                LoggerUtil.Log($"COOP probe: Animation.Play('{raw}')");
                a.Play(raw);
                LoggerUtil.Log("COOP probe: Play returned isPlaying=" + (a.isPlaying ? "y" : "n"));

                // Decisive test: force the clip's own curves onto this object at its
                // last frame. If the transform still does not move, the clip holds
                // no transform curves for this hierarchy and the visible motion (if
                // any) comes from somewhere else.
                try
                {
                    var c = a.clip;
                    if (c != null)
                    {
                        float end = Mathf.Max(0f, c.length - 0.01f);
                        LoggerUtil.Log($"COOP probe: clip '{c.name}' empty={c.empty} len={c.length:F2}s " +
                                       $"fps={c.frameRate:F1} legacy={c.legacy} wrap={c.wrapMode}");
                        c.SampleAnimation(_door.gameObject, end);
                        LoggerUtil.Log($"COOP probe: sampled clip at {end:F2}s");
                        LogDoor("after-sample");
                    }
                }
                catch (Exception e2)
                {
                    LoggerUtil.Log("COOP probe: SampleAnimation FAILED: " + e2.GetType().Name + ": " + e2.Message);
                }
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe: Animation.Play FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void SetDoorOpenOnly()
        {
            try
            {
                _door.GetType().GetProperty("DoorOpen").SetValue(_door, true);
                LoggerUtil.Log("COOP probe: wrote DoorOpen=true (alone)");
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe: DoorOpen write FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Play a named legacy clip and force its state on. Two suspects for the
        /// earlier no-op: the state's weight stays 0, or the Animation component
        /// is culled because the door is off-screen (default culling is
        /// BasedOnRenderers), so AlwaysAnimate is set for the test.
        /// </summary>
        private void PlayNamed(string clipName)
        {
            try
            {
                var a = _door.GetComponent<Animation>();
                if (a == null) { LoggerUtil.Log("COOP probe: no Animation component"); return; }

                var mr = _door.GetComponent<MeshRenderer>();
                LoggerUtil.Log($"COOP probe: anim culling={a.cullingType} updateMode={a.updateMode} " +
                               $"animatePhysics={a.animatePhysics} rendererVisible={(mr != null && mr.isVisible ? "y" : "n")}");

                var st = a[clipName];
                LoggerUtil.Log("COOP probe: state before: " + StateDesc(st));

                a.cullingType = AnimationCullingType.AlwaysAnimate;
                st.enabled = true;
                st.weight = 1f;
                st.time = 0f;
                st.speed = 1f;
                st.wrapMode = WrapMode.Clamp;

                a.Play(clipName);
                LoggerUtil.Log("COOP probe: Play('" + clipName + "') + weight=1 -> " + StateDesc(a[clipName]));
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe: PlayNamed FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Snap back to the closed pose with the closed clip's own curves:
        /// the inverse of opening, and a check that both clips act on this door.</summary>
        private void RestoreClosed()
        {
            try
            {
                var a = _door.GetComponent<Animation>();
                if (a == null) { LoggerUtil.Log("COOP probe: no Animation to close"); return; }
                var c = a["InnerdoorClose"].clip;
                if (c == null) { LoggerUtil.Log("COOP probe: no InnerdoorClose clip"); return; }
                float end = Mathf.Max(0f, c.length - 0.01f);
                LoggerUtil.Log($"COOP probe: closing via '{a["InnerdoorClose"].name}' len={c.length:F2}s");
                c.SampleAnimation(_door.gameObject, end);
                LogDoor("after-close-clip");
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe: RestoreClosed FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string StateDesc(AnimationState st)
        {
            if (st == null) return "-";
            try
            {
                return $"'{st.name}' enabled={st.enabled} weight={st.weight:F2} time={st.time:F2}/{st.length:F2} " +
                       $"speed={st.speed:F2} wrap={st.wrapMode}";
            }
            catch (Exception e) { return "!" + e.GetType().Name; }
        }


        private void SetMovingFlag(string name)
        {
            try
            {
                var p = _door.GetType().GetProperty(name);
                if (p == null || !p.CanWrite) return;
                p.SetValue(_door, true);
                LoggerUtil.Log("COOP probe: wrote " + name + "=true");
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe: " + name + " write FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void StartTimerCoroutine()
        {
            try
            {
                var m = _door.GetType().GetMethod("timerDooropen", BindingFlags.Public | BindingFlags.Instance);
                if (m == null) { LoggerUtil.Log("COOP probe: no timerDooropen method"); return; }

                var raw = m.Invoke(_door, null);
                LoggerUtil.Log("COOP probe: timerDooropen returned " +
                               (raw == null ? "null" : raw.GetType().FullName));

                // The game returns Il2CppSystem.Collections.IEnumerator, not the
                // managed System.Collections.IEnumerator, so MelonCoroutines.Start
                // cannot take it. Start it on the door's own MonoBehaviour instead:
                // the game's coroutine then runs with the object that owns it.
                var e = raw as Il2CppSystem.Collections.IEnumerator;
                if (e == null) { LoggerUtil.Log("COOP probe: coroutine is not an Il2Cpp IEnumerator"); return; }

                _coroutine = e;
                _door.StartCoroutine(e);
                LoggerUtil.Log("COOP probe: started timerDooropen on the door object");
            }
            catch (Exception ex)
            {
                LoggerUtil.Log("COOP probe: timerDooropen FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool Moved()
        {
            if (_part == null) return false;
            var r = _part.localEulerAngles;
            if (Mathf.Abs(Mathf.DeltaAngle(_baseRot.x, r.x)) > 1f ||
                Mathf.Abs(Mathf.DeltaAngle(_baseRot.y, r.y)) > 1f ||
                Mathf.Abs(Mathf.DeltaAngle(_baseRot.z, r.z)) > 1f) return true;
            // Drawers and sliding doors move in position, not rotation.
            return (_part.localPosition - _basePos).magnitude > 0.01f;
        }

        private void LogDoor(string tag)
        {
            try
            {
                var r = _part != null ? _part.localEulerAngles : Vector3.zero;
                var lp = _part != null ? _part.localPosition : Vector3.zero;
                var d = _part != null ? (_part.localPosition - _basePos).magnitude : 0f;
                var wp = _part != null ? _part.position : Vector3.zero;
                var we = _part != null ? _part.eulerAngles : Vector3.zero;
                LoggerUtil.Log($"COOP probe door {tag}: open={Prop(_door, "DoorOpen")} " +
                               $"moving={Prop(_door, "DoorMoving")} loda={Prop(_door, "LodaMoving")} " +
                               $"locked={Prop(_door, "doorLocked")} " +
                               $"part={_part?.name} rot={r.x:F1},{r.y:F1},{r.z:F1} " +
                               $"wrot={we.x:F1},{we.y:F1},{we.z:F1} " +
                               $"lpos={lp.x:F3},{lp.y:F3},{lp.z:F3} dpos={d:F3} " +
                               $"wpos={wp.x:F2},{wp.y:F2},{wp.z:F2} moved={Moved()} " +
                               "| " + BlockState() + " | " + AnimDesc(_door.gameObject));
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe door log FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>What the game may change instead of (or as well as) the
        /// transform: the tag switches between innerdoorClosed and an open tag,
        /// and the NavMeshObstacle/collider decide whether anything can pass.
        /// Logged on every door sample so a state-only "open" is visible.</summary>
        private string BlockState()
        {
            var sb = new StringBuilder();
            try { sb.Append("tag=").Append(_door.gameObject.tag); } catch { sb.Append("tag=!"); }
            try
            {
                var c = _door.GetComponent<BoxCollider>();
                sb.Append(" box=").Append(c == null ? "-" : (c.enabled ? "on" : "off"));
            }
            catch { sb.Append(" box=!"); }
            try
            {
                var mr = _door.GetComponent<MeshRenderer>();
                sb.Append(" mr=").Append(mr == null ? "-" : (mr.enabled ? "on" : "off"));
            }
            catch { sb.Append(" mr=!"); }
            try
            {
                var o = _door.GetComponent<NavMeshObstacle>();
                sb.Append(" obs=").Append(o == null ? "-" : (o.enabled ? "on" : "off"));
            }
            catch { sb.Append(" obs=!"); }
            return sb.ToString();
        }

        /// <summary>Public method names of a stub type, so an API can be used
        /// without guessing (e.g. Animation.GetClipCount, AnimationClip.SampleAnimation).</summary>
        private static string ApiOf(Type t)
        {
            var sb = new StringBuilder();
            try
            {
                var seen = new HashSet<string>();
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (!seen.Add(m.Name)) continue;
                    if (sb.Length + m.Name.Length > 400) { sb.Append(" ..."); break; }
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(m.Name);
                }
            }
            catch (Exception e) { sb.Append("!").Append(e.GetType().Name); }
            return sb.ToString();
        }

        /// <summary>
        /// Every public property of the door script with its current value: this
        /// exposes the blocking/moving flags (doorStop, DoorBlocked, doorDead, …)
        /// without needing a decompilation.
        /// </summary>
        private void DumpFields()
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var p in _door.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.GetIndexParameters().Length > 0) continue;
                    string v;
                    if (!TryValue(_door, p, out v)) v = "!";
                    sb.Append("\n  ").Append(p.Name).Append('=').Append(v);
                }
            }
            catch (Exception e)
            {
                sb.Append(" FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            LoggerUtil.Log("COOP probe fields:" + sb);
        }

        /// <summary>The door's parent subtree — the leaf may live on a sibling.</summary>
        private void DumpParent(string tag)
        {
            var sb = new StringBuilder();
            try
            {
                var start = _door.transform.parent;
                if (start == null) start = _door.transform;
                int n = 0;
                Walk(start, sb, tag, ref n, 0);
                sb.Append("\n  parent parts logged=").Append(n);
            }
            catch (Exception e)
            {
                sb.Append(" FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            LoggerUtil.Log("COOP probe parent " + tag + ":" + sb);
        }

        private void DumpAnimators()
        {
            var sb = new StringBuilder();
            try
            {
                var go = _door.gameObject;
                sb.Append(" children=").Append(go.transform.childCount);
                var p = go.transform.parent;
                sb.Append(" parent=").Append(p == null ? "-" : p.name);

                sb.Append("\n  components on door:");
                foreach (var c in go.GetComponents<Component>())
                    sb.Append(' ').Append(c == null ? "null" : c.GetType().Name);
                sb.Append("\n  typed on door: ").Append(TypedDesc(go));
                sb.Append("\n  typed on parent: ").Append(p == null ? "-" : TypedDesc(p.gameObject));

                sb.Append("\n  animator door: ").Append(AnimDesc(go));
                if (p != null)
                {
                    sb.Append("\n  components on parent '").Append(p.name).Append("':");
                    foreach (var c in p.gameObject.GetComponents<Component>())
                        sb.Append(' ').Append(c == null ? "null" : c.GetType().Name);
                    sb.Append("\n  animator parent: ").Append(AnimDesc(p.gameObject));
                }
                for (int i = 0; i < go.transform.childCount && i < 10; i++)
                {
                    var ch = go.transform.GetChild(i);
                    sb.Append("\n  child '").Append(ch.name).Append("' ").Append(AnimDesc(ch.gameObject));
                }
            }
            catch (Exception e)
            {
                sb.Append(" FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            LoggerUtil.Log("COOP probe components:" + sb);
        }

        /// <summary>
        /// One-line description of the animation components on a GameObject: the
        /// Animator's controller, its parameters and current clip, plus any legacy
        /// Animation clips. These are how a scripted door may actually move.
        /// </summary>
        private static string AnimDesc(GameObject go)
        {
            var sb = new StringBuilder();
            try
            {
                var anim = go.GetComponent<Animator>();
                if (anim == null) sb.Append("Animator=no");
                else
                {
                    sb.Append("Animator=yes enabled=").Append(anim.enabled);
                    sb.Append(" ctrl=").Append(anim.runtimeAnimatorController == null ? "null" : "set");
                    if (anim.runtimeAnimatorController != null)
                    {
                        // anim.parameters is Il2CppReferenceArray<AnimatorControllerParameter>;
                        // touching it fails JIT with TypeLoadException, so it lives in
                        // its own method that can fail without losing the rest.
                        try { sb.Append(" params=").Append(ParamDesc(anim)); }
                        catch (Exception e3) { sb.Append(" params=!").Append(e3.GetType().Name); }
                    }
                    try
                    {
                        var ci = anim.GetCurrentAnimatorClipInfo(0);
                        sb.Append(" clip=");
                        if (ci == null || ci.Length == 0) sb.Append("-");
                        else sb.Append(ci[0].clip == null ? "null" : ci[0].clip.name);
                        sb.Append(" speed=").Append(anim.speed.ToString("F2"));
                    }
                    catch (Exception e2) { sb.Append(" state=!").Append(e2.GetType().Name); }
                }

                var legacy = go.GetComponent<Animation>();
                if (legacy != null)
                {
                    sb.Append(" Animation=yes enabled=").Append(legacy.enabled ? "y" : "n");
                    sb.Append(" autoplay=").Append(legacy.playAutomatically);
                    sb.Append(" isPlaying=").Append(legacy.isPlaying ? "y" : "n");
                    try
                    {
                        var c = legacy.clip;
                        sb.Append(" clip=").Append(c == null ? "-" : c.name);
                        if (c != null)
                            sb.Append("(").Append(c.empty ? "empty" : c.length.ToString("F2") + "s").Append(')');
                    }
                    catch (Exception e4) { sb.Append(" clip=!").Append(e4.GetType().Name); }

                    // foreach over Animation yields Il2CppSystem.Object, so the
                    // AnimationState cast fails; read each entry by reflection.
                    sb.Append(" states=[");
                    try
                    {
                        var en = legacy.GetEnumerator();
                        int n = 0;
                        while (en.MoveNext())
                        {
                            if (n > 0) sb.Append(',');
                            var cur = en.Current;
                            sb.Append(ReadName(cur));
                            n++;
                            if (n > 16) { sb.Append(",..."); break; }
                        }
                        if (n == 0) sb.Append("-");
                    }
                    catch (Exception e5) { sb.Append("!").Append(e5.GetType().Name); }
                    sb.Append(']');
                }
            }
            catch (Exception e)
            {
                sb.Append(" FAILED:").Append(e.GetType().Name);
            }
            return sb.ToString();
        }
        /// <summary>Animator parameters, isolated so its TypeLoadException cannot
        /// take out the rest of AnimDesc. Uses the integer count + indexer, which
        /// do not build an Il2CppReferenceArray of the parameter struct.</summary>
        /// <summary>Name of an entry yielded by Animation's enumerator. The
        /// entries arrive as Il2CppSystem.Object, so read the property by
        /// reflection instead of casting to AnimationState.</summary>
        private static string ReadName(object cur)
        {
            if (cur == null) return "null";
            try
            {
                // The enumerator yields Il2CppSystem.Object proxies; TryCast maps
                // them back onto the concrete AnimationState.
                if (cur is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b)
                {
                    var st = b.TryCast<AnimationState>();
                    if (st != null)
                        return st.name + (st.enabled ? "*" : "") + "@" + st.weight.ToString("F2") +
                               " t=" + st.time.ToString("F2") + "/" + st.length.ToString("F2");
                }
            }
            catch { }

            try
            {
                var t = cur.GetType();
                var p = t.GetProperty("name");
                var name = p != null && p.CanRead ? p.GetValue(cur) as string : null;
                if (name == null) name = "?" + t.Name;

                var e = t.GetProperty("enabled");
                if (e != null && e.CanRead && e.GetValue(cur) is bool b2 && b2) name += "*";

                var w = t.GetProperty("weight");
                if (w != null && w.CanRead && w.GetValue(cur) is float f) name += "@" + f.ToString("F2");
                return name;
            }
            catch (Exception ex) { return "!" + ex.GetType().Name; }
        }

        /// <summary>Typed GetComponent probes: the untyped list prints several
        /// engine components as "Component", so ask for each candidate by type.</summary>
        private static string TypedDesc(GameObject go)
        {
            var sb = new StringBuilder();
            sb.Append("Animator=").Append(Has(go, o => o.GetComponent<Animator>() != null));
            sb.Append(" Animation=").Append(Has(go, o => o.GetComponent<Animation>() != null));
            sb.Append(" MeshFilter=").Append(Has(go, o => o.GetComponent<MeshFilter>() != null));
            sb.Append(" MeshRenderer=").Append(Has(go, o => o.GetComponent<MeshRenderer>() != null));
            sb.Append(" BoxCollider=").Append(Has(go, o => o.GetComponent<BoxCollider>() != null));
            sb.Append(" MeshCollider=").Append(Has(go, o => o.GetComponent<MeshCollider>() != null));
            sb.Append(" CharacterController=").Append(Has(go, o => o.GetComponent<CharacterController>() != null));
            sb.Append(" Rigidbody=").Append(Has(go, o => o.GetComponent<Rigidbody>() != null));
            sb.Append(" NavMeshObstacle=").Append(Has(go, o => o.GetComponent<NavMeshObstacle>() != null));
            return sb.ToString();
        }

        private static string Has(GameObject go, Func<GameObject, bool> test)
        {
            try { return test(go) ? "y" : "n"; }
            catch (Exception e) { return "!" + e.GetType().Name; }
        }

        private static string ParamDesc(Animator anim)
        {
            var sb = new StringBuilder("[");
            int n = anim.parameterCount;
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(',');
                string name;
                try { name = anim.GetParameter(i).name; }
                catch { sb.Append('?'); continue; }
                sb.Append(name).Append('=');
                try { sb.Append(anim.GetBool(name)); } catch { sb.Append('?'); }
            }
            sb.Append(']');
            return sb.ToString();
        }

        private void DumpParts(string tag)
        {
            var sb = new StringBuilder();
            try
            {
                int n = 0;
                Walk(_door.transform, sb, tag, ref n, 0);
                sb.Append("\n  parts logged=").Append(n);
            }
            catch (Exception e)
            {
                sb.Append(" FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            LoggerUtil.Log("COOP probe parts " + tag + ":" + sb);
        }

        private static void Walk(Transform t, StringBuilder sb, string tag, ref int n, int depth)
        {
            if (n >= 24 || depth > 3) return;
            if (depth > 0) sb.Append("\n  ");
            var lp = t.localPosition;
            var le = t.localEulerAngles;
            var wp = t.position;
            var we = t.eulerAngles;
            sb.Append("<").Append(tag).Append("> ").Append(t.name)
              .Append(" lpos=").Append(lp.x.ToString("F3")).Append(',').Append(lp.y.ToString("F3")).Append(',').Append(lp.z.ToString("F3"))
              .Append(" lrot=").Append(le.x.ToString("F1")).Append(',').Append(le.y.ToString("F1")).Append(',').Append(le.z.ToString("F1"))
              .Append(" wpos=").Append(wp.x.ToString("F2")).Append(',').Append(wp.y.ToString("F2")).Append(',').Append(wp.z.ToString("F2"))
              .Append(" wrot=").Append(we.x.ToString("F1")).Append(',').Append(we.y.ToString("F1")).Append(',').Append(we.z.ToString("F1"));
            n++;
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), sb, tag, ref n, depth + 1);
        }

        // ---- drop visibility test (--coop-dropvis) --------------------------

        /// <summary>
        /// Drive ONE vase through the game's own path instead of writing a
        /// transform: set havevas, let CheckInventory() show it in the hand,
        /// then press the game's own drop button. Every item transform is
        /// snapshotted before and diffed after each step, and every transform
        /// whose name contains "Vas" is listed with its instance id. This answers
        /// which object the game actually moves/activates on a real drop — the
        /// thing the co-op placement sync must capture and apply.
        /// </summary>
        private void DropVisTick(GameObject player, float dt)
        {
            _dvT += dt;
            if (_dvStage == 0)
            {
                // Wait for connection and mapping, and then keep waiting for a
                // while: the other end maps its items only after its own scene
                // load, which trails this one.
                if (NetConnected && ItemsReady) _dvReadyT += dt;
                else _dvReadyT = 0f;
                if (_dvT > 2f && _dvReadyT >= DvSettle)
                {
                    _dvStage = 1;
                    // The gate can open minutes after startup, so restart the
                    // clock: the stages below are offsets from the begin, not
                    // uptimes. Without this the first tick after the gate ran
                    // every stage at once and the snapshots all described the
                    // same instant.
                    _dvT = 0f;
                    DropVisBegin();
                }
            }
            else if (_dvStage == 1 && _dvT > 3.0f)
            {
                _dvStage = 2;
                DropVisLog("held-0.5s");
                DropVisDrop();
            }
            else if (_dvStage == 2 && _dvT > 4.6f)
            {
                _dvStage = 3;
                DropVisLog("drop+1.6s");
            }
            else if (_dvStage == 3 && _dvT > 6.4f)
            {
                _dvStage = 4;
                DropVisLog("drop+3.4s");
                if (DropVisHeld())
                {
                    LoggerUtil.Log("COOP dropvis: still held, writing PickUp.dropObject=true");
                    DropVisWrite(_dvPick, "dropObject", true);
                    DropVisLog("drop-flag+0.1s");
                }
            }
            else if (_dvStage == 4 && _dvT > 8.0f)
            {
                _dvStage = 5;
                DropVisLog("drop-flag+1.6s");
            }
            else if (_dvStage == 5 && _dvT > 10f)
            {
                _dvStage = 99;
                DropVisLog("final");
                DumpVasWorld("final");
                LoggerUtil.Log("COOP dropvis: done");
            }
        }

        private void DropVisBegin()
        {
            foreach (var inv in FindAll<InventoryController>()) { _dvInv = inv; break; }
            foreach (var pu in FindAll<PickUp>()) { _dvPick = pu; break; }

            if (_dvInv == null)
            {
                LoggerUtil.Log("COOP dropvis: no InventoryController in the scene");
                _dvStage = 99;
                return;
            }

            // Bind first, then snapshot: the previous run snapshotted before
            // _dvInv existed and recorded zero items.
            _dvBefore = DropVisSnapshot();
            LoggerUtil.Log("COOP dropvis: snapshot baseline items=" + (_dvBefore == null ? 0 : _dvBefore.Count));
            DumpVasWorld("before");

            LoggerUtil.Log("COOP dropvis: inv @ " + PathOf(_dvInv.transform) +
                           " havevas=" + Prop(_dvInv, "havevas") +
                           " pickup=" + (_dvPick == null ? "-" : PathOf(_dvPick.transform)));
            DropVisIdent("before");
            DropVisDropPoint();
            DropVisTake();
        }

        /// <summary>The object identity behind the two fields that matter:
        /// <c>vas</c> (GameObject) and <c>newvas</c> (Transform). The drop path
        /// clones <c>vas</c> at the drop point, so this line says whether
        /// <c>newvas</c> is that clone, the scene copy, or a spawn marker.</summary>
        private void DropVisIdent(string tag)
        {
            try
            {
                var vg = SafeProp(_dvInv, "vas") as GameObject;
                var nt = SafeProp(_dvInv, "newvas") as Transform;
                LoggerUtil.Log("COOP dropvis ident " + tag + ": vas=" + DropVisObj(vg) +
                               " newvas=" + DropVisTr(nt) +
                               " same=" + (vg != null && nt != null && ReferenceEquals(vg.transform, nt) ? "y" : "n"));
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP dropvis ident " + tag + " FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string DropVisObj(GameObject go)
        {
            if (go == null) return "null";
            try
            {
                var t = go.transform;
                return "'" + go.name + "' id=" + go.GetInstanceID() +
                       " activeSelf=" + (go.activeSelf ? "y" : "n") +
                       " pos=" + Fmt(t.position) + " path=" + PathOf(t);
            }
            catch { return "!"; }
        }

        private static string DropVisTr(Transform t)
        {
            if (t == null) return "null";
            try
            {
                return "'" + t.name + "' id=" + t.GetInstanceID() +
                       " activeSelf=" + (t.gameObject.activeSelf ? "y" : "n") +
                       " pos=" + Fmt(t.position) + " path=" + PathOf(t);
            }
            catch { return "!"; }
        }

        private bool DropVisHeld()
        {
            try
            {
                var p = _dvInv.GetType().GetProperty("havevas");
                var v = p != null ? p.GetValue(_dvInv) : null;
                return v is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>The live vase in the world: <c>UsefulObjects/Vas</c> for the
        /// original, or a root-level <c>Vas(Clone)</c> left by an earlier drop.
        /// The prefab template (path exactly "Vas") and the hand sockets are
        /// excluded.</summary>
        private Transform FindWorldVas()
        {
            Transform best = null;
            int bestScore = int.MinValue;
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    string n = t.name;
                    if (n != "Vas" && !n.StartsWith("Vas(", StringComparison.Ordinal)) continue;
                    string path = PathOf(t);
                    if (path == "Vas") continue;                        // prefab template
                    if (path.StartsWith("GrannyCoop", StringComparison.Ordinal)) continue;
                    if (path.Contains("HandHoldObjects")) continue;      // hand sockets
                    int score = t.gameObject.activeSelf ? 1000 : 0;
                    if (path == "UsefulObjects/Vas") score += 100;
                    if (n.EndsWith("(Clone)", StringComparison.Ordinal)) score += 10;
                    if (score > bestScore) { bestScore = score; best = t; }
                }
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP dropvis: find world FAILED: " + e.GetType().Name + ": " + e.Message);
            }
            return best;
        }

        /// <summary>Emulate the game's own pickup: hide the world vase, switch
        /// the hand socket on, then let CheckInventory() derive havevas. The
        /// previous test wrote the flag with the hand still off, which the game
        /// read as a DROP: it spawned Vas(Clone) at DropPoint and cleared the
        /// flag again. The hand object must be active for the flag to stick.</summary>
        private void DropVisTake()
        {
            _dvWorld = FindWorldVas();
            LoggerUtil.Log("COOP dropvis: take world=" + DropVisTr(_dvWorld));
            if (_dvWorld != null) _dvWorld.gameObject.SetActive(false);

            var hand = SafeProp(_dvInv, "vas") as GameObject;
            if (hand != null) hand.SetActive(true);
            LoggerUtil.Log("COOP dropvis: take hand(on)=" + DropVisObj(hand));

            bool wrote = DropVisWrite(_dvInv, "havevas", true);
            LoggerUtil.Log("COOP dropvis: take havevas=true ok=" + wrote + " now=" + Prop(_dvInv, "havevas"));
            // No CheckInventory() here: the previous run called it and the game
            // treated the call as a DROP (spawned Vas(Clone) at DropPoint and
            // cleared the flag in the same frame). CheckInventory is the drop
            // handler, not a per-frame derivation; the hold must simply leave
            // the flag set and be read back at the next stage.
            LoggerUtil.Log("COOP dropvis: take settled havevas=" + Prop(_dvInv, "havevas"));
            DropVisLog("after-take");
        }

        /// <summary>The game's own drop: hand socket off, flag still true, then
        /// CheckInventory() spawns Vas(Clone) at DropPoint and clears the flag.
        /// ItemSync sees the true -> false edge and sends the placement.</summary>
        private void DropVisDrop()
        {
            var hand = SafeProp(_dvInv, "vas") as GameObject;
            LoggerUtil.Log("COOP dropvis: drop hand(before)=" + DropVisObj(hand));
            if (hand != null) hand.SetActive(false);
            DropVisWrite(_dvInv, "havevas", true);
            DropVisCheckInventory("drop");
            LoggerUtil.Log("COOP dropvis: drop settled havevas=" + Prop(_dvInv, "havevas"));
            DropVisLog("after-drop");
            DumpVasWorld("after-drop");
        }

        private void DropVisCheckInventory(string tag)
        {
            try
            {
                _dvInv.CheckInventory();
                LoggerUtil.Log("COOP dropvis: CheckInventory(" + tag + ") returned havevas=" + Prop(_dvInv, "havevas"));
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP dropvis: CheckInventory(" + tag + ") FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void DropVisDropPoint()
        {
            try
            {
                var p = _dvInv.GetType().GetProperty("dropPoint");
                var go = p != null ? p.GetValue(_dvInv) as GameObject : null;
                if (go == null) { LoggerUtil.Log("COOP dropvis: dropPoint=-"); return; }
                var q = go.transform.position;
                LoggerUtil.Log("COOP dropvis: dropPoint path=" + PathOf(go.transform) +
                               " activeSelf=" + (go.activeSelf ? "y" : "n") +
                               " pos=" + Fmt(q) + " fwd=" + Fmt(go.transform.forward));
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP dropvis: dropPoint FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private string DropVisClickDrop()
        {
            try
            {
                GameObject go = null;
                if (_dvInv != null)
                {
                    var p = _dvInv.GetType().GetProperty("dropObjectButton");
                    if (p != null) go = p.GetValue(_dvInv) as GameObject;
                }
                if (go != null)
                {
                    LoggerUtil.Log("COOP dropvis: dropObjectButton path=" + PathOf(go.transform) +
                                   " activeSelf=" + (go.activeSelf ? "y" : "n"));
                    var b = go.GetComponent<UnityEngine.UI.Button>();
                    if (b != null)
                    {
                        b.onClick.Invoke();
                        return "button '" + PathOf(go.transform) + "'";
                    }
                    var b2 = go.GetComponentInChildren<UnityEngine.UI.Button>(true);
                    if (b2 != null)
                    {
                        b2.onClick.Invoke();
                        return "child button '" + PathOf(b2.transform) + "'";
                    }
                }

                // Fall back to any dropObjects script's wired button.
                foreach (var d in FindAll<dropObjects>())
                {
                    var p = d.GetType().GetProperty("btn1");
                    var b3 = p != null ? p.GetValue(d) as UnityEngine.UI.Button : null;
                    if (b3 == null) continue;
                    var path = PathOf(b3.transform);
                    b3.onClick.Invoke();
                    return "dropObjects.btn1 '" + path + "'";
                }
                return go == null ? "dropObjectButton is null" : "no Button found";
            }
            catch (Exception e)
            {
                return "FAILED " + e.GetType().Name + ": " + e.Message;
            }
        }

        private static bool DropVisWrite(object o, string name, object value)
        {
            if (o == null) return false;
            try
            {
                var p = o.GetType().GetProperty(name);
                if (p == null || !p.CanWrite) return false;
                p.SetValue(o, value);
                return true;
            }
            catch { return false; }
        }

        private Dictionary<string, ItemSnap> DropVisSnapshot()
        {
            var map = new Dictionary<string, ItemSnap>(64);
            if (_dvInv == null) return map;
            try
            {
                foreach (var prop in _dvInv.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.Name.StartsWith("new", StringComparison.Ordinal)) continue;
                    var t = SafeGet(prop, _dvInv) as Transform;
                    var snap = new ItemSnap { Id = 0, Path = "-" };
                    if (t != null)
                    {
                        try
                        {
                            snap.Id = t.GetInstanceID();
                            snap.Pos = t.position;
                            snap.Active = t.gameObject.activeSelf;
                            snap.Path = PathOf(t);
                        }
                        catch { snap.Id = -1; }
                    }
                    map[prop.Name] = snap;
                }
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP dropvis: snapshot FAILED: " + e.GetType().Name + ": " + e.Message);
            }
            return map;
        }

        private void DropVisLog(string tag)
        {
            string flags;
            try
            {
                flags = "havevas=" + Prop(_dvInv, "havevas") +
                        " havevas2=" + Prop(_dvInv, "havevas2") +
                        " placeObject=" + Prop(_dvInv, "placeObject");
            }
            catch { flags = "flags=!"; }
            string pu;
            try
            {
                pu = _dvPick == null
                    ? " pickup=-"
                    : " pu.playerTaken=" + Prop(_dvPick, "playerTaken") +
                      " pu.pickUp=" + Prop(_dvPick, "pickUp") +
                      " pu.dropObject=" + Prop(_dvPick, "dropObject") +
                      " pu.placeObject=" + Prop(_dvPick, "placeObject") +
                      " pu.readyPickUp=" + Prop(_dvPick, "readyPickUp");
            }
            catch { pu = " pu=!"; }

            var now = DropVisSnapshot();
            var sb = new StringBuilder();
            int changed = 0, gone = 0;
            foreach (var kv in _dvBefore)
            {
                ItemSnap b = kv.Value;
                ItemSnap a;
                if (!now.TryGetValue(kv.Key, out a) || a.Id <= 0)
                {
                    sb.Append("\n  ").Append(kv.Key).Append(" GONE id=").Append(b.Id).Append(" wasActive=").Append(b.Active ? "y" : "n");
                    gone++;
                    continue;
                }
                float d = Vector3.Distance(a.Pos, b.Pos);
                bool moved = d > 0.01f;
                bool vis = a.Active != b.Active;
                if (!moved && !vis) continue;
                changed++;
                sb.Append("\n  ").Append(kv.Key).Append(' ')
                  .Append(b.Active ? "y" : "n").Append("->").Append(a.Active ? "y" : "n")
                  .Append(" id=").Append(a.Id)
                  .Append(" pos=").Append(Fmt(b.Pos)).Append(" -> ").Append(Fmt(a.Pos))
                  .Append(" d=").Append(d.ToString("F2"))
                  .Append(" path=").Append(a.Path);
            }
            sb.Append("\n  changed=").Append(changed).Append(" gone=").Append(gone).Append('/').Append(_dvBefore.Count);
            LoggerUtil.Log("COOP dropvis " + tag + ": " + flags + pu);
            LoggerUtil.Log("COOP dropvis diff " + tag + ":" + sb);
            DumpVasWorld(tag);
            DropVisIdent(tag);
        }

        /// <summary>All transforms whose name contains "Vas", plus their direct
        /// children, with instance ids. Marks ids that were not present in the
        /// first dump as NEW, which is how a spawned drop copy shows up.</summary>
        private void DumpVasWorld(string tag)
        {
            var sb = new StringBuilder();
            bool first = _dvVasIds.Count == 0;
            int n = 0, fresh = 0;
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<Transform>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null) continue;
                    string name;
                    try { name = t.name; } catch { continue; }
                    if (name.IndexOf("Vas", StringComparison.Ordinal) < 0) continue;
                    int id = t.GetInstanceID();
                    bool isNew = false;
                    if (first) _dvVasIds.Add(id);
                    else if (!_dvVasIds.Contains(id)) { isNew = true; fresh++; }

                    Vector3 q;
                    bool act;
                    string path;
                    try { q = t.position; act = t.gameObject.activeSelf; path = PathOf(t); }
                    catch { q = Vector3.zero; act = false; path = "!"; }
                    sb.Append("\n  ").Append(isNew ? "NEW " : "").Append(name)
                      .Append(" id=").Append(id)
                      .Append(" activeSelf=").Append(act ? "y" : "n")
                      .Append(" pos=").Append(Fmt(q))
                      .Append(" children=").Append(t.childCount)
                      .Append(" path=").Append(path);
                    int kids = Mathf.Min(t.childCount, 8);
                    for (int c = 0; c < kids; c++)
                    {
                        var ch = t.GetChild(c);
                        if (ch == null) continue;
                        var cq = ch.position;
                        sb.Append("\n      child ").Append(ch.name)
                          .Append(" activeSelf=").Append(ch.gameObject.activeSelf ? "y" : "n")
                          .Append(" local=").Append(Fmt(ch.localPosition))
                          .Append(" pos=").Append(Fmt(cq));
                    }
                    n++;
                }
            }
            catch (Exception e)
            {
                sb.Append(" FAILED: ").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            LoggerUtil.Log("COOP dropvis vas " + tag + ": matches=" + n + " new=" + fresh + sb);
        }

        private static string Fmt(Vector3 v)
        {
            return v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2");
        }

        // ---- helpers --------------------------------------------------------

        private static List<T> FindAll<T>() where T : MonoBehaviour
        {
            var into = new List<T>();
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<T>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) into.Add(all[i]);
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP probe: find " + typeof(T).Name + " FAILED: " + e.GetType().Name + ": " + e.Message);
            }
            return into;
        }

        private static object SafeGet(PropertyInfo p, object o)
        {
            try { return p.GetValue(o); } catch { return null; }
        }

        private static object SafeProp(object o, string name)
        {
            try
            {
                var p = o.GetType().GetProperty(name);
                return p == null ? null : p.GetValue(o);
            }
            catch { return null; }
        }

        private static string Prop(object o, string name)
        {
            try
            {
                var p = o.GetType().GetProperty(name);
                if (p == null) return "-";
                var v = p.GetValue(o);
                return v == null ? "null" : v.ToString();
            }
            catch { return "!"; }
        }

        private static bool TryValue(object o, PropertyInfo p, out string value)
        {
            value = null;
            try
            {
                var v = p.GetValue(o);
                if (v == null) { value = "null"; return true; }
                var s = v.ToString();
                if (s.Length > 60) s = s.Substring(0, 60) + "…";
                value = s;
                return true;
            }
            catch { return false; }
        }

        private static string SafeActive(MonoBehaviour mb)
        {
            try { return mb.gameObject.activeInHierarchy ? "y" : "n"; } catch { return "?"; }
        }

        private static string PathOf(Transform t)
        {
            var s = t.name;
            var p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 12) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }
    }
}
