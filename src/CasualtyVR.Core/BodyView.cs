using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CasualtyVR
{
    /// <summary>
    /// First person (FP-5): your own torso and legs when you look down, as the game draws and animates them, with
    /// everything they show: blood and wounds (the limb materials), bandages, splints and clothing (child sprites),
    /// broken or dislocated limbs (the game's pose) and missing ones (hidden with the original).
    ///
    /// A flat side-view sprite seen from above is a line, so each sprite is drawn as a pixel-extruded mesh
    /// (ObjectExtruder.MeshFor, like crates and capsules) with some body depth: the torso thickest, the legs
    /// thinner, attachments a hair outside their limb. The copies sit exactly where the game's limbs are (its
    /// walk cycle, crouch and lean come through) on the VR-only layer; the originals stay hidden in the eyes
    /// (FirstPerson.OwnBody). The head stays hidden (your eyes are in it) and the arms are VrArms.
    /// </summary>
    internal sealed class BodyView
    {
        // Body.limbs: UpTorso 1, DownTorso 2, ThighF 9, CrusF 10, FootF 11, ThighB 12, CrusB 13, FootB 14.
        private static readonly int[] Parts = { 1, 2, 9, 10, 11, 12, 13, 14 };
        // Depth across the view (z) is the body's width from side to side: shoulders and hips wide, the two legs
        // side by side within them (user, 2026-10-06: "I only appear to have one leg": both legs were at z = 0,
        // one inside the other, as in the side view).
        private static readonly float[] DepthBlocks = { 1.15f, 1.05f, 0.5f, 0.42f, 0.42f, 0.5f, 0.42f, 0.42f };
        private static readonly float[] SideBlocks = { 0f, 0f, -0.3f, -0.3f, -0.3f, 0.3f, 0.3f, 0.3f };

        private sealed class Piece
        {
            public SpriteRenderer Src;
            public GameObject Go;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Sprite Built;
            public float Depth;
            public float Side;    // z offset in blocks: the front leg one side, the back leg the other
            public bool Torso;    // turns with the spine (else with the legs)
        }

        private readonly GameObject root;
        private readonly List<Piece> pieces = new List<Piece>();
        private readonly MaterialPropertyBlock props = new MaterialPropertyBlock();
        private Body built;
        private int lastScan = -1000;
        private float restGap = -1f;   // standing still: head height above the upper torso

        public BodyView()
        {
            root = new GameObject("CasualtyVR.BodyView") { hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(root);
        }

        public void SetActive(bool on)
        {
            if (root.activeSelf != on) root.SetActive(on);
        }

        public void Update(Body body)
        {
            if (body == null || body.limbs == null || body.limbs.Length < 15) { SetActive(false); return; }
            if (body != built || Scans.Due(lastScan, 30, 6)) { long t0 = Scans.Start(); Rescan(body); Scans.Stop("body view", t0); }
            float s = PopupDepth.Scale * Plugin.FpBodyDepth.Value;
            Vector3 back = Vector3.zero;
            if (Plugin.FpBodyBack.Value != 0f)
            {
                // The body behind the eye: the character's eyes are in its head, forward of the torso, but the view
                // keeps to the body's centre line (FirstPerson.Update), so drawn where the game has it the torso
                // filled the view below and ahead (user, 2026-10-05: "too far forward, it really gets in the way").
                Vector3 f = new Vector3(FirstPerson.FacingRightNow ? 1f : -1f, 0f, 0f);
                if (f.sqrMagnitude > 1e-4f) back = -f.normalized * Plugin.FpBodyBack.Value * VrSpace.Scale;
            }

            // Standing, the drawn body stays upright under the eyes (user, 2026-10-06: running from a stop, the view
            // was sometimes blocked by the body). The game leans the whole figure about the lower torso when it
            // speeds up or slows down (Body.accelRot, up to tens of degrees with weak legs), and the spine further
            // towards where it looks, attacks and ducks; drawn as is, the upper torso swung forward into the view.
            // Your own body does not lean with the character's animation, so here the spine is set upright and the
            // legs lose the acceleration lean. The view is smoothed (FirstPerson.Smoothing) and trails the character
            // as it sets off, so the body is also kept under the view's anchor, and never higher than it stands.
            var pivot = body.limbs[2].transform.position;
            Quaternion torsoFix = Quaternion.identity, legFix = Quaternion.identity;
            Vector3 shift = back;
            if (body.standing && body.limbs[1] != null)
            {
                Vector2 spine = body.limbs[1].transform.position - pivot;
                if (spine.sqrMagnitude > 1e-4f) torsoFix = Quaternion.Euler(0f, 0f, -Vector2.SignedAngle(Vector2.up, spine));
                legFix = Quaternion.Euler(0f, 0f, -body.accelRot);
                var anchor = VrSpace.Anchor;
                shift.x += anchor.x - body.transform.position.x;
                float gap = body.limbs[0].transform.position.y - body.limbs[1].transform.position.y;
                bool still = body.grounded && !body.crouching && body.crouchAmount < 0.05f && Mathf.Abs(body.accelRot) < 1f;   // highest when upright, looking level
                if (still && gap > 0.2f && gap < 6f) restGap = Mathf.Max(restGap, gap);
                if (restGap > 0f)
                {
                    float upY = pivot.y + spine.magnitude;   // the upright upper torso
                    shift.y += Mathf.Min(0f, anchor.y - restGap - upY);
                }
            }
            DebugLean(body, pivot, torsoFix, shift);
            foreach (var p in pieces)
            {
                var src = p.Src;
                bool show = src != null && src.enabled && src.gameObject.activeInHierarchy && src.sprite != null;
                if (show && p.Built != src.sprite)
                {
                    var mesh = ObjectExtruder.MeshFor(src.sprite);
                    p.Filter.sharedMesh = mesh;
                    p.Built = mesh != null ? src.sprite : null;
                }
                show &= p.Built != null;
                if (p.Go.activeSelf != show) p.Go.SetActive(show);
                if (!show) continue;
                var t = src.transform;
                var scale = t.lossyScale;
                scale.x *= src.flipX ? -1f : 1f;
                scale.y *= src.flipY ? -1f : 1f;
                scale.z = Mathf.Max(0.01f, p.Depth * s);
                var fix = p.Torso ? torsoFix : legFix;
                Vector3 pos = pivot + fix * (new Vector3(t.position.x, t.position.y, pivot.z) - pivot);
                pos.z = (DesktopFirstPerson.IsMovementActive ? VrSpace.HeadPosition.z : 0f) + p.Side * s;
                p.Go.transform.SetPositionAndRotation(pos + shift, fix * t.rotation);
                p.Go.transform.localScale = scale;
                if (p.Renderer.sharedMaterial != src.sharedMaterial) p.Renderer.sharedMaterial = src.sharedMaterial;
                p.Renderer.sortingLayerID = src.sortingLayerID;
                // After the world, before the arms (VrArms: 30000+).
                p.Renderer.sortingOrder = 29000 + Mathf.Clamp(src.sortingOrder, -900, 900);
                props.Clear();
                props.SetTexture("_MainTex", src.sprite.texture);
                props.SetColor("_RendererColor", src.color);
                p.Renderer.SetPropertyBlock(props);
            }
        }

        // Verbose: how far the game leans the body, and where the drawn upper torso ends up relative to the eyes.
        private float nextLeanLog;
        private float maxLean;
        private int rebuilds;
        private void DebugLean(Body body, Vector3 pivot, Quaternion torsoFix, Vector3 shift)
        {
            if (!Plugin.Verbose || !body.standing) return;
            maxLean = Mathf.Max(maxLean, Mathf.Abs(body.accelRot));
            if (Time.unscaledTime < nextLeanLog) return;
            nextLeanLog = Time.unscaledTime + (body.rb != null && Mathf.Abs(body.rb.velocity.x) > 0.5f ? 0.2f : 1f);
            var up = body.limbs[1].transform.position;
            var drawn = pivot + torsoFix * (up - pivot) + shift;
            var eye = VrSpace.Anchor;
            float fwd = (drawn.x - eye.x) * (FirstPerson.FacingRightNow ? 1f : -1f);
            Plugin.Log.LogInfo($"Body: lean max {maxLean:F1} now {body.accelRot:F1} vx {(body.rb != null ? body.rb.velocity.x : 0f):F1} cap {body.actualMaxSpeed:F1}/{body.maxSpeed:F1} pieces {pieces.Count} rebuilt {rebuilds} eye-hip {eye.y - pivot.y:F2} head-hip {body.limbs[0].transform.position.y - pivot.y:F2} crouch {body.crouchAmount:F2} | game upper torso fwd {(up.x - eye.x) * (FirstPerson.FacingRightNow ? 1f : -1f):F2} below {eye.y - up.y:F2} | drawn fwd {fwd:F2} below {eye.y - drawn.y:F2} (units, restGap {restGap:F2})");
            maxLean = 0f;
        }

        private void Rescan(Body body)
        {
            lastScan = Time.frameCount;
            if (body != built) { Clear(); built = body; restGap = -1f; }
            var want = new List<(SpriteRenderer sr, float depth, int part)>();
            for (int i = 0; i < Parts.Length; i++)
            {
                var limb = body.limbs[Parts[i]];
                if (limb == null) continue;
                var own = limb.GetComponent<SpriteRenderer>();
                foreach (var sr in limb.GetComponentsInChildren<SpriteRenderer>(true))
                    // Attachments (clothing, bandages, splints) wrap their limb, a little proud of it.
                    want.Add((sr, sr == own ? DepthBlocks[i] : DepthBlocks[i] + 0.08f, i));
            }
            bool same = want.Count == pieces.Count;
            for (int i = 0; same && i < want.Count; i++) same = pieces[i].Src == want[i].sr;
            if (same) return;
            // Only the pieces: Clear() also forgets the body, which rebuilt every piece every frame (fixed 2026-10-06).
            DestroyPieces();
            rebuilds++;
            foreach (var (sr, depth, part) in want)
            {
                var go = new GameObject("body " + sr.name) { layer = VoxelTerrain.Layer, hideFlags = HideFlags.DontSave };
                go.transform.SetParent(root.transform, false);
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                go.SetActive(false);
                pieces.Add(new Piece { Src = sr, Go = go, Filter = mf, Renderer = mr, Depth = depth, Side = SideBlocks[part], Torso = Parts[part] <= 2 });
            }
        }

        private void DestroyPieces()
        {
            foreach (var p in pieces) if (p.Go != null) Object.Destroy(p.Go);
            pieces.Clear();
        }

        public void Clear()
        {
            DestroyPieces();
            built = null;
        }

        public void Destroy()
        {
            Clear();
            Object.Destroy(root);
        }
    }
}
