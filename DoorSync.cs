using System;
using System.Collections.Generic;
using System.Reflection;
using Il2Cpp;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyCoop
{
    /// <summary>
    /// Replicates the 48 swinging doors (NOTES.md section 13). Host-authoritative:
    /// both sides poll the game's own door state - the tag, with the DoorOpen bool
    /// as an earlier signal - and exchange it as (hash, state) pairs keyed by an
    /// FNV-1a hash of the hierarchy path.
    ///
    /// A state change is shown locally by sampling the door's own open clip over
    /// the clip's length, so the pose and any other curves the clip carries (the
    /// NavMeshObstacle enable track included) come from the game's own data
    /// rather than from us. The clip is chosen once per scene load by
    /// measurement: every candidate is sampled on the door's hierarchy and the
    /// one that moves a descendant the most wins. Sampling that same clip at its
    /// other end is the closed pose, so one clip per door is enough.
    /// </summary>
    internal sealed class DoorSync
    {
        public const byte Closed = 0, Open = 1, Locked = 2, Unknown = 255;

        public const byte KindState = 0;           // host -> client, one door
        public const byte KindSnapshot = 1;        // host -> client, all doors
        public const byte KindRequest = 2;         // client -> host, one door
        public const byte KindSnapshotRequest = 3; // client -> host, ask for the table

        private const float PollInterval = 0.1f;
        private const float SnapshotInterval = 10f;
        private const float SnapshotRequestInterval = 5f;
        private const float RequestRetry = 0.4f;
        private const int RequestTries = 3;
        private const float RequestRange = 8f;
        private const float SnapRange = 15f;
        private const float SettleDelay = 1.5f;
        private const float IgnorePollFor = 2.5f;

        private sealed class Door
        {
            public MonoBehaviour Comp;
            public GameObject Go;
            public Transform[] Trs;
            public Animation Anim;
            public AnimationClip OpenClip;
            public float OpenLen;
            public bool OpenAtEnd = true;
            public float MeasuredDelta;
            public uint Hash;
            public string Path;
            public byte State = Unknown;
            public PropertyInfo BoolProp;
            public bool HasBool;
            public bool Warned;
            public bool Playing;
            public float PlayStart;
            public float PlayDur;
            public float PlayFrom;
            public float PlayTo;
            public float IgnorePollUntil;
            // Client-side request bookkeeping.
            public byte ReqState;
            public int ReqLeft;
            public float ReqNext;
        }

        private readonly List<Door> _doors = new List<Door>();
        private bool _prepared;
        private float _prepAccum;
        private float _pollAccum;
        private float _snapAccum;
        private float _reqSnapAccum;
        private bool _gotSnapshot;

        /// <summary>QA only (--coop-doortest): the host flips the nearest door
        /// every few seconds by writing the game's own state fields, as a
        /// player's interaction would, and leaves State alone so Poll sees it
        /// as a local change. It exercises observe -> send -> apply without a
        /// human, and also shows whether the game's door script rewrites what
        /// we wrote.</summary>
        public bool DoorTest;

        private const float DoorTestPeriod = 6f;
        private float _testAccum;
        private int _testCount;
        private bool _testWarned;

        public int DoorCount => _doors.Count;
        public int Sent { get; private set; }
        public int Recv { get; private set; }
        public int Applied { get; private set; }
        public int Requests { get; private set; }
        public int NoClip { get; private set; }

        public void Reset()
        {
            _doors.Clear();
            _prepared = false;
            _prepAccum = 0f;
            _pollAccum = 0f;
            _snapAccum = 0f;
            _reqSnapAccum = 0f;
            _gotSnapshot = false;
            _testAccum = 0f;
            _testCount = 0;
            _testWarned = false;
            Sent = Recv = Applied = Requests = NoClip = 0;
        }

        // ---- main tick -------------------------------------------------------

        public void Tick(float dt, GameObject player, CoopRole role, Func<byte[], bool> send)
        {
            if (role == CoopRole.Off) return;
            if (player == null) return;

            if (!_prepared)
            {
                // Only world discovery waits for an active player: the game keeps
                // the Player root inactive through its intro, and the scene may
                // still be filling in. Once mapped, sync does not depend on it.
                if (!player.activeSelf) return;
                _prepAccum += dt;
                if (_prepAccum < SettleDelay) return; // let the scene settle
                Prepare(role);
                if (!_prepared) return;
                if (role == CoopRole.Join)
                {
                    if (send(BuildKindBody(KindSnapshotRequest))) Sent++;
                }
            }
            // Past this point keep polling while the Player root is inactive: the
            // game re-keys doors during the caught/respawn sequence, and pausing
            // here let the two worlds drift apart for the whole 10-20 s window.

            _pollAccum += dt;
            if (_pollAccum >= PollInterval)
            {
                _pollAccum = 0f;
                float now = UnityEngine.Time.unscaledTime;
                Poll(now, player, role, send);
                TickPlayback();
                TickRequests(now, send);

                if (role == CoopRole.Host)
                {
                    _snapAccum += PollInterval;
                    if (_snapAccum >= SnapshotInterval)
                    {
                        _snapAccum = 0f;
                        // QA only: a door whose live state disagrees with the
                        // table we are about to publish was reverted while Poll
                        // could not see it (e.g. the Player root was inactive).
                        int stale = 0;
                        foreach (var d in _doors)
                            if (d.State != Unknown && d.State != Observe(d)) stale++;
                        if (send(BuildSnapshotBody())) { Sent++; LoggerUtil.Log("COOP doors: snapshot sent stale=" + stale); }
                    }
                }
            }

            // QA-only: either side may drive a flip, so the harness can exercise
            // the join -> host Request path as well as the host -> join one.
            if (DoorTest) TickDoorTest(dt, player);

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
                LoggerUtil.Log("COOP doors: scan " + typeof(T).Name + " FAILED: " +
                               e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// One pass per scene load: find the 16 animating door scripts, pair each
        /// with the Animation component that drives it, and pick one clip by
        /// measurement. Every door in this game is a legacy Animation whose open
        /// pose is the last frame of an Open clip, so a single clip can show both
        /// directions: forward to open, sampled backwards to close.
        /// </summary>
        private void Prepare(CoopRole role)
        {
            _prepared = true;

            var comps = new List<MonoBehaviour>();
            Add<OpenCloseDoors>(comps);
            Add<OpenCloseDoorVind>(comps);
            Add<OpenCloseGarde1>(comps);
            Add<OpenCloseKitchenDoor1>(comps);
            Add<OpenCloseKitchenDoor2>(comps);
            Add<OpenCloseKyl>(comps);
            Add<OpenCloseLoda1>(comps);
            Add<OpenCloseLoda2>(comps);
            Add<openCloseMotorhuv>(comps);
            Add<OpenCloseSmallDoors>(comps);
            Add<openCloseVitrindoorL>(comps);
            Add<openCloseVitrindoorR>(comps);
            Add<openCloseVitrindoorVRL>(comps);
            Add<openCloseVitrindoorVRR>(comps);
            Add<openCloseBaklucka>(comps);
            Add<prisonDoorOpenClose>(comps);

            foreach (var c in comps)
            {
                try
                {
                    var d = new Door
                    {
                        Comp = c,
                        Go = c.gameObject,
                        Path = PathOf(c.transform),
                    };
                    d.Hash = Fnv(d.Path);
                    d.Trs = c.gameObject.GetComponentsInChildren<Transform>(true);
                    d.Anim = FindAnimation(c.gameObject);
                    d.BoolProp = c.GetType().GetProperty("DoorOpen");
                    d.HasBool = d.BoolProp != null && d.BoolProp.CanRead &&
                                d.BoolProp.PropertyType == typeof(bool);
                    d.State = Observe(d);

                    if (d.Anim != null) ChooseClip(d);
                    if (d.OpenClip == null) NoClip++;

                    // A door that is already open has its rest pose at the open
                    // end, so the clip's far end is the closed pose: invert the
                    // direction the measurement implied. Only the prison door
                    // starts open (NOTES.md section 13), and its clip is named
                    // "prisondoorLocked", which the name heuristic cannot read.
                    if (d.OpenClip != null && d.State == Open)
                    {
                        d.OpenAtEnd = !d.OpenAtEnd;
                        LoggerUtil.Log("COOP doors: " + d.Path +
                                       " starts open, sampling clip in reverse");
                    }

                    _doors.Add(d);
                    LoggerUtil.Log("COOP doors: map " + _doors.Count +
                                   " " + c.GetType().Name +
                                   " hash=" + d.Hash.ToString("X8") +
                                   " state=" + StateName(d.State) +
                                   " clip=" + (d.OpenClip != null ? d.OpenClip.name : "-") +
                                   " len=" + d.OpenLen.ToString("F2") +
                                   " openAtEnd=" + (d.OpenAtEnd ? "y" : "n") +
                                   " delta=" + d.MeasuredDelta.ToString("F1") +
                                   " path=" + d.Path);
                }
                catch (Exception e)
                {
                    LoggerUtil.Log("COOP doors: prepare FAILED: " + e.GetType().Name + ": " + e.Message);
                }
            }

            var byType = new Dictionary<string, int>();
            foreach (var d in _doors)
            {
                string n = d.Comp.GetType().Name;
                byType.TryGetValue(n, out int k);
                byType[n] = k + 1;
            }
            var keys = new List<string>(byType.Keys);
            keys.Sort();
            var counts = new System.Text.StringBuilder();
            foreach (var k in keys) counts.Append(k).Append('=').Append(byType[k]).Append(' ');
            LoggerUtil.Log("COOP doors: prepared " + _doors.Count + " doors as " + role +
                           ", noClip=" + NoClip + ", types: " + counts);
        }

        private static Animation FindAnimation(GameObject go)
        {
            try
            {
                var a = go.GetComponent<Animation>();
                if (a != null) return a;
                var kids = go.GetComponentsInChildren<Animation>(true);
                if (kids != null)
                    for (int i = 0; i < kids.Length; i++)
                        if (kids[i] != null) return kids[i];
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Pick the clip that moves this door the most, by sampling each one at
        /// its far end on the door's own hierarchy. Clip names are not one
        /// scheme across this game (NOTES.md section 13), so measurement decides;
        /// a name only breaks ties and says which end is open.
        /// </summary>
        private void ChooseClip(Door d)
        {
            int n = d.Trs.Length;
            var rot = new Quaternion[n];
            var pos = new Vector3[n];
            var obs = new List<NavMeshObstacle>();
            for (int i = 0; i < n; i++)
            {
                rot[i] = d.Trs[i].localRotation;
                pos[i] = d.Trs[i].localPosition;
                var o = d.Trs[i].GetComponent<NavMeshObstacle>();
                if (o != null) obs.Add(o);
            }
            var obsOn = new bool[obs.Count];
            for (int i = 0; i < obs.Count; i++) { try { obsOn[i] = obs[i].enabled; } catch { } }

            AnimationClip best = null;
            float bestDelta = 0f;
            float bestScore = 0f;
            bool bestOpenAtEnd = true;

            try
            {
                var en = d.Anim.GetEnumerator();
                int guard = 0;
                while (en.MoveNext() && guard++ < 24)
                {
                    AnimationState st = null;
                    try
                    {
                        st = (en.Current as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                             ?.TryCast<AnimationState>();
                    }
                    catch { }
                    if (st == null) continue;

                    AnimationClip clip = null;
                    try { clip = st.clip; } catch { }
                    if (clip == null || clip.length <= 0.01f) continue;

                    float delta = 0f;
                    try
                    {
                        clip.SampleAnimation(d.Go, Mathf.Max(0f, clip.length - 0.01f));
                        delta = PoseDelta(d, rot, pos);
                    }
                    catch { }
                    finally
                    {
                        for (int i = 0; i < n; i++)
                        {
                            d.Trs[i].localRotation = rot[i];
                            d.Trs[i].localPosition = pos[i];
                        }
                        for (int i = 0; i < obs.Count; i++)
                        {
                            try { obs[i].enabled = obsOn[i]; } catch { }
                        }
                    }

                    if (delta < 1f) continue;
                    bool openAtEnd = IsOpenClip(clip.name);
                    // A small preference for an explicitly open-named clip, so
                    // an equal pair always resolves to the same direction.
                    float score = delta + (openAtEnd ? 2f : 0f);
                    if (score > bestScore + 0.5f)
                    {
                        best = clip;
                        bestDelta = delta;
                        bestScore = score;
                        bestOpenAtEnd = openAtEnd;
                    }
                }
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP doors: clip scan FAILED on " + d.Path + ": " +
                               e.GetType().Name + ": " + e.Message);
            }

            d.OpenClip = best;
            d.OpenLen = best != null ? best.length : 0f;
            d.MeasuredDelta = best != null ? bestDelta : 0f;
            d.OpenAtEnd = bestOpenAtEnd;
            if (best != null && !IsOpenClip(best.name))
            {
                LoggerUtil.Log("COOP doors: no open-named clip on " + d.Path +
                               ", using [" + best.name + "] reversed");
            }
        }

        /// <summary>Largest change a sampled pose made to the door hierarchy,
        /// with one metre of translation counted as ~57 degrees so a sliding
        /// door still wins against a tiny rotation.</summary>
        private static float PoseDelta(Door d, Quaternion[] rot, Vector3[] pos)
        {
            float best = 0f;
            for (int i = 0; i < d.Trs.Length; i++)
            {
                var t = d.Trs[i];
                float ang = Quaternion.Angle(rot[i], t.localRotation);
                float lin = Vector3.Distance(pos[i], t.localPosition) * 57.29578f;
                if (ang > best) best = ang;
                if (lin > best) best = lin;
            }
            return best;
        }

        private static bool IsOpenClip(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return !name.ToLowerInvariant().Contains("close");
        }

        // ---- state ------------------------------------------------------------

        /// <summary>
        /// The game keeps a door's state in two places: a tag whose suffix is
        /// Closed/Open/Locked on most doors, and a DoorOpen bool on the script.
        /// The tag lags the pose by up to ~1.8 s (NOTES.md section 13), so the
        /// bool is an earlier signal; either one saying Open wins.
        /// </summary>
        private byte Observe(Door d)
        {
            bool open = false, closed = false, locked = false;
            string tag = "";
            try { tag = d.Go.tag ?? ""; } catch { }
            string lower = tag.ToLowerInvariant();
            if (lower.EndsWith("locked")) locked = true;
            else if (lower.EndsWith("open")) open = true;
            else if (lower.EndsWith("closed")) closed = true;

            bool boolOpen = false;
            if (d.HasBool)
            {
                try { boolOpen = (bool)d.BoolProp.GetValue(d.Comp); }
                catch { d.HasBool = false; }
            }

            if (locked && !open) return Locked;
            if (open || boolOpen) return Open;
            if (closed || d.HasBool) return Closed;
            return Unknown;
        }

        /// <summary>Tell the game's own logic what this door is doing, so Granny
        /// pathing and triggers agree with the pose we are showing.</summary>
        private void WriteState(Door d, byte state)
        {
            if (d.HasBool && d.BoolProp.CanWrite)
            {
                try { d.BoolProp.SetValue(d.Comp, state == Open); }
                catch (Exception e)
                {
                    if (!d.Warned)
                        LoggerUtil.Log("COOP doors: DoorOpen write FAILED on " + d.Path + ": " +
                                       e.GetType().Name + ": " + e.Message);
                }
            }

            try
            {
                string tag = d.Go.tag ?? "";
                string want = SwapTag(tag, state);
                if (want != null && want != tag) d.Go.tag = want;
            }
            catch (Exception e)
            {
                if (!d.Warned)
                {
                    d.Warned = true;
                    LoggerUtil.Log("COOP doors: tag write FAILED on " + d.Path + " tag=" +
                                   SafeTag(d.Go) + ": " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>Replace only a recognised state suffix, keeping the prefix and
        /// its casing: innerdoorClosed becomes innerdoorOpen.</summary>
        private static string SwapTag(string tag, byte state)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string[] suffixes = { "closed", "open", "locked" };
            foreach (var s in suffixes)
            {
                if (!tag.EndsWith(s, StringComparison.OrdinalIgnoreCase)) continue;
                string prefix = tag.Substring(0, tag.Length - s.Length);
                string word = state == Open ? "open" : state == Locked ? "locked" : "closed";
                if (char.IsUpper(tag[tag.Length - s.Length]))
                    word = char.ToUpperInvariant(word[0]) + word.Substring(1);
                return prefix + word;
            }
            return null;
        }

        private static string SafeTag(GameObject go)
        {
            try { return go.tag; } catch { return "?"; }
        }

        // ---- pose -------------------------------------------------------------

        /// <summary>Show a state. Near doors animate over the clip's own length;
        /// far ones snap, so a late-joining client does not play a hundred
        /// animations at once.</summary>
        private void Apply(Door d, byte state, bool animate)
        {
            d.State = state;
            WriteState(d, state);

            if (!animate || d.OpenClip == null)
            {
                d.Playing = false;
                SamplePose(d, state == Open ? 1f : 0f);
                return;
            }

            d.Playing = true;
            d.PlayStart = UnityEngine.Time.unscaledTime;
            d.PlayDur = Mathf.Clamp(d.OpenLen, 0.08f, 3f);
            d.PlayFrom = state == Open ? 0f : 1f;
            d.PlayTo = state == Open ? 1f : 0f;
        }

        /// <summary>Sample the door's single clip at an openness from 0 (closed)
        /// to 1 (open). The clip's far end is the open pose when it is an Open
        /// clip, the near end when it is a Close one.</summary>
        private static void SamplePose(Door d, float s)
        {
            if (d.OpenClip == null) return;
            float span = Mathf.Max(0.001f, d.OpenLen - 0.01f);
            float t = d.OpenAtEnd ? s * span : (1f - s) * span;
            try { d.OpenClip.SampleAnimation(d.Go, t); } catch { }
        }

        private void TickPlayback()
        {
            float now = UnityEngine.Time.unscaledTime;
            foreach (var d in _doors)
            {
                if (!d.Playing) continue;
                float u = Mathf.Clamp01((now - d.PlayStart) / Mathf.Max(0.001f, d.PlayDur));
                SamplePose(d, Mathf.Lerp(d.PlayFrom, d.PlayTo, u));
                if (u >= 1f) d.Playing = false;
            }
        }

        // ---- local changes ----------------------------------------------------

        private void Poll(float now, GameObject player, CoopRole role, Func<byte[], bool> send)
        {
            Vector3 p = player.transform.position;
            foreach (var d in _doors)
            {
                if (now < d.IgnorePollUntil) continue;
                byte observed = Observe(d);
                if (observed == Unknown || observed == d.State) continue;

                d.State = observed;
                if (role == CoopRole.Host)
                {
                    if (send(BuildDoorBody(KindState, d.Hash, observed)))
                    {
                        Sent++;
                        LoggerUtil.Log("COOP doors: send " + d.Path + " -> " +
                                       StateName(observed));
                    }
                }
                else
                {
                    float dist = Vector3.Distance(p, d.Go.transform.position);
                    if (dist > RequestRange) continue;
                    d.ReqState = observed;
                    d.ReqLeft = RequestTries;
                    d.ReqNext = now;
                }
            }
        }

        private void TickRequests(float now, Func<byte[], bool> send)
        {
            foreach (var d in _doors)
            {
                if (d.ReqLeft <= 0 || now < d.ReqNext) continue;
                if (send(BuildDoorBody(KindRequest, d.Hash, d.ReqState)))
                {
                    Requests++;
                    d.ReqLeft--;
                }
                d.ReqNext = now + RequestRetry;
            }
        }

        // ---- QA door flip -----------------------------------------------------

        private void TickDoorTest(float dt, GameObject player)
        {
            _testAccum += dt;
            if (_testAccum < DoorTestPeriod) return;
            _testAccum = 0f;

            Door best = null;
            float bestD = float.MaxValue;
            foreach (var d in _doors)
            {
                if (d.OpenClip == null) continue;
                if (d.State != Closed && d.State != Open) continue;
                float dist = Distance(player, d);
                if (dist < bestD) { bestD = dist; best = d; }
            }

            if (best == null)
            {
                if (!_testWarned)
                {
                    _testWarned = true;
                    LoggerUtil.Log("COOP doors: TEST no open/closed door to flip");
                }
                return;
            }

            byte want = best.State == Open ? Closed : Open;
            WriteState(best, want);
            _testCount++;
            LoggerUtil.Log("COOP doors: TEST flip #" + _testCount + " " + best.Path +
                           " " + StateName(best.State) + " -> " + StateName(want) +
                           " at " + bestD.ToString("F1") + "m");
        }

        // ---- helpers ----------------------------------------------------------

        private Door Find(uint hash)
        {
            foreach (var d in _doors) if (d.Hash == hash) return d;
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

        private static string StateName(byte s)
        {
            switch (s)
            {
                case Closed: return "closed";
                case Open: return "open";
                case Locked: return "locked";
                default: return "?";
            }
        }

        private static string PathOf(Transform t)
        {
            string s = t.name;
            var p = t.parent;
            int guard = 0;
            while (p != null && guard++ < 12) { s = p.name + "/" + s; p = p.parent; }
            return s;
        }

        // ---- wire -------------------------------------------------------------

        private static byte[] BuildDoorBody(byte kind, uint hash, byte state)
        {
            using (var ms = new System.IO.MemoryStream(16))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(kind);
                w.Write(hash);
                w.Write(state);
                return ms.ToArray();
            }
        }

        private byte[] BuildSnapshotBody()
        {
            using (var ms = new System.IO.MemoryStream(256))
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(KindSnapshot);
                // Always publish the world, not our cache: Poll stops while the
                // Player root is inactive (the game deactivates it during the
                // caught/respawn sequence), so d.State can be many seconds old
                // exactly when a joining client asks for the table.
                var states = new List<byte>(_doors.Count);
                foreach (var d in _doors) states.Add(Observe(d));
                int count = 0;
                foreach (var st in states) if (st != Unknown) count++;
                w.Write((byte)Math.Min(255, count));
                for (int i = 0; i < _doors.Count; i++)
                {
                    if (states[i] == Unknown) continue;
                    w.Write(_doors[i].Hash);
                    w.Write(states[i]);
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

        public void OnMessage(MsgReader r, GameObject player, CoopRole role, Func<byte[], bool> send)
        {
            Recv++;
            float now = UnityEngine.Time.unscaledTime;
            byte kind = r.Byte();

            switch (kind)
            {
                case KindState:
                {
                    uint hash = r.U32();
                    byte st = r.Byte();
                    if (role != CoopRole.Join || !_prepared || st == Unknown) break;
                    var d = Find(hash);
                    if (d == null) break;
                    if (d.State == st) break;
                    Apply(d, st, Distance(player, d) <= SnapRange);
                    d.IgnorePollUntil = now + IgnorePollFor;
                    Applied++;
                    LoggerUtil.Log("COOP doors: apply " + d.Path + " -> " + StateName(st) +
                                   " at " + Distance(player, d).ToString("F1") + "m (msg)");
                    break;
                }

                case KindSnapshot:
                {
                    int n = r.Byte();
                    if (role != CoopRole.Join || !_prepared) break;
                    for (int i = 0; i < n; i++)
                    {
                        uint hash = r.U32();
                        byte st = r.Byte();
                        if (st == Unknown) continue;
                        var d = Find(hash);
                        if (d == null) continue;
                        if (d.State == st) continue;
                        Apply(d, st, Distance(player, d) <= SnapRange);
                        d.IgnorePollUntil = now + IgnorePollFor;
                        Applied++;
                    }
                    _gotSnapshot = true;
                    break;
                }

                case KindRequest:
                {
                    uint hash = r.U32();
                    byte st = r.Byte();
                    if (role != CoopRole.Host || !_prepared || st == Unknown) break;
                    var d = Find(hash);
                    if (d == null)
                    {
                        // The client has a door we do not know: send the table.
                        send(BuildSnapshotBody());
                        break;
                    }
                    // Poll stops while the Player root is inactive, so refresh from
                    // the world before answering: otherwise a request that lands
                    // during that window is answered from a table the game has
                    // already reverted, and the client applies our stale value.
                    byte live = Observe(d);
                    if (live != Unknown && live != d.State)
                    {
                        LoggerUtil.Log("COOP doors: refresh " + d.Path + " " +
                                       StateName(d.State) + " -> " + StateName(live) +
                                       " (request)");
                        d.State = live;
                    }
                    // A locked door stays locked whatever the client saw.
                    if (d.State == Locked && st != Locked) { send(BuildDoorBody(KindState, d.Hash, d.State)); break; }
                    if (d.State != st)
                    {
                        Apply(d, st, true);
                        d.IgnorePollUntil = now + IgnorePollFor;
                        Applied++;
                        LoggerUtil.Log("COOP doors: apply " + d.Path + " -> " + StateName(st) + " (request)");
                    }
                    send(BuildDoorBody(KindState, d.Hash, d.State));
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

        private static float Distance(GameObject player, Door d)
        {
            if (player == null) return 0f;
            try { return Vector3.Distance(player.transform.position, d.Go.transform.position); }
            catch { return 0f; }
        }

        /// <summary>QA only (soak runs): a fingerprint of every door's live
        /// state, so the two ends' status lines can be diffed. Read through
        /// Observe - the game's own tag/bool - not the last value we applied,
        /// so an end that silently drifted shows up. Doors still Unknown are
        /// left out on both ends; the count catches a door one end never found.</summary>
        public string StateFingerprint()
        {
            // "not-ready" rather than "0:811C9DC5": an end that has not mapped
            // the world yet must not compare equal to another blind end and
            // read as a matched world in the soak diff.
            if (!_prepared) return "not-ready";
            var pairs = new List<KeyValuePair<uint, byte>>();
            for (int i = 0; i < _doors.Count; i++)
            {
                byte st = Observe(_doors[i]);
                if (st == Unknown) continue;
                pairs.Add(new KeyValuePair<uint, byte>(_doors[i].Hash, st));
            }
            return Protocol.Fingerprint(pairs);
        }

        public string Status()
        {
            return "doors=" + _doors.Count + " applied=" + Applied +
                   " sent=" + Sent + " recv=" + Recv + " req=" + Requests +
                   " noClip=" + NoClip;
        }
    }
}
