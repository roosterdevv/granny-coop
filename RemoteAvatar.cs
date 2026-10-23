using UnityEngine;

namespace GrannyCoop
{
    /// <summary>
    /// The other player as seen in this client's world. The game's Player prefab
    /// has no body mesh (first-person only, with a fake shadow), so v1 draws a
    /// neutral capsule with a nose cube and no collider: purely visual, it never
    /// pushes the local character or touches the game's physics.
    /// </summary>
    internal sealed class RemoteAvatar
    {
        private const string Name = "GrannyCoop_RemotePlayer";

        // Standing height and the crouch height the capsule used to have on
        // screen before v0.7.0 (it sat half a metre into the floor, so what was
        // visible was a ~0.95 m stub; that stub is now the crouch pose).
        private const float StandHeight = 1.85f;
        private const float CrouchHeight = 0.95f;

        // The capsule primitive is 2 units tall, so its y scale is height / 2
        // and its centre sits half a height above the reported feet position.
        private const float BaseCapsuleHeight = 2f;

        private GameObject _root;
        private Transform _body;
        private Transform _nose;
        private Transform _hand;
        private Renderer _rend;
        private Material _material;
        private bool _remoteDead;
        private float _height = StandHeight;

        // The item the peer is holding (v0.7.0). The source is the peer's own
        // world model on this end, kept hidden by ItemSync; the clone is parented
        // to the avatar's hand so it follows without colliders or game scripts.
        private GameObject _heldSource;
        private GameObject _held;
        private string _heldName;

        public bool HasTarget { get; private set; }
        public Vector3 Target { get; private set; }
        public float TargetYaw { get; private set; }
        public float TargetPitch { get; private set; }
        public bool TargetCrouch { get; private set; }

        /// <summary>True when the avatar exists and a camera rendered it this frame.</summary>
        public bool IsVisible => _root != null && _root.activeSelf && _rend != null && _rend.isVisible;

        /// <summary>The spectator camera sits inside this avatar's head, so the
        /// capsule and nose have to go while it is following. Set by CatchSync.</summary>
        public bool FirstPersonHidden { get; set; }

        /// <summary>True while the peer reports itself dead. The body is with
        /// Granny (or in the bed), so the standing avatar is hidden until the
        /// peer announces it is alive again. Set by CatchSync.</summary>
        public bool RemoteDead
        {
            get => _remoteDead;
            set
            {
                if (value == _remoteDead) return;
                _remoteDead = value;
                LoggerUtil.Log(value
                    ? "COOP avatar: hidden, the peer is dead"
                    : "COOP avatar: shown again, the peer is alive");
            }
        }

        public void SetTarget(byte flags, float x, float y, float z, float yaw, float pitch)
        {
            HasTarget = (flags & Protocol.FlagPlayerValid) != 0;
            TargetCrouch = (flags & Protocol.FlagPlayerCrouch) != 0;
            Target = new Vector3(x, y, z);
            TargetYaw = yaw;
            TargetPitch = pitch;
        }

        public void ClearTarget() => HasTarget = false;

        public void Tick(float dt, bool remoteInGameplayScene)
        {
            bool visible = HasTarget && remoteInGameplayScene && !FirstPersonHidden && !RemoteDead;
            if (!visible)
            {
                if (_root != null) _root.SetActive(false);
                return;
            }

            if (_root == null) Create();

            if (!_root.activeSelf) _root.SetActive(true);

            // v0.7.0: the peer reports its feet, so the capsule is centred half a
            // height above that point instead of being buried up to its waist.
            float want = TargetCrouch ? CrouchHeight : StandHeight;
            if (!Mathf.Approximately(_height, want))
            {
                _height = Mathf.Lerp(_height, want, 1f - Mathf.Exp(-10f * dt));
                if (Mathf.Abs(_height - want) < 0.01f) _height = want;
                ApplyHeight();
            }

            float t = 1f - Mathf.Exp(-14f * dt);
            var p = _root.transform.position;
            p = Vector3.Lerp(p, Target + Vector3.up * (_height * 0.5f), t);
            _root.transform.position = p;

            var q = Quaternion.Euler(0f, TargetYaw, 0f);
            _root.transform.rotation = Quaternion.Slerp(_root.transform.rotation, q, t);

            if (_nose != null)
                _nose.localRotation = Quaternion.Euler(TargetPitch, 0f, 0f);

            if (_held != null && _heldSource == null) ClearHeldItem();
        }

