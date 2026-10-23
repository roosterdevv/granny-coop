using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

[assembly: MelonInfo(typeof(GrannyCoop.GrannyCoopMod), "Granny Co-op", "0.5.1", "coop-anything")]
[assembly: MelonGame("DVloper", "Granny")]

namespace GrannyCoop
{
    public sealed class GrannyCoopMod : MelonMod
    {
        private CoopConfig _cfg;
        private CoopNet _net;
        private readonly RemoteAvatar _remote = new RemoteAvatar();
        private readonly RemoteGranny _granny = new RemoteGranny();
        private readonly DoorSync _doors = new DoorSync();
        private readonly ItemSync _items = new ItemSync();
        private readonly WorldProbe _probe = new WorldProbe();
        private readonly CatchSync _catch = new CatchSync();

        private string _scene = "";
        private float _sendAccum;

        // Guest scene follow: the host is authoritative for which scene we are
        // in. One load in flight at a time; the load itself is what clears the
        // request, so a scene that never arrives is retried a few times and
        // then dropped instead of pinning the guest in a load loop.
        private string _pendingScene;
        private float _pendingSceneAt;
        private int _pendingSceneTries;
        private int _sceneFollows;
        private float _grannyAccum;
        private float _grannyBindAccum;
        private int _statusBucket = -1;
        private int _stateSent;
        private int _stateRecv;
        private int _grannySent;
        private int _grannyRecv;
        private Transform _camT;
        private CharacterController _cc;
        private bool _sawPlayerActive;

        // QA only: the Player root goes inactive for stretches during a run
        // (observed in the v0.4.1 sanity run), and both sync ticks stop while
        // it does. Log each edge so the log shows whether the game or the mod
        // paused, and when a missed change gets picked up again.
        private bool _playerActive;
        private bool _playerActiveKnown;

        // QA-only: walk the local character so the other instance has something
        // to interpolate (there is no input injection in a headless test run).
        private float _testMoveClock;
        private bool _testMoveLogged;

        // QA-only: teleport the local player onto Granny so a catch happens
        // without a human driving her.
        private float _catchTestAccum;

        // Auto play (test harness only)
        private int _menuFrames = -1;
        private int _afterClick;
        private bool _autoContinued;
        private int _playTries;

        public override void OnInitializeMelon()
        {
            _cfg = CoopConfig.Load();
            _doors.DoorTest = _cfg.DoorTest;
            _items.ItemTest = _cfg.ItemTest;
            LoggerUtil.Log($"COOP initialised v0.5.1 role={_cfg.Role} name='{_cfg.PlayerName}' " +
                           $"addr={_cfg.Address}:{_cfg.Port} autoplay={_cfg.AutoPlay} probe={_cfg.Probe} " +
                           $"doortest={_cfg.DoorTest} itemtest={_cfg.ItemTest} " +
                           $"spectate={_cfg.Spectate} catchtest={_cfg.CatchTest}");

            if (_cfg.Role == CoopRole.Off)
            {
                LoggerUtil.Log("COOP idle: set Mode=Host/Join in UserData/MelonPreferences.cfg " +
                               "or pass --coop-host / --coop-join=<address>");
                return;
            }

            _net = new CoopNet(_cfg.Role, _cfg.PlayerName, _cfg.Address, _cfg.Port);
            _net.AppMessage += OnAppMessage;
            _net.ConnectionChanged += OnConnectionChanged;
        }

