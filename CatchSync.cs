using System;
using Il2Cpp;
using UnityEngine;

namespace GrannyCoop
{
    /// <summary>
    /// Catch/death sync and the post-death spectator view.
    ///
    /// The game's catch state lives on the scene's playerCaught component: five
    /// booleans, one per way the player can be taken (Granny grabs you, a spider
    /// bites you, you explode, you fall, Mom Spider eats you). This polls them,
    /// sends an edge message when the local player is caught or gets up again,
    /// and, while the local player is caught, puts a camera at the other
    /// player's head so the dead player keeps watching instead of the game's
    /// death camera.
    ///
    /// The spectator camera is applied in LateUpdate: the game's own camera
    /// scripts move their cameras in Update, so writing the transform later in
    /// the frame is what keeps the view on the survivor.
    /// </summary>
    internal sealed class CatchSync
    {
        public const byte CauseNone = 0;
        public const byte CauseGranny = 1;
        public const byte CauseSpider = 2;
        public const byte CauseExplode = 3;
        public const byte CauseFall = 4;
        public const byte CauseMomSpider = 5;

        private const float EyeHeight = 1.55f;
        private const float BindIntervalSec = 1f;
        private const float PollIntervalSec = 0.1f;

        // playerCaught's flags are an edge: they drop about a second after the
        // grab, while the death sequence runs on. Granny then carries the body
        // to the bed before playerDead.startEndScene runs, which was ~30 s
        // later in the QA runs. Stay dead until the end sequence is over and
        // the flags have been clear for this long, so the spectator view
        // covers the grab, the carry and the game-over screen.
        private const float DeathHoldSec = 45f;

        private playerCaught _caught;
        private playerDead _dead;
        private bool _readFailed;

        private bool _known;
        private bool _localCaught;
        private byte _cause;

        private bool _remoteKnown;
        private bool _remoteCaught;
        private byte _remoteCause;

        private float _bindAccum;
        private float _pollAccum;
        private float _logAccum;
        private float _aliveAccum;

        private bool _endSeq;
        private bool _endSeqLogged;

        // Death overlay (black fade / blood), hidden only while spectating.
        private UnityEngine.UI.Image _blackScreen;
        private bool _overlayWasEnabled;
        private bool _overlayHidden;
        private bool _overlayLogged;

        // Spectator camera. _camT is the transform we write; _ownCam is only
        // created when the scene has no active camera at all (the death
        // sequence can switch the player camera off).
        private Camera _cam;
        private Transform _camT;
        private GameObject _ownCam;
        private bool _usingOwnCam;
        private bool _camSaved;
        private Vector3 _camSavedLocalPos;
        private Quaternion _camSavedLocalRot;
        private string _camName = "-";
        private bool _camLogged;

        private Vector3 _pos;
        private bool _posKnown;
        private Vector3 _applyPos;
        private Quaternion _applyRot;
        private bool _apply;

        public bool LocalCaught => _localCaught;
        public bool RemoteCaught => _remoteCaught;
        public bool RemoteKnown => _remoteKnown;
        public bool Spectating { get; private set; }
        public bool Bound => _caught != null;

        public string CauseName => _cause == CauseNone
            ? (_localCaught ? "dead" : "none")
            : CauseNameOf(_cause);
        public string RemoteCauseName => CauseNameOf(_remoteCause);

        public string StatusText =>
            $"catch={(LocalCaught ? CauseName : "alive")} " +
            $"rcatch={(_remoteKnown ? (RemoteCaught ? RemoteCauseName : "alive") : "?")} " +
            $"spec={Spectating} cam='{_camName}'";

        public void Tick(float dt, GameObject player, RemoteAvatar remote, bool inScene,
                         bool forceSpectate, bool connected, Action<byte, byte> send)
        {
            if (!inScene)
            {
                StopSpectating(remote);
                return;
            }

            if (_caught == null || _dead == null)
            {
                _bindAccum += dt;
                if (_bindAccum >= BindIntervalSec)
                {
                    _bindAccum = 0f;
                    TryBind(player);
                }
            }

            if (_caught != null)
            {
                _pollAccum += dt;
                if (_pollAccum >= PollIntervalSec)
                {
                    _pollAccum = 0f;
                    Poll(connected ? send : null);
                }
            }

            bool want = (_localCaught || forceSpectate) && connected &&
                        remote != null && remote.HasTarget;
            if (want) Spectate(dt, remote);
            else StopSpectating(remote);
        }