        /// <summary>Height of the capsule in metres; the root is at its centre.</summary>
        private void ApplyHeight()
        {
            if (_body != null)
                _body.localScale = new Vector3(0.6f, _height / BaseCapsuleHeight, 0.6f);
            if (_nose != null)
                _nose.localPosition = new Vector3(0f, _height * 0.82f, 0.30f);
            if (_hand != null)
                _hand.localPosition = new Vector3(0.30f, _height * 0.60f, 0.30f);
        }

        private void Create()
        {
            // An empty root at the capsule's centre, so scaling the body does not
            // also scale the nose and the hand anchor.
            _root = new GameObject(Name);
            _root.transform.position = Target + Vector3.up * (_height * 0.5f);
            _root.transform.rotation = Quaternion.Euler(0f, TargetYaw, 0f);

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "body";
            var col = body.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            body.transform.SetParent(_root.transform, false);
            _body = body.transform;
            _rend = body.GetComponent<Renderer>();

            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "nose";
            var ncol = nose.GetComponent<Collider>();
            if (ncol != null) Object.Destroy(ncol);
            nose.transform.SetParent(_root.transform, false);
            nose.transform.localScale = new Vector3(0.18f, 0.14f, 0.30f);
            _nose = nose.transform;

            var hand = new GameObject("hand");
            hand.transform.SetParent(_root.transform, false);
            _hand = hand.transform;

            ApplyHeight();
            Tint(new Color(0.25f, 0.65f, 1f, 1f), body.GetComponent<Renderer>());

            LoggerUtil.Log($"COOP avatar created at feet {Target}, height {_height:0.00}");
        }

        /// <summary>
        /// Show the item the peer picked up in the peer's hand (v0.7.0). The
        /// clone is visual only: colliders are disabled and the game's scripts
        /// are switched off, so it can never be picked up or move on its own.
        /// </summary>
        public void SetHeldItem(GameObject source, string itemName)
        {
            if (source == null) { ClearHeldItem(); return; }
            if (_held != null && _heldSource == source && _heldName == itemName) return;
            ClearHeldItem();
            _heldSource = source;
            _heldName = itemName;
            try
            {
                _held = Object.Instantiate(source);
                _held.name = "GrannyCoop_Held_" + (itemName ?? "item");
                foreach (var c in _held.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                foreach (var rb in _held.GetComponentsInChildren<Rigidbody>(true))
                {
                    rb.detectCollisions = false;
                    rb.isKinematic = true;
                }
                foreach (var mb in _held.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null) mb.enabled = false;

                _held.transform.SetParent(_hand, false);
                _held.transform.localRotation = Quaternion.identity;
                _held.transform.localPosition = -LocalCenter(_held);
                LoggerUtil.Log($"COOP avatar: peer holds {itemName ?? "?"}");
            }
            catch (System.Exception e)
            {
                LoggerUtil.Log("COOP avatar held item FAILED: " + e.GetType().Name + ": " + e.Message);
                _held = null;
            }
        }

        public void ClearHeldItem()
        {
            if (_held != null)
            {
                LoggerUtil.Log($"COOP avatar: peer no longer holds {_heldName ?? "?"}");
                Object.Destroy(_held);
            }
            _held = null;
            _heldSource = null;
            _heldName = null;
        }

        /// <summary>Clear only if the avatar is showing this source: the peer
        /// may have dropped one item and picked up another, and the newer clone
        /// must not be thrown away by a late message for the older one.</summary>
        public void ClearHeldItem(GameObject source)
        {
            if (source == null) { ClearHeldItem(); return; }
            if (_heldSource != source) return;
            ClearHeldItem();
        }

        /// <summary>Centre of the clone's renderers, in the clone's own space.</summary>
        private static Vector3 LocalCenter(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return Vector3.zero;
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return go.transform.InverseTransformPoint(b.center);
        }

        private void Tint(Color color, Renderer target)
        {
            try
            {
                var shader = Shader.Find("Standard");
                if (shader == null) shader = Shader.Find("Unlit/Color");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    LoggerUtil.Log("COOP avatar tint skipped: no suitable shader in build");
                    return;
                }

                _material = new Material(shader);
                if (_material.HasProperty("_Color")) _material.color = color;

                if (target != null) target.material = _material;
            }
            catch (System.Exception e)
            {
                LoggerUtil.Log("COOP avatar tint FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        public void Destroy()
        {
            ClearHeldItem();
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _body = null;
            _nose = null;
            _hand = null;
            _rend = null;
            _material = null;
            _remoteDead = false;
            HasTarget = false;
            _height = StandHeight;
        }
    }
}