        public override void OnUpdate()
        {
            _remote.Tick(UnityEngine.Time.unscaledDeltaTime, _scene == "Scene");

            NotePlayerActive();

            if (_net != null)
            {
                _net.Tick();
                SendLocalState();
                SyncGranny(UnityEngine.Time.unscaledDeltaTime);
                _catch.Tick(UnityEngine.Time.unscaledDeltaTime, FindRoot("Player"), _remote,
                    _scene == "Scene", _cfg.Spectate, _net.Connected,
                    (alive, cause) => _net.SendCatch(alive, cause));
            }

            if (_net != null && _cfg.CatchTest) CatchTestTick(UnityEngine.Time.unscaledDeltaTime);

            if (_net != null && _scene == "Scene")
            {
                var player = FindRoot("Player");
                _doors.Tick(UnityEngine.Time.unscaledDeltaTime, player, _cfg.Role, _net.SendDoor);
                _items.Tick(UnityEngine.Time.unscaledDeltaTime, player, _cfg.Role, _net.SendItem);
            }

            if (_cfg != null && _cfg.AutoPlay) AutoPlayTick();
            if (_cfg != null && _cfg.TestMove) TestMoveTick(UnityEngine.Time.unscaledDeltaTime);
            if (_cfg != null && _cfg.Probe) _probe.Tick(FindRoot("Player"), UnityEngine.Time.unscaledDeltaTime);
            TickSceneFollow();

            // Status on wall-clock 10 s boundaries, not a per-process
            // accumulator. Both test instances run on one machine, so this
            // makes host and guest print within milliseconds of each other
            // and the soak's dh/ih comparison is like-for-like. With an
            // accumulator the two lines carry the launch offset (~9 s in the
            // v0.4.4 run), and a door flip between them makes two identical
            // worlds read as a mismatch.
            int bucket = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond);
            if (bucket % 10 == 0 && bucket != _statusBucket)
            {
                _statusBucket = bucket;
                Status();
            }
        }

        public override void OnLateUpdate()
        {
            _catch.ApplyLate();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _scene = sceneName;
            _camT = null;
            _cc = null;
            _testMoveClock = 0f;
            _testMoveLogged = false;
            _sawPlayerActive = false;
            _remote.Destroy();
            _granny.Reset();
            _doors.Reset();
            _items.Reset();
            _probe.Reset();
            _catch.Reset();
            LoggerUtil.Log($"COOP scene -> '{sceneName}' (buildIndex={buildIndex})");
            if (_pendingScene == sceneName)
            {
                _pendingScene = null;
                _sceneFollows++;
                LoggerUtil.Log($"COOP scene follow: reached '{sceneName}' (follow #{_sceneFollows})");
            }
            if (_net != null && _cfg.Role == CoopRole.Host) _net.SendScene(sceneName);
        }

        public override void OnApplicationQuit()
        {
            _remote.Destroy();
            _net?.Dispose();
        }

        private void SendLocalState()
        {
            _sendAccum += UnityEngine.Time.unscaledDeltaTime;
            if (_sendAccum < 0.05f) return; // 20 Hz
            _sendAccum = 0f;

            byte flags = 0;
            float x = 0, y = 0, z = 0, yaw = 0, pitch = 0;

            var player = FindRoot("Player");
            if (player != null && player.activeSelf)
            {
                flags |= Protocol.FlagPlayerValid;
                // The body lives on the Player root; read the character controller's
                // transform when there is one so the position follows the physics
                // body rather than a possibly stationary parent.
                var body = BodyOf(player);
                var t = body != null ? body.transform : player.transform;
                var p = t.position;
                x = p.x; y = p.y; z = p.z;
                yaw = t.eulerAngles.y;

                if (_camT == null) _camT = player.transform.Find("CameraShakeAnim/CameraPivot/Main Camera");
                if (_camT != null)
                {
                    var e = _camT.eulerAngles;
                    pitch = e.x > 180f ? e.x - 360f : e.x;
                }

                if (!_sawPlayerActive)
                {
                    _sawPlayerActive = true;
                    LoggerUtil.Log($"COOP local player active at {p} yaw={yaw:F1}");
                }
            }

            if (FindRoot("PlayerInBed") != null) flags |= Protocol.FlagPlayerInBed;

            if (_net.SendState(flags, x, y, z, yaw, pitch)) _stateSent++;
        }

