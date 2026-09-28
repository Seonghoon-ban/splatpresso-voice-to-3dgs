using System;
using GaussianSplatting.Runtime;
using SplatPresso.Placement;
using UnityEngine;

namespace SplatPresso.Extras
{
    /// <summary>
    /// Keyboard nudging of the last spawned object, used to calibrate the placement tuning:
    /// arrows move X/Z (world) by 0.05 m per press (Shift: 0.25 m), PageUp/PageDown move Y, [ / ] scale /1.05 or
    /// x1.05, comma / period yaw -5 / +5 degrees.
    /// </summary>
    /// <remarks>
    /// Logs the cumulative delta so the values found can be baked into <see cref="PlacementTuning"/> (this is how
    /// the calibrated yawOffsetDeg = 270 and uniformScaleFactor = 0.9 were found). Gizmos show the object's world
    /// bounds and forward ray. Keys are ignored while the HUD text box has focus.
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("SplatPresso/Extras/Placement Nudge Controller")]
    public sealed class PlacementNudgeController : MonoBehaviour
    {
        /// <summary>Spawn service whose last object is nudged; null = found automatically.</summary>
        [Tooltip("Empty = found automatically.")]
        public ObjectSpawnService spawnService;

        /// <summary>Raised after a nudge: (object root, "move" | "scale" | "rotate").</summary>
        public event Action<GameObject, string> Nudged;

        GameObject m_Tracked;
        Vector3 m_CumMove;
        float m_CumYaw;
        float m_CumScale = 1f;

        void Awake()
        {
            if (spawnService == null)
                spawnService = FindFirstObjectByType<ObjectSpawnService>();
        }

        GameObject Target
        {
            get
            {
                var last = spawnService != null ? spawnService.LastSpawned : null;
                return last != null ? last.gameObject : null;
            }
        }

        void Update()
        {
            GameObject go = Target;
            if (go == null)
                return;
            if (go != m_Tracked)
            {
                m_Tracked = go;
                m_CumMove = Vector3.zero;
                m_CumYaw = 0f;
                m_CumScale = 1f;
            }
            if (ExtrasInput.TextInputFocused)
                return;

            bool shift = InputCompat.GetKey(KeyCode.LeftShift) || InputCompat.GetKey(KeyCode.RightShift);
            float step = shift ? 0.25f : 0.05f;

            Vector3 move = Vector3.zero;
            if (InputCompat.GetKeyDown(KeyCode.LeftArrow)) move.x -= step;
            if (InputCompat.GetKeyDown(KeyCode.RightArrow)) move.x += step;
            if (InputCompat.GetKeyDown(KeyCode.UpArrow)) move.z += step;
            if (InputCompat.GetKeyDown(KeyCode.DownArrow)) move.z -= step;
            if (InputCompat.GetKeyDown(KeyCode.PageUp)) move.y += step;
            if (InputCompat.GetKeyDown(KeyCode.PageDown)) move.y -= step;

            float scaleMul = 1f;
            if (InputCompat.GetKeyDown(KeyCode.LeftBracket)) scaleMul /= 1.05f;
            if (InputCompat.GetKeyDown(KeyCode.RightBracket)) scaleMul *= 1.05f;

            float yaw = 0f;
            if (InputCompat.GetKeyDown(KeyCode.Comma)) yaw -= 5f;
            if (InputCompat.GetKeyDown(KeyCode.Period)) yaw += 5f;

            bool scaled = !Mathf.Approximately(scaleMul, 1f);
            if (move == Vector3.zero && !scaled && yaw == 0f)
                return;

            Transform t = go.transform;
            t.position += move;
            t.localScale *= scaleMul;
            t.Rotate(0f, yaw, 0f, Space.World);

            m_CumMove += move;
            m_CumScale *= scaleMul;
            m_CumYaw += yaw;
            var gen = go.GetComponent<GeneratedObject>();
            string rule = gen != null && gen.source?.placement != null ? gen.source.placement.rule : "-";
            Debug.Log($"[SplatPresso] Nudge cumulative: move={m_CumMove:F3} yaw={m_CumYaw:F1}deg scale x{m_CumScale:F3} rule={rule} " +
                      "(bake yaw into placement.yawOffsetDeg and scale into placement.uniformScaleFactor)");

            if (move != Vector3.zero) RaiseNudged(go, "move");
            if (scaled) RaiseNudged(go, "scale");
            if (yaw != 0f) RaiseNudged(go, "rotate");
        }

        void RaiseNudged(GameObject go, string kind)
        {
            try
            {
                Nudged?.Invoke(go, kind);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void OnDrawGizmos()
        {
            GameObject go = Target;
            if (go == null)
                return;

            var renderer = go.GetComponentInChildren<GaussianSplatRenderer>();
            if (renderer != null && renderer.asset != null)
            {
                Vector3 bmin = renderer.asset.boundsMin, bmax = renderer.asset.boundsMax;
                Vector3 wmin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 wmax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                for (int i = 0; i < 8; ++i)
                {
                    var c = new Vector3(
                        (i & 1) == 0 ? bmin.x : bmax.x,
                        (i & 2) == 0 ? bmin.y : bmax.y,
                        (i & 4) == 0 ? bmin.z : bmax.z);
                    Vector3 w = renderer.transform.TransformPoint(c);
                    wmin = Vector3.Min(wmin, w);
                    wmax = Vector3.Max(wmax, w);
                }
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireCube((wmin + wmax) * 0.5f, wmax - wmin);
            }
            else
            {
                var renderers = go.GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    Bounds b = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; ++i)
                        b.Encapsulate(renderers[i].bounds);
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawWireCube(b.center, b.size);
                }
            }

            Gizmos.color = Color.yellow;
            Gizmos.DrawRay(go.transform.position, go.transform.forward);
        }
    }
}
