using Il2Cpp;
using UnityEngine;

namespace GrannyCoop
{
    /// <summary>
    /// Host-authoritative Granny. The host keeps running the game's own AI and
    /// sends her transform 20 times a second. The client keeps its AI (so it
    /// still reacts to the local player and can catch them) but turns off the
    /// NavMeshAgent and pins the transform to the host's copy every frame, so
    /// both players see the same Granny in the same place.
    /// </summary>
    internal sealed class RemoteGranny
    {
        private EnemyAIGranny _granny;
        private bool _slaved;
        private bool _hasTarget;
        private Vector3 _target;
        private float _yaw;
        private byte _flags;
        private bool _bindLogged;

        public bool HasGranny => _granny != null;
        public bool HasTarget => _hasTarget;
        public Vector3 Target => _target;
        public byte Flags => _flags;
        public EnemyAIGranny Native => _granny;
        public float Yaw => _granny != null ? _granny.transform.eulerAngles.y : 0f;

        public Vector3 Position => _granny != null ? _granny.transform.position : Vector3.zero;

        /// <summary>
        /// Find the scene's EnemyAIGranny. On the client, also disable every
        /// NavMeshAgent under it once, so the AI cannot move her on its own.
        /// </summary>
        public bool TryBind(bool slave)
        {
            if (_granny != null) return true;

            try
            {
                var all = Object.FindObjectsByType<EnemyAIGranny>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (all == null || all.Length == 0) return false;
                _granny = all[0];
            }
            catch (System.Exception e)
            {
                LoggerUtil.Log("COOP granny bind FAILED: " + e.GetType().Name + ": " + e.Message);
                return false;
            }

            if (slave && !_slaved)
            {
                _slaved = true;
                Slave();
            }

            if (!_bindLogged)
            {
                _bindLogged = true;
                LoggerUtil.Log($"COOP granny bound at {_granny.transform.position} (slave={slave})");
            }
            return true;
        }

        private void Slave()
        {
            try
            {
                var agents = _granny.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true);
                int disabled = 0;
                for (int i = 0; i < agents.Length; i++)
                {
                    var a = agents[i];
                    if (a == null) continue;
                    if (a.enabled)
                    {
                        a.enabled = false;
                        disabled++;
                    }
                    // Stop the agent's own steering from writing the transform.
                    try { a.isStopped = true; } catch (System.Exception) { }
                }
                LoggerUtil.Log($"COOP granny: {agents.Length} NavMeshAgent(s) found, {disabled} disabled on the client");
            }
            catch (System.Exception e)
            {
                LoggerUtil.Log("COOP granny slave FAILED: " + e.GetType().Name + ": " + e.Message);
            }
        }

        public void SetTarget(byte flags, float x, float y, float z, float yaw)
        {
            _flags = flags;
            _hasTarget = (flags & Protocol.FlagGrannyValid) != 0;
            _target = new Vector3(x, y, z);
            _yaw = yaw;
        }

        public void ClearTarget() => _hasTarget = false;

        /// <summary>Client: follow the host's Granny. Called once per frame.</summary>
        public void Apply(float dt)
        {
            if (_granny == null || !_hasTarget) return;
            var t = _granny.transform;

            var p = t.position;
            // A gap wider than normal walking speed means a teleport in the game
            // (bed, car, cellar); snap rather than glide through walls/floors.
            float alpha = Vector3.Distance(p, _target) > 2.5f ? 1f : 1f - Mathf.Exp(-18f * dt);
            t.position = Vector3.Lerp(p, _target, alpha);

            var q = Quaternion.Euler(0f, _yaw, 0f);
            t.rotation = Quaternion.Slerp(t.rotation, q, 1f - Mathf.Exp(-12f * dt));
        }

        public void Reset()
        {
            _granny = null;
            _slaved = false;
            _bindLogged = false;
            _hasTarget = false;
            _flags = 0;
        }
    }
}