        /// <summary>
        /// Host: publish Granny's transform 20 times a second. Client: pin the
        /// local Granny to the host's copy and keep her NavMeshAgent off so the
        /// local AI cannot walk her somewhere else.
        /// </summary>
        private void SyncGranny(float dt)
        {
            if (_scene != "Scene")
            {
                _granny.ClearTarget();
                return;
            }

            if (!_granny.HasGranny)
            {
                _grannyBindAccum += dt;
                if (_grannyBindAccum < 0.5f) return;
                _grannyBindAccum = 0f;
                if (!_granny.TryBind(_cfg.Role == CoopRole.Join)) return;
            }

            if (_cfg.Role == CoopRole.Host)
            {
                _grannyAccum += dt;
                if (_grannyAccum < 0.05f) return; // 20 Hz
                _grannyAccum = 0f;

                var p = _granny.Position;
                byte flags = Protocol.FlagGrannyValid;
                try
                {
                    var ai = _granny.Native;
                    if (ai.GrannyMoving) flags |= Protocol.FlagGrannyMoving;
                    if (ai.attackingPlayer) flags |= Protocol.FlagGrannyAttacking;
                    if (ai.seePlayer) flags |= Protocol.FlagGrannySeesPlayer;
                }
                catch (Exception) { }

                if (_net.SendGranny(flags, p.x, p.y, p.z, _granny.Yaw)) _grannySent++;
            }
            else
            {
                _granny.Apply(dt);
            }
        }

        private void OnAppMessage(MsgReader r)
        {
            switch (r.Type)
            {
                case MsgType.State:
                    r.Byte(); // sender id, one peer per session in v1
                    var flags = r.Byte();
                    float x = r.Float(), y = r.Float(), z = r.Float(), yaw = r.Float(), pitch = r.Float();
                    _remote.SetTarget(flags, x, y, z, yaw, pitch);
                    _stateRecv++;
                    break;

                case MsgType.Scene:
                {
                    var scene = r.Str();
                    if (scene == _scene)
                    {
                        if (_pendingScene == scene) _pendingScene = null;
                        break;
                    }

                    // The host owns scene selection, so a guest follows it.
                    // The load is attempted in TickSceneFollow and guarded by a
                    // try/catch there: asking the engine in advance whether a
                    // name is loadable (Application.CanStreamedLevelBeLoaded)
                    // threw MissingMethodException on this IL2CPP build.
                    if (_cfg.Role != CoopRole.Join) break;
                    if (!_net.Connected) break;

                    if (_pendingScene != scene)
                    {
                        _pendingScene = scene;
                        _pendingSceneTries = 0;
                        // Short delay so the load does not start inside the
                        // socket callback, and so a burst of identical
                        // announcements does not keep pushing the deadline
                        // forward (a repeat leaves the deadline alone).
                        _pendingSceneAt = UnityEngine.Time.unscaledTime + 0.5f;
                        LoggerUtil.Log($"COOP scene follow: host entered '{scene}', loading it");
                    }
                    break;
                }

                case MsgType.Granny:
                {
                    if (_cfg.Role != CoopRole.Join) break;
                    var gflags = r.Byte();
                    float gx = r.Float(), gy = r.Float(), gz = r.Float(), gyaw = r.Float();
                    _granny.SetTarget(gflags, gx, gy, gz, gyaw);
                    _grannyRecv++;
                    break;
                }

                case MsgType.Door:
                    _doors.OnMessage(r, FindRoot("Player"), _cfg.Role, _net.SendDoor);
                    break;

                case MsgType.Item:
                    _items.OnMessage(r, _cfg.Role, _net.SendItem);
                    break;

                case MsgType.Catch:
                    _catch.OnMessage(r);
                    break;
            }
        }

        private void OnConnectionChanged(bool connected)
        {
            LoggerUtil.Log(connected
                ? $"COOP connected to '{_net.RemoteName}'"
                : "COOP disconnected");

            // Scene is announced once, when it loads. A client that joins while
            // the host is already in the gameplay scene never saw that message
            // (there was no peer to send it to), so re-announce on connect.
            if (connected && _cfg.Role == CoopRole.Host && !string.IsNullOrEmpty(_scene))
                _net.SendScene(_scene);
            if (connected) _catch.Resend((alive, cause) => _net.SendCatch(alive, cause));

            if (!connected)
            {
                _remote.ClearTarget();
                _granny.ClearTarget();
                _pendingScene = null;
            }
        }