        /// <summary>
        /// Called from LateUpdate so the transform write lands after the game's
        /// own camera controllers.
        /// </summary>
        public void ApplyLate()
        {
            if (!_apply || _camT == null) return;
            _apply = false;
            try { _camT.SetPositionAndRotation(_applyPos, _applyRot); }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP spectate: camera write failed: " + e.GetType().Name + ": " + e.Message);
                _apply = false;
            }
        }

        public void OnMessage(MsgReader r)
        {
            byte alive = r.Byte();
            byte cause = r.Byte();
            bool caught = alive == 0;
            if (_remoteKnown && caught == _remoteCaught) return;

            _remoteKnown = true;
            _remoteCaught = caught;
            _remoteCause = caught ? cause : CauseNone;
            LoggerUtil.Log(caught
                ? $"COOP catch: remote player caught ({CauseNameOf(cause)})"
                : "COOP catch: remote player is alive again");
        }

        /// <summary>Re-announce our state after a (re)connect: the peer may have
        /// missed the edge that happened before it was listening.</summary>
        public void Resend(Action<byte, byte> send)
        {
            if (send == null || !_known || _caught == null) return;
            if (_localCaught) send(0, _cause);
        }

        public void Reset()
        {
            StopSpectating(null);
            _caught = null;
            _dead = null;
            _readFailed = false;
            _known = false;
            _localCaught = false;
            _cause = CauseNone;
            _remoteKnown = false;
            _remoteCaught = false;
            _remoteCause = CauseNone;
            _bindAccum = 0f;
            _pollAccum = 0f;
            _logAccum = 0f;
            _aliveAccum = 0f;
            _endSeq = false;
            _endSeqLogged = false;
            _blackScreen = null;
            _overlayWasEnabled = false;
            _overlayHidden = false;
            _overlayLogged = false;
            _cam = null;
            _camT = null;
            _ownCam = null;
            _usingOwnCam = false;
            _camSaved = false;
            _camName = "-";
            _camLogged = false;
        }

        // ---- catch state ----------------------------------------------------

