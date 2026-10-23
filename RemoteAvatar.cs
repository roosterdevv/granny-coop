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

        private GameObject _root;
        private Transform _nose;
        private Renderer _rend;
        private Material _material;

        public bool HasTarget { get; private set; }
        public Vector3 Target { get; private set; }
        public float TargetYaw { get; private set; }
        public float TargetPitch { get; private set; }

        /// <summary>True when the avatar exists and a camera rendered it this frame.</summary>
        public bool IsVisible => _root != null && _root.activeSelf && _rend != null && _rend.isVisible;

        public void SetTarget(byte flags, float x, float y, float z, float yaw, float pitch)
        {
            HasTarget = (flags & Protocol.FlagPlayerValid) != 0;
            Target = new Vector3(x, y, z);
            TargetYaw = yaw;
            TargetPitch = pitch;
        }

        public void ClearTarget() => HasTarget = false;

        public void Tick(float dt, bool remoteInGameplayScene)
        {
            bool visible = HasTarget && remoteInGameplayScene;
            if (!visible)
            {
                if (_root != null) _root.SetActive(false);
                return;
            }

            if (_root == null) Create();

            if (!_root.activeSelf) _root.SetActive(true);

            float t = 1f - Mathf.Exp(-14f * dt);
            var p = _root.transform.position;
            p = Vector3.Lerp(p, Target, t);
            _root.transform.position = p;

            var q = Quaternion.Euler(0f, TargetYaw, 0f);
            _root.transform.rotation = Quaternion.Slerp(_root.transform.rotation, q, t);

            if (_nose != null)
                _nose.localRotation = Quaternion.Euler(TargetPitch, 0f, 0f);
        }

        private void Create()
        {
            _root = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _root.name = Name;

            var col = _root.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);

            _root.transform.position = Target;
            _root.transform.rotation = Quaternion.Euler(0f, TargetYaw, 0f);
            _root.transform.localScale = new Vector3(0.6f, 0.95f, 0.6f);

            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "nose";
            var ncol = nose.GetComponent<Collider>();
            if (ncol != null) Object.Destroy(ncol);
            nose.transform.SetParent(_root.transform, false);
            nose.transform.localPosition = new Vector3(0f, 0.65f, 0.85f);
            nose.transform.localScale = new Vector3(0.25f, 0.15f, 0.5f);
            _nose = nose.transform;

            Tint(new Color(0.25f, 0.65f, 1f, 1f));

            LoggerUtil.Log($"COOP avatar created at {Target}");
        }

        private void Tint(Color color)
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

                _rend = _root.GetComponent<Renderer>();
                if (_rend != null) _rend.material = _material;
            }
            catch (System.Exception e)
            {
                LoggerUtil.Log("COOP avatar tint FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            _root = null;
            _nose = null;
            _rend = null;
            _material = null;
            HasTarget = false;
        }
    }
}