        // A guest follows the host's scene. Called every frame so the load
        // happens outside the message handler (a scene load inside a socket
        // callback would run the whole OnSceneWasLoaded chain in that stack)
        // and so a failed or missed load is retried instead of being dropped.
        private void TickSceneFollow()
        {
            if (_pendingScene == null || _cfg == null || _cfg.Role != CoopRole.Join) return;
            if (_net == null || !_net.Connected) { _pendingScene = null; return; }

            // The host re-announces its scene as soon as a guest connects,
            // which is while the guest is still in SplashScreen. Loading then
            // would skip the game's own boot, so hold the request until the
            // game has reached a scene of its own; it fires on the transition.
            if (_scene == "" || _scene == "SplashScreen") return;
            if (UnityEngine.Time.unscaledTime < _pendingSceneAt) return;

            if (_scene == _pendingScene) { _pendingScene = null; return; }

            if (_pendingSceneTries >= 3)
            {
                LoggerUtil.Log($"COOP scene follow: gave up on '{_pendingScene}' after " +
                               $"{_pendingSceneTries} loads, we are still in '{_scene}'");
                _pendingScene = null;
                return;
            }

            _pendingSceneTries++;
            _pendingSceneAt = UnityEngine.Time.unscaledTime + 4f;
            LoggerUtil.Log($"COOP scene follow: loading '{_pendingScene}' (try {_pendingSceneTries}/3)");
            try
            {
                SceneManager.LoadScene(_pendingScene);
            }
            catch (Exception e)
            {
                LoggerUtil.Log($"COOP scene follow: load '{_pendingScene}' threw " +
                               $"{e.GetType().Name}: {e.Message}");
            }
        }

        private void NotePlayerActive()
        {
            if (_scene != "Scene") { _playerActiveKnown = false; return; }
            var player = FindRoot("Player");
            bool active = player != null && player.activeSelf;
            if (_playerActiveKnown && active == _playerActive) return;
            _playerActiveKnown = true;
            _playerActive = active;
            // Position, not FindRoot("PlayerInBed"): that root is present in the
            // scene whether or not the player is in bed, so the first version of
            // this line said inBed=True on every edge and proved nothing.
            string pos = "-";
            if (player != null)
            {
                try
                {
                    var p = player.transform.position;
                    pos = p.x.ToString("F1") + "," + p.y.ToString("F1") + "," + p.z.ToString("F1");
                }
                catch { }
            }
            LoggerUtil.Log($"COOP player active -> {active} (root={(player != null ? player.name : "-")} pos={pos})");
        }

        private void Status()
        {
            string local = "-";
            var player = FindRoot("Player");
            if (player != null && player.activeSelf)
            {
                var p = player.transform.position;
                local = $"{p.x:F1},{p.y:F1},{p.z:F1}";
            }

            var rt = _remote.HasTarget ? $"{_remote.Target.x:F1},{_remote.Target.y:F1},{_remote.Target.z:F1}" : "-";
            var gp = _granny.HasGranny ? $"{_granny.Position.x:F1},{_granny.Position.y:F1},{_granny.Position.z:F1}" : "-";
            var gt = _granny.HasTarget ? $"{_granny.Target.x:F1},{_granny.Target.y:F1},{_granny.Target.z:F1}" : "-";

            if (_net == null)
                LoggerUtil.Log($"COOP status idle scene='{_scene}' local={local}");
            else
                LoggerUtil.Log($"COOP status role={_net.Role} connected={_net.Connected} peer='{_net.RemoteName}' " +
                               $"scene='{_scene}' local={local} remote={rt} avVisible={_remote.IsVisible} " +
                               $"granny={gp} gRemote={gt} " +
                               $"sent={_stateSent} recv={_stateRecv} gSent={_grannySent} gRecv={_grannyRecv} " +
                               _doors.Status() + " " + _items.Status() + " " + _catch.StatusText +
                               " dh=" + _doors.StateFingerprint() +
                               " ih=" + _items.StateFingerprint());
        }

        private static GameObject FindRoot(string name)
        {
            try
            {
                var roots = SceneManager.GetActiveScene().GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                    if (roots[i].name == name) return roots[i];
            }
            catch (Exception) { }
            return null;
        }

