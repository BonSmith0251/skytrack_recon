using UnityEditor;
using UnityEditor.Rendering.HighDefinition;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SkyTrack.EditorTools
{
    /// <summary>
    /// High Definition Render Pipeline setup for the rebuilt project: pipeline asset, linear colour space, a shared
    /// volume profile (gradient sky for ambient light, fixed exposure, fog, shadow distance) and HDRP versions of the
    /// game's material kinds. Exposure is fixed at EV 0 so the game's colours keep their values: an unlit texel of
    /// 1.0 shows as 1.0, and the sun (~3.6 lux) lights a white surface like intensity 1.15 did in the built-in pipeline.
    /// </summary>
    public static class SkyTrackHDRP
    {
        const string Dir = SkyTrackRebuilder.Root + "/Settings";
        public const string PipelinePath = Dir + "/SkyTrackHDRP.asset";
        public const string ProfilePath = Dir + "/SkyTrackVolume.asset";
        public const float SunLux = 3.6f;

        public static void EnsurePipeline()
        {
            if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder(SkyTrackRebuilder.Root, "Settings");
            var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(PipelinePath);
            if (!asset)
            {
                asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                AssetDatabase.CreateAsset(asset, PipelinePath);
            }
            var s = asset.currentPlatformRenderPipelineSettings;
            s.supportTransparentBackface = true;
            s.supportTransparentDepthPrepass = true;
            s.hdShadowInitParams.maxShadowRequests = 32;
            asset.currentPlatformRenderPipelineSettings = s;
            EditorUtility.SetDirty(asset);
            PlayerSettings.colorSpace = ColorSpace.Linear;
            GraphicsSettings.defaultRenderPipeline = asset;
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = null;                     // every level uses the default asset
            }
            EnsureProfile();
            AssetDatabase.SaveAssets();
        }

        static T Get<T>(VolumeProfile p) where T : VolumeComponent
        {
            if (!p.TryGet<T>(out var c)) { c = p.Add<T>(true); c.name = typeof(T).Name; AssetDatabase.AddObjectToAsset(c, p); }
            c.active = true;
            return c;
        }

        public static VolumeProfile EnsureProfile()
        {
            var p = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (!p) { p = ScriptableObject.CreateInstance<VolumeProfile>(); AssetDatabase.CreateAsset(p, ProfilePath); }

            var env = Get<VisualEnvironment>(p);
            env.skyType.Override((int)SkyType.Gradient);
            env.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
            // the game's sky (the dome with Clouds01..10) is geometry; the gradient sky behind it lights the scene
            var sky = Get<GradientSky>(p);
            sky.top.Override(new Color(0.42f, 0.55f, 0.82f));
            sky.middle.Override(new Color(0.62f, 0.66f, 0.76f));
            sky.bottom.Override(new Color(0.18f, 0.16f, 0.15f));
            sky.gradientDiffusion.Override(1.2f);
            sky.skyIntensityMode.Override(SkyIntensityMode.Multiplier);
            sky.multiplier.Override(0.9f);

            var exp = Get<Exposure>(p);
            exp.mode.Override(ExposureMode.Fixed);
            exp.fixedExposure.Override(0f);

            var fog = Get<Fog>(p);
            fog.enabled.Override(true);
            fog.meanFreePath.Override(2600f);           // distant towers fade into the haze, the dome (4000 out) stays
            fog.baseHeight.Override(-200f);
            fog.maximumHeight.Override(900f);
            fog.maxFogDistance.Override(3500f);
            fog.albedo.Override(new Color(0.8f, 0.78f, 0.82f));
            fog.enableVolumetricFog.Override(false);

            var sh = Get<HDShadowSettings>(p);
            sh.maxShadowDistance.Override(450f);

            var tm = Get<Tonemapping>(p);
            tm.mode.Override(TonemappingMode.Neutral);

            var ao = Get<ScreenSpaceAmbientOcclusion>(p);
            ao.intensity.Override(0.6f);

            EditorUtility.SetDirty(p);
            return p;
        }

        /// <summary>Global volume with the shared profile (one per scene).</summary>
        public static void AddSceneVolume()
        {
            var go = new GameObject("Global Volume");
            var v = go.AddComponent<Volume>();
            v.isGlobal = true;
            v.sharedProfile = EnsureProfile();
        }

        public static Light AddSun(Color color, Quaternion rotation)
        {
            var go = new GameObject("Sun");
            var sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
            go.AddComponent<HDAdditionalLightData>();
            sun.color = color;
            sun.intensity = SunLux;                       // directional lights are in lux
            sun.shadows = LightShadows.Soft;
            go.transform.rotation = rotation;
            return sun;
        }

        // ---------------------------------------------------------------- materials
        public static Shader Lit => Shader.Find("HDRP/Lit");
        public static Shader Unlit => Shader.Find("HDRP/Unlit");

        public static void SetupLit(Material m, Texture tex, Color col, float metallic, float smoothness)
        {
            m.shader = Lit;
            m.SetTexture("_BaseColorMap", tex);
            m.SetColor("_BaseColor", col);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
            m.SetTexture("_MainTex", tex);                // legacy names: lightmapper / scripts that look for them
            m.SetColor("_Color", col);
        }

        public static void SetupUnlit(Material m, Texture tex, Color col)
        {
            m.shader = Unlit;
            m.SetTexture("_UnlitColorMap", tex);
            m.SetColor("_UnlitColor", col);
            m.SetTexture("_MainTex", tex);
            m.SetColor("_Color", col);
        }

        public enum Blend { Alpha = 0, Additive = 1, Premultiplied = 4 }

        public static void MakeTransparent(Material m, Blend blend, bool doubleSided, int sortPriority = 0)
        {
            m.SetFloat("_SurfaceType", 1);
            m.SetFloat("_BlendMode", (float)blend);
            m.SetFloat("_EnableFogOnTransparent", blend == Blend.Additive ? 0 : 1);
            m.SetFloat("_TransparentSortPriority", sortPriority);
            m.SetFloat("_ZWrite", 0);
            if (doubleSided) { m.SetFloat("_DoubleSidedEnable", 1); m.SetFloat("_CullMode", 0); }
        }

        public static void SetEmission(Material m, Color c, Texture map)
        {
            m.SetFloat("_UseEmissiveIntensity", 0);
            m.SetColor("_EmissiveColor", c);
            m.SetColor("_EmissiveColorLDR", c);
            m.SetTexture("_EmissiveColorMap", map);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }

        /// <summary>Recomputes keywords and passes after the properties change (what the material inspector does).</summary>
        public static void Validate(Material m) => HDShaderUtils.ResetMaterialKeywords(m);
    }
}
