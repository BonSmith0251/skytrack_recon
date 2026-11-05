using System.Collections.Generic;
using UnityEngine;

namespace SkyTrack
{
    /// <summary>
    /// Lets you tint the car body paint mask (Decal textures) at runtime, like the game's paint shop. The decals are
    /// black/white masks (black = paint, white = stripes); the paint job is baked into a texture once per
    /// (decal, paint, stripes) and set as the HDRP/Lit base colour map of the body.
    /// </summary>
    [ExecuteAlways]
    public class VehiclePaint : MonoBehaviour
    {
        public Renderer body;
        public Color paint = new Color(0.75f, 0.08f, 0.05f);
        public Color stripes = new Color(0.92f, 0.92f, 0.9f);
        public Texture2D[] decals;
        public int decal;

        static readonly Dictionary<(Texture2D, Color32, Color32), Texture2D> baked = new Dictionary<(Texture2D, Color32, Color32), Texture2D>();
        static readonly int BaseMap = Shader.PropertyToID("_BaseColorMap"), BaseColor = Shader.PropertyToID("_BaseColor");

        void OnValidate() { if (isActiveAndEnabled) Apply(); }
        void OnEnable() { Apply(); }

        public void Apply()
        {
            if (!body) return;
            var mask = decals != null && decals.Length > 0 ? decals[Mathf.Clamp(decal, 0, decals.Length - 1)] : null;
            var mpb = new MaterialPropertyBlock();
            body.GetPropertyBlock(mpb);
            var tex = Bake(mask, paint, stripes);
            if (tex) { mpb.SetTexture(BaseMap, tex); mpb.SetColor(BaseColor, Color.white); }
            else mpb.SetColor(BaseColor, paint);                 // no readable mask: plain paint
            body.SetPropertyBlock(mpb);
        }

        public static Texture2D Bake(Texture2D mask, Color paint, Color stripes)
        {
            if (!mask || !mask.isReadable) return null;
            var key = (mask, (Color32)paint, (Color32)stripes);
            if (baked.TryGetValue(key, out var t) && t) return t;
            var src = mask.GetPixels32();
            var dst = new Color32[src.Length];
            Color32 a = paint, b = stripes;
            for (int i = 0; i < src.Length; i++) dst[i] = Color32.Lerp(a, b, src[i].r / 255f);
            t = new Texture2D(mask.width, mask.height, TextureFormat.RGBA32, true) { name = $"{mask.name}_paint", wrapMode = TextureWrapMode.Repeat, anisoLevel = 4, hideFlags = HideFlags.DontSave };
            t.SetPixels32(dst);
            t.Apply(true, true);
            baked[key] = t;
            return t;
        }
    }
}