        private CharacterController BodyOf(GameObject player)
        {
            if (_cc == null)
            {
                try { _cc = player.GetComponentInChildren<CharacterController>(); }
                catch (Exception) { }
            }
            return _cc;
        }

        // ---- QA-only movement driver ----------------------------------------

        private void TestMoveTick(float dt)
        {
            var player = FindRoot("Player");
            if (player == null || !player.activeSelf) return;

            var cc = BodyOf(player);
            if (cc == null || !cc.enabled) return;

            if (!_testMoveLogged)
            {
                _testMoveLogged = true;
                LoggerUtil.Log($"COOP testmove: walking '{cc.gameObject.name}' " +
                               $"(host +X / guest +Z, 4 s each way)");
            }

            _testMoveClock += dt;
            float phase = _testMoveClock % 8f;
            float sign = phase < 4f ? 1f : -1f;
            var axis = _cfg.Role == CoopRole.Host ? Vector3.right : Vector3.forward;
            cc.Move(axis * (sign * 0.8f * dt));
        }

        // ---- QA-only catch driver -------------------------------------------

        // Granny has to be close enough to grab the player, and a headless test
        // cannot walk there. Once a second, move the character body onto her.
        // It stops as soon as the game reports the catch, so the death sequence
        // runs undisturbed, and resumes if the player gets up again.
        private void CatchTestTick(float dt)
        {
            if (_scene != "Scene" || _catch.LocalCaught) { _catchTestAccum = 0f; return; }
            if (!_granny.HasGranny) return;

            _catchTestAccum += dt;
            if (_catchTestAccum < 1f) return;
            _catchTestAccum = 0f;

            var player = FindRoot("Player");
            if (player == null || !player.activeSelf) return;
            var cc = BodyOf(player);
            if (cc == null) return;

            try
            {
                var gp = _granny.Position;
                var dest = new Vector3(gp.x + 0.6f, gp.y + 0.02f, gp.z + 0.6f);
                bool wasEnabled = cc.enabled;
                cc.enabled = false;
                cc.transform.position = dest;
                cc.enabled = wasEnabled;
                LoggerUtil.Log($"COOP catchtest: moved the player onto Granny at {gp}");
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP catchtest FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---- test harness: drive the menu without a human -------------------

        private void AutoPlayTick()
        {
            if (_scene != "Menu")
            {
                // Reset on every visit, not once per process: a test player that
                // gets caught drops back to the menu, and a soak that stopped
                // driving the menu there spent 25 of its 30 minutes on the menu
                // screen (soak-v046, 11:27:34 onward).
                _menuFrames = -1;
                _autoContinued = false;
                _afterClick = 0;
                return;
            }

            if (_menuFrames < 0)
            {
                _menuFrames = 0;
                return;
            }

            _menuFrames++;
            if (_menuFrames == 240)
            {
                ClickByPathPart("Play");
            }
            else if (_menuFrames > 240)
            {
                _afterClick++;
                if (_afterClick == 120 && !_autoContinued)
                {
                    _autoContinued = true;
                    ClickByPathPart("Continue");
                    LoggerUtil.Log("COOP autoplay: menu driven to the gameplay scene");
                }
                else if (_afterClick > 120 && _afterClick % 240 == 0)
                {
                    // The menu did not advance: try the pair again instead of
                    // sitting on it for the rest of the run.
                    _autoContinued = false;
                    _playTries++;
                    LoggerUtil.Log("COOP autoplay: menu still up, retry #" + _playTries);
                    ClickByPathPart("Play");
                }
            }
        }

        private void ClickByPathPart(string part)
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(
                    UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    var b = all[i];
                    if (b == null) continue;
                    if (PathOf(b.transform).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        LoggerUtil.Log($"COOP autoplay: invoking '{PathOf(b.transform)}'");
                        b.onClick.Invoke();
                        return;
                    }
                }
                LoggerUtil.Log($"COOP autoplay: no button matching '{part}'");
            }
            catch (Exception e)
            {
                LoggerUtil.Log($"COOP autoplay FAILED: {e.GetType().Name}: {e.Message}");
            }
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
