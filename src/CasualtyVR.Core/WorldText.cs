using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace CasualtyVR
{
    /// <summary>
    /// World-space text in first person (user, 2026-10-06: "dialogue text is rendered on the side of the wall and
    /// obscured"). The game's speech (Talker.text, a TextMeshPro placed every LateUpdate 4 units above the head and
    /// 2.6 units towards the flat camera), damage numbers and the tutorial's course text lie in the play plane: from
    /// inside the tunnel they are seen edge-on, and the speech sits inside the front cave wall (the corridor is
    /// +-2 units wide). While an eye renders:
    /// * Your own character's speech is a caption a metre ahead of you, a little below eye level, following where
    ///   you look (lazily, by yaw only), drawn over everything at a readable size.
    /// * Other texts stay where the game puts them, moved into the middle of the tunnel and turned to face you, and
    ///   scaled up if they would be too small to read from where you are.
    /// * Character attachments (2026-10-17, the co-op mod): sprites and texts that hang off a character's root next
    ///   to its Body (not part of it, no collider) are status icons and labels. The icons turn to face you (they
    ///   were seen edge-on); a name tag (TextMeshPro named "CharacterNametag_...", placed by the co-op mod under the
    ///   feet, where it was lost in the floor) goes just above the head instead.
    /// The monitor view is unchanged: everything is put back after each eye.
    /// </summary>
    internal sealed class WorldText
    {
        private const float CaptionMetres = 1.0f, CaptionDropMetres = 0.2f, CaptionLineMetres = 0.04f;
        private const float MinLineRadians = 0.03f;   // about 1.7 degrees per line of text

        private readonly List<TextMeshPro> texts = new List<TextMeshPro>();
        private readonly Dictionary<TextMeshPro, Body> tagOwners = new Dictionary<TextMeshPro, Body>();
        private readonly List<SpriteRenderer> indicators = new List<SpriteRenderer>();
        private const float AttachmentRange = 60f;      // blocks from the player
        private const float NameTagAboveHead = 1.6f;    // blocks above the head limb
        private const float MaxTagLineRadians = 0.05f;  // about 3 degrees per line of a name tag
        private const float MaxIconRadians = 0.07f;     // about 4 degrees for a status icon
        private int lastAttachmentCount;
        private readonly List<(Transform t, Vector3 pos, Quaternion rot, Vector3 scale, Renderer r, int layer, int order)> moved = new List<(Transform, Vector3, Quaternion, Vector3, Renderer, int, int)>();
        private readonly HashSet<TextMeshPro> onTop = new HashSet<TextMeshPro>();
        private float nextScan;
        private Vector3 captionDir;
        private bool hasDir;
        private Body talkerBody;
        private TextMeshPro ownText;
        private string lastLogged;

        public int Shown { get; private set; }

        // Damage numbers live half a second (WorldGeneration.CreateDamageNumber): each one triggers a rescan.
        private static bool scanSoon;

        [HarmonyLib.HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.CreateDamageNumber))]
        private static class DamageNumberPatch
        {
            private static void Postfix() => scanSoon = true;
        }

        public WorldText()
        {
            RenderPipelineManager.beginCameraRendering += OnBegin;
            RenderPipelineManager.endCameraRendering += OnEnd;
        }

        public void Update()
        {
            if (!FirstPerson.Active) { hasDir = false; return; }
            if (scanSoon || Time.unscaledTime >= nextScan)
            {
                scanSoon = false;
                nextScan = Time.unscaledTime + 1f;
                texts.Clear();
                texts.AddRange(Object.FindObjectsOfType<TextMeshPro>());
                ScanAttachments();
            }
            var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (body != talkerBody)
            {
                talkerBody = body;
                var talker = body != null ? body.GetComponent<Talker>() : null;
                ownText = talker != null ? talker.text : null;
            }
            Vector3 f = VrSpace.HeadForward;
            f.y = 0f;
            if (f.sqrMagnitude < 0.01f) f = new Vector3(FirstPerson.FacingRightNow ? 1f : -1f, 0f, 0f);
            f.Normalize();
            captionDir = hasDir ? Vector3.Slerp(captionDir, f, 1f - Mathf.Exp(-2.5f * Time.unscaledDeltaTime)) : f;
            hasDir = true;
        }

        // Direct children of each nearby character's root, other than its Body: the co-op mod's name tag and status
        // icons (its "chara" object is the Body's parent). A root shared by many objects is not a character.
        private void ScanAttachments()
        {
            tagOwners.Clear();
            indicators.Clear();
            var player = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            Vector2 c = player != null ? (Vector2)player.transform.position : Vector2.zero;
            foreach (var b in Object.FindObjectsOfType<Body>())
            {
                if (b == player || b.limbs == null || b.limbs.Length == 0 || b.limbs[0] == null) continue;
                var root = b.transform.parent;
                if (root == null || root.childCount > 16) continue;
                if (((Vector2)b.transform.position - c).sqrMagnitude > AttachmentRange * AttachmentRange) continue;
                for (int i = 0; i < root.childCount; i++)
                {
                    var child = root.GetChild(i);
                    if (child == b.transform || child.GetComponent<Collider2D>() != null || child.GetComponent<Body>() != null) continue;
                    if (child.TryGetComponent<TextMeshPro>(out var tmp)) { if (tmp.name.StartsWith("CharacterNametag_")) tagOwners[tmp] = b; }
                    else if (child.TryGetComponent<SpriteRenderer>(out var sr)) indicators.Add(sr);
                }
            }
            int found = tagOwners.Count + indicators.Count;
            if (Plugin.Verbose && found != lastAttachmentCount)
                Plugin.Log.LogInfo($"WorldText: {tagOwners.Count} name tags, {indicators.Count} status icons on other characters");
            lastAttachmentCount = found;
        }

        private void OnBegin(ScriptableRenderContext ctx, Camera cam)
        {
            if (FirstPerson.Active && DesktopFirstPerson.IsRenderCamera(cam))
            {
                PositionDesktopTalkText(cam);
                return;
            }
            if (!FirstPerson.Active || !hasDir || VrRig.Instance == null || !VrRig.Instance.IsEyeCamera(cam)) return;
            FirstPerson.RenderTime.Start();
            Vector3 head = VrSpace.HeadPosition;
            float s = VrSpace.Scale;
            int shown = 0;
            foreach (var text in texts)
            {
                if (text == null || !text.isActiveAndEnabled || string.IsNullOrEmpty(text.text)) continue;
                var t = text.transform;
                var r = text.renderer;
                moved.Add((t, t.position, t.rotation, t.localScale, r, r != null ? r.sortingLayerID : 0, r != null ? r.sortingOrder : 0));
                float line = LineHeight(text);
                Vector3 pos;
                float k = 1f;
                if (text == ownText)
                {
                    pos = head + captionDir * (CaptionMetres * s) + Vector3.down * (CaptionDropMetres * s);
                    if (line > 0f) k = Mathf.Clamp(CaptionLineMetres * s / line, 0.05f, 20f);
                    OnTop(text);
                    if (r != null) { r.sortingLayerID = TopLayer; r.sortingOrder = 32000; }
                }
                else if (tagOwners.TryGetValue(text, out var owner) && owner != null && owner.limbs[0] != null)
                {
                    // Name tag: above the head (the co-op mod puts it under the feet, in the floor in first person).
                    pos = owner.limbs[0].transform.position + Vector3.up * NameTagAboveHead;
                    pos.z = FirstPerson.EyeZ;
                    // Readable but not huge: the tag is sized for the flat view, where it covered a metre at arm's length.
                    float d = Vector3.Distance(head, pos);
                    if (line > 0f) k = Mathf.Clamp(line, MinLineRadians * d, MaxTagLineRadians * d) / line;
                }
                else
                {
                    pos = t.position;
                    pos.z = FirstPerson.EyeZ;
                    float d = Vector3.Distance(head, pos);
                    if (line > 0f && line < MinLineRadians * d) k = Mathf.Min(3f, MinLineRadians * d / line);
                }
                t.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head, Vector3.up));
                t.localScale *= k;
                shown++;
                // Logged once per line (the game types it out a letter at a time).
                bool fresh = lastLogged == null || !text.text.StartsWith(lastLogged);
                if (fresh || text.text.Length > lastLogged.Length) lastLogged = text.text;
                if (Plugin.Verbose && fresh)
                {
                    Plugin.Log.LogInfo($"WorldText: {(text == ownText ? "caption" : "facing")} '{text.text}' line {line:F2} units x{k:F2} at {pos}");
                }
            }
            // Status icons (flat sprites in the play plane) turn to face you where they are.
            foreach (var sr in indicators)
            {
                if (sr == null || !sr.enabled || !sr.gameObject.activeInHierarchy || sr.sprite == null) continue;
                var t = sr.transform;
                moved.Add((t, t.position, t.rotation, t.localScale, null, 0, 0));
                Vector3 pos = t.position;
                pos.z = FirstPerson.EyeZ;
                t.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head, Vector3.up));
                // Sized for the flat view: near you it filled the view. At most MaxIconRadians tall.
                float h = sr.bounds.size.y, dist = Vector3.Distance(head, pos);
                if (h > MaxIconRadians * dist && h > 1e-4f) t.localScale *= MaxIconRadians * dist / h;
            }
            Shown = shown;
            FirstPerson.RenderTime.Stop();
        }

        private void PositionDesktopTalkText(Camera cam)
        {
            var body = talkerBody;
            var text = ownText;
            if (body == null || body.limbs == null || body.limbs.Length == 0
                || body.limbs[0] == null || text == null || !text.isActiveAndEnabled || string.IsNullOrEmpty(text.text))
                return;

            var head = body.limbs[0];
            var headCollider = head.GetComponentInChildren<Collider2D>();
            float headTop = headCollider != null ? headCollider.bounds.max.y : head.transform.position.y + 0.5f;
            var t = text.transform;
            var r = text.renderer;
            moved.Add((t, t.position, t.rotation, t.localScale, r,
                r != null ? r.sortingLayerID : 0, r != null ? r.sortingOrder : 0));

            Vector3 position = head.transform.position;
            position.y = headTop + 0.15f;
            t.SetPositionAndRotation(position, Quaternion.LookRotation(cam.transform.forward, cam.transform.up));
            t.localScale *= 0.5f;
        }

        private void OnEnd(ScriptableRenderContext ctx, Camera cam)
        {
            if (moved.Count == 0) return;
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                var m = moved[i];
                if (m.t == null) continue;
                m.t.SetPositionAndRotation(m.pos, m.rot);
                m.t.localScale = m.scale;
                if (m.r != null) { m.r.sortingLayerID = m.layer; m.r.sortingOrder = m.order; }
            }
            moved.Clear();
        }

        /// <summary>World height of one line of the text (0 while TMP has not laid it out).</summary>
        private static float LineHeight(TextMeshPro text)
        {
            var b = text.textBounds;
            int lines = text.textInfo != null ? Mathf.Max(1, text.textInfo.lineCount) : 1;
            return b.size.y > 0f ? b.size.y / lines * Mathf.Abs(text.transform.lossyScale.y) : 0f;
        }

        // The caption is drawn through walls (a wall can be nearer than a metre): its own material instance with
        // TMP's depth test off. The flat game draws text without depth anyway.
        private void OnTop(TextMeshPro text)
        {
            if (!onTop.Add(text)) return;
            var mat = text.fontMaterial;   // a per-text instance
            if (mat != null) mat.SetFloat("unity_GUIZTestMode", (float)CompareFunction.Always);
        }

        private static int topLayer = int.MinValue;
        private static int TopLayer
        {
            get
            {
                if (topLayer == int.MinValue)
                {
                    var layers = SortingLayer.layers;
                    topLayer = layers.Length > 0 ? layers[layers.Length - 1].id : 0;
                }
                return topLayer;
            }
        }

        public void Destroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBegin;
            RenderPipelineManager.endCameraRendering -= OnEnd;
            OnEnd(default, null);
        }
    }
}