        private void TryBind(GameObject player)
        {
            if (_caught == null)
            {
                try
                {
                    if (player != null)
                        _caught = player.GetComponentInChildren<playerCaught>(true);

                    if (_caught == null)
                    {
                        // Fallback: the component may sit on a manager root rather
                        // than under Player.
                        var all = UnityEngine.Object.FindObjectsByType<playerCaught>(
                            FindObjectsInactive.Include, FindObjectsSortMode.None);
                        if (all != null && all.Length > 0) _caught = all[0];
                    }

                    if (_caught != null)
                        LoggerUtil.Log($"COOP catch: bound playerCaught on '{_caught.gameObject.name}'");
                }
                catch (Exception e)
                {
                    LoggerUtil.Log("COOP catch bind FAILED: " + e.GetType().Name + ": " + e.Message);
                }
            }

            // playerDead runs the game-over sequence; its endSceneRunning flags
            // outlast playerCaught's, which is what the death view hangs on.
            if (_dead == null)
            {
                try
                {
                    if (player != null)
                        _dead = player.GetComponentInChildren<playerDead>(true);

                    if (_dead == null)
                    {
                        var all = UnityEngine.Object.FindObjectsByType<playerDead>(
                            FindObjectsInactive.Include, FindObjectsSortMode.None);
                        if (all != null && all.Length > 0) _dead = all[0];
                    }

                    if (_dead != null)
                        LoggerUtil.Log($"COOP death: bound playerDead on '{_dead.gameObject.name}'" +
                                       $" end=({_dead.endSceneRunning},{_dead.endSceneRunning2},{_dead.endSceneRunning3})");
                }
                catch (Exception e)
                {
                    LoggerUtil.Log("COOP death bind FAILED: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        /// <summary>True while the game's death/game-over sequence is running.</summary>
        private bool ReadEndSeq()
        {
            if (_dead == null) return false;
            try
            {
                bool on = _dead.endSceneRunning || _dead.endSceneRunning2 || _dead.endSceneRunning3;
                if (on != _endSeq || !_endSeqLogged)
                {
                    _endSeq = on;
                    _endSeqLogged = true;
                    LoggerUtil.Log(on
                        ? "COOP death: game-over sequence running"
                        : "COOP death: game-over sequence finished");
                }
                return on;
            }
            catch (Exception e)
            {
                _dead = null;
                LoggerUtil.Log("COOP death read FAILED: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private void Poll(Action<byte, byte> send)
        {
            byte cause = CauseNone;
            bool caught;
            try
            {
                if (_caught.grannyTakePlayer) cause = CauseGranny;
                else if (_caught.spiderBitePlayer) cause = CauseSpider;
                else if (_caught.explodingPlayer) cause = CauseExplode;
                else if (_caught.playerFallDead) cause = CauseFall;
                else if (_caught.momSpiderEat) cause = CauseMomSpider;
                caught = cause != CauseNone;
            }
            catch (Exception e)
            {
                if (!_readFailed)
                {
                    _readFailed = true;
                    LoggerUtil.Log("COOP catch read FAILED: " + e.GetType().Name + ": " + e.Message);
                }
                _caught = null;
                return;
            }

            bool endSeq = ReadEndSeq();
            bool deadNow = caught || endSeq;

            if (!_known)
            {
                _known = true;
                _localCaught = deadNow;
                _cause = caught ? cause : CauseNone;
                if (deadNow)
                {
                    LoggerUtil.Log($"COOP catch: local player is already caught ({CauseNameOf(_cause)})");
                    send?.Invoke(0, _cause);
                }
                return;
            }

            if (deadNow)
            {
                _aliveAccum = 0f;
                HideDeathOverlay();
                if (_localCaught) return;

                _localCaught = true;
                _cause = caught ? cause : CauseNone;
                LoggerUtil.Log(caught
                    ? $"COOP catch: local player caught ({CauseNameOf(cause)})"
                    : "COOP catch: local death sequence started");
                send?.Invoke(0, _cause);
                return;
            }

            if (!_localCaught) return;

            // Flags and end scene are both clear. Give the game a few seconds to
            // start its end sequence before calling the player alive again: the
            // grab flag drops before the game-over coroutine begins.
            _aliveAccum += PollIntervalSec;
            if (_aliveAccum < DeathHoldSec) return;

            _aliveAccum = 0f;
            _localCaught = false;
            LoggerUtil.Log("COOP catch: local player is alive again");
            send?.Invoke(1, _cause);
            _cause = CauseNone;
        }

        /// <summary>
        /// The game's death screen is screen-space UI and would draw over the
        /// spectator camera, so hide the black fade (and the blood overlay)
        /// while the local player is dead. Re-applied every poll because the
        /// sequence can switch it back on.
        /// </summary>
        private void HideDeathOverlay()
        {
            if (_dead == null) return;
            try
            {
                if (_blackScreen == null) _blackScreen = _dead.blackScreenTexture;
                if (_blackScreen != null && _blackScreen.enabled)
                {
                    if (!_overlayHidden) _overlayWasEnabled = true;
                    _overlayHidden = true;
                    _blackScreen.enabled = false;
                }

                var blood = _dead.bloodscreenEnd;
                if (blood != null && blood.activeSelf)
                {
                    blood.SetActive(false);
                    _overlayHidden = true;
                }

                if (_overlayHidden && !_overlayLogged)
                {
                    _overlayLogged = true;
                    LoggerUtil.Log("COOP death: hid the death overlay so the spectator view is visible");
                }
            }
            catch (Exception e)
            {
                LoggerUtil.Log("COOP death: overlay hide FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void RestoreDeathOverlay()
        {
            if (!_overlayHidden) return;
            _overlayHidden = false;
            _overlayLogged = false;
            try
            {
                if (_blackScreen != null && _overlayWasEnabled) _blackScreen.enabled = true;
            }
            catch (Exception) { }
            _overlayWasEnabled = false;
        }

        private static string CauseNameOf(byte cause)
        {
            switch (cause)
            {
                case CauseGranny: return "granny";
                case CauseSpider: return "spider";
                case CauseExplode: return "explode";
                case CauseFall: return "fall";
                case CauseMomSpider: return "momspider";
                default: return "none";
            }
        }

        // ---- spectator camera ----------------------------------------------

        private void Spectate(float dt, RemoteAvatar remote)
        {
            if (!Spectating)
            {
                Spectating = true;
                _posKnown = false;
                _camLogged = false;
                LoggerUtil.Log(_localCaught
                    ? "COOP spectate: watching the other player"
                    : "COOP spectate: forced follow (QA)");
            }

            if (!remote.FirstPersonHidden) remote.FirstPersonHidden = true;

            var cam = EnsureCam();
            if (cam == null) return;

            // The remote state is the body pivot; a first-person view sits at
            // the other player's eye height. Snap on a teleport (bed, car,
            // scene change) instead of gliding across the map.
            var target = remote.Target + new Vector3(0f, EyeHeight, 0f);
            if (!_posKnown || Vector3.Distance(target, _pos) > 8f)
            {
                _pos = target;
                _posKnown = true;
            }
            else
            {
                _pos = Vector3.Lerp(_pos, target, 1f - Mathf.Exp(-10f * dt));
            }

            _applyPos = _pos;
            _applyRot = Quaternion.Euler(remote.TargetPitch, remote.TargetYaw, 0f);
            _apply = true;

            _logAccum += dt;
            if (_logAccum >= 1f)
            {
                _logAccum = 0f;
                LoggerUtil.Log($"COOP spectate: cam='{_camName}' at {Fmt(_pos)} " +
                               $"target={Fmt(remote.Target)} yaw={remote.TargetYaw:F1} pitch={remote.TargetPitch:F1}");
            }
        }

        private Camera EnsureCam()
        {
            if (_cam != null && _cam.enabled && _cam.gameObject.activeInHierarchy)
                return _cam;

            // Prefer a camera that is already rendering the scene: its layers,
            // culling mask and post effects are the game's own.
            _cam = Camera.main;
            if (_cam != null && !_cam.enabled)
                _cam = null;

            if (_cam == null)
            {
                try
                {
                    var all = UnityEngine.Object.FindObjectsByType<Camera>(
                        FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null && all[i].enabled)
                        {
                            _cam = all[i];
                            break;
                        }
                    }
                }
                catch (Exception) { }
            }

            if (_cam != null && !_usingOwnCam)
            {
                var t = _cam.transform;
                if (t != _camT)
                {
                    // The previous camera went away (the death sequence swaps
                    // cameras); give it its transform back before taking this one.
                    RestoreSavedCam();
                    _camT = t;
                    _camSaved = true;
                    _camSavedLocalPos = t.localPosition;
                    _camSavedLocalRot = t.localRotation;
                }
                _camName = _cam.gameObject.name;
                LogCam();
                return _cam;
            }

            // Nothing is rendering: make our own. The game's death sequence can
            // deactivate the player camera, and then there is no camera to
            // borrow.
            if (_ownCam == null)
            {
                RestoreSavedCam();
                _camT = null;
                try
                {
                    _ownCam = new GameObject("GrannyCoop_SpectatorCam");
                    _cam = _ownCam.AddComponent<Camera>();
                    _cam.depth = 100f;
                    _cam.nearClipPlane = 0.05f;
                    _usingOwnCam = true;
                    _camT = _ownCam.transform;
                    _camName = "GrannyCoop_SpectatorCam";
                    LoggerUtil.Log("COOP spectate: no active camera, created GrannyCoop_SpectatorCam");
                }
                catch (Exception e)
                {
                    LoggerUtil.Log("COOP spectate: own camera FAILED: " + e.GetType().Name + ": " + e.Message);
                    _ownCam = null;
                    _cam = null;
                    return null;
                }
            }
            else
            {
                _ownCam.SetActive(true);
                _cam = _ownCam.GetComponent<Camera>();
                _camT = _ownCam.transform;
            }

            LogCam();
            return _cam;
        }

        private void LogCam()
        {
            if (_camLogged) return;
            _camLogged = true;
            LoggerUtil.Log($"COOP spectate: using camera '{_camName}'");
        }

        private void StopSpectating(RemoteAvatar remote)
        {
            if (!Spectating)
            {
                // A reset/destroy can drop the avatar flag without a session.
                if (remote != null && remote.FirstPersonHidden) remote.FirstPersonHidden = false;
                return;
            }

            Spectating = false;
            _apply = false;
            _posKnown = false;
            _logAccum = 0f;
            RestoreDeathOverlay();

            if (remote != null && remote.FirstPersonHidden) remote.FirstPersonHidden = false;

            if (_usingOwnCam)
            {
                try { if (_ownCam != null) _ownCam.SetActive(false); }
                catch (Exception) { }
            }
            else if (_camT != null && _camSaved)
            {
                // Put the game's camera back where we found it; its controller
                // takes over again from there.
                RestoreSavedCam();
            }

            _camSaved = false;
            _cam = null;
            _camT = null;
            _camName = "-";
            _camLogged = false;
            LoggerUtil.Log("COOP spectate: stopped");
        }

        private void RestoreSavedCam()
        {
            if (_camT == null || !_camSaved) return;
            try { _camT.localPosition = _camSavedLocalPos; _camT.localRotation = _camSavedLocalRot; }
            catch (Exception) { }
            _camSaved = false;
        }

        private static string Fmt(Vector3 v) => $"({v.x:F1},{v.y:F1},{v.z:F1})";
    }
}
