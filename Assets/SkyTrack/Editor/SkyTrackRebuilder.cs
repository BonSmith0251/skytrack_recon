using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SkyTrack.EditorTools
{
    // ---------------------------------------------------------------- rebuild.json schema
    [Serializable] public class RbTexture { public string game, asset; public bool alpha, ui; }
    [Serializable] public class RbAudio { public string game, asset; public bool music; public float duration; }
    [Serializable] public class RbMaterial { public string key, shader, texture; public float[] color, emission; public float metallic, smoothness; }
    [Serializable] public class RbSlot { public int slot; public string role, name, material, collider; public bool helper; public int activeInstances; }
    [Serializable] public class RbEffect { public string type; public List<KeyValue> props; }
    [Serializable]
    public class RbModel
    {
        public string name, kind, category, gamePath, definitionPath, mesh, bump;
        public List<RbSlot> slots; public List<KeyValue> properties; public List<RbEffect> effects; public List<string> lightmaps;
    }
    [Serializable] public class RbVehicleDef { public string path; public List<KeyValue> properties; }
    [Serializable] public class RbTrackLibrary { public List<string> platformVariant1, platformVariant2, boxMaterials; }
    [Serializable] public class RbRacerNames { public List<string> menFirst, menLast, womenFirst, womenLast; }
    [Serializable]
    public class RebuildData
    {
        public List<RbTexture> textures; public List<RbAudio> audio; public List<RbMaterial> materials;
        public List<RbModel> models; public List<RbVehicleDef> vehicleDefinitions;
        public RbTrackLibrary trackLibrary;
        public RbRacerNames racerNames;
    }

    [Serializable]
    public class RebuildReport
    {
        public int textures, audioClips, materials, meshes, prefabs, scenes;
        public List<string> warnings = new List<string>();
        public List<string> errors = new List<string>();
    }

    /// <summary>
    /// Builds the Unity project content from RebuildData/rebuild.json (written by tools/stage_rebuild.py).
    /// Batch entry point: Unity -batchmode -projectPath P -executeMethod SkyTrack.EditorTools.SkyTrackRebuilder.RunBatch
    /// Also available from the menu: Sky Track/Rebuild Everything.
    /// </summary>
    public static class SkyTrackRebuilder
    {
        public const string Root = "Assets/SkyTrack";
        static string DataDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "RebuildData"));
        static RebuildData data;
        static RebuildReport report;
        static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();
        static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();
        static readonly Dictionary<string, MshFile> mshCache = new Dictionary<string, MshFile>();

        public static void RunBatch()
        {
            int code = 0;
            try { RebuildAll(); if (report.errors.Count > 0) code = 2; }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            EditorApplication.Exit(code);
        }

        [MenuItem("Sky Track/Rebuild Everything")]
        public static void RebuildAll()
        {
            report = new RebuildReport();
            SkyTrackHDRP.EnsurePipeline();
            mats.Clear(); prefabs.Clear(); mshCache.Clear();
            data = JsonUtility.FromJson<RebuildData>(File.ReadAllText(Path.Combine(DataDir, "rebuild.json")));
            AssetDatabase.Refresh();
            try
            {
                AssetDatabase.StartAssetEditing();
                ConfigureTextures();
                ConfigureAudio();
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh();
            BuildMaterials();
            foreach (var m in data.models) BuildModelPrefab(m);
            BuildVehiclePrefab();
            BuildTrackLibrary();
            AssetDatabase.SaveAssets();
            BuildShowcaseScene();
            BuildTrackScene();
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(Root + "/Scenes/TrackDemo.unity", true),
                new EditorBuildSettingsScene(Root + "/Scenes/Showcase.unity", true),
            };
            PlayerSettings.productName = "Sky Track (rebuilt)";
            PlayerSettings.companyName = "SkyTrackRebuild";
            AssetDatabase.SaveAssets();
            File.WriteAllText(Path.Combine(DataDir, "rebuild_report.json"), JsonUtility.ToJson(report, true));
            Debug.Log($"[SkyTrack] rebuild done: {JsonUtility.ToJson(report)}");
        }

        // ---------------------------------------------------------------- importers
        static void ConfigureTextures()
        {
            foreach (var t in data.textures)
            {
                var ti = AssetImporter.GetAtPath(t.asset) as TextureImporter;
                if (ti == null) { report.errors.Add("texture not imported: " + t.asset); continue; }
                ti.textureType = TextureImporterType.Default;
                ti.alphaIsTransparency = t.alpha;
                ti.alphaSource = t.alpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                ti.wrapMode = t.ui ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                ti.mipmapEnabled = !t.ui;
                ti.anisoLevel = t.ui ? 1 : 4;
                ti.maxTextureSize = 2048;
                ti.isReadable = t.asset.Contains("/Vehicles/Decal");     // VehiclePaint bakes paint + stripes from them
                ti.SaveAndReimport();
                report.textures++;
            }
        }

        static void ConfigureAudio()
        {
            foreach (var a in data.audio)
            {
                var ai = AssetImporter.GetAtPath(a.asset) as AudioImporter;
                if (ai == null) { report.errors.Add("audio not imported: " + a.asset); continue; }
                var s = ai.defaultSampleSettings;
                s.loadType = a.music ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.7f;
                ai.defaultSampleSettings = s;
                ai.loadInBackground = a.music;
                ai.SaveAndReimport();
                report.audioClips++;
            }
        }

        // ---------------------------------------------------------------- materials
        static Texture2D Tex(string asset) => string.IsNullOrEmpty(asset) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(asset);

        /// <summary>tools/clean_textures.py writes de-grimed copies under Textures/Clean; materials prefer them (same UV layout).</summary>
        static Texture2D CleanTex(string asset)
        {
            if (string.IsNullOrEmpty(asset)) return null;
            string clean = asset.Replace("/Textures/", "/Textures/Clean/");
            return AssetDatabase.LoadAssetAtPath<Texture2D>(clean) ?? Tex(asset);
        }

        [MenuItem("Sky Track/Apply Clean Look")]
        public static void ApplyCleanLook()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Root + "/Textures/Clean" }))
            {
                var ti = AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as TextureImporter;
                if (ti == null) continue;
                ti.wrapMode = TextureWrapMode.Repeat; ti.anisoLevel = 8; ti.mipmapEnabled = true;
                ti.alphaIsTransparency = ti.DoesSourceTextureHaveAlpha();
                ti.SaveAndReimport();
            }
            int n = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { Root + "/Materials" }))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                var cur = mat.HasProperty("_BaseColorMap") ? mat.GetTexture("_BaseColorMap") as Texture2D : null;
                if (!cur) continue;
                string p = AssetDatabase.GetAssetPath(cur);
                if (p.Contains("/Textures/Clean/") || p.Contains("/Vehicles/Decal")) continue;
                var clean = CleanTex(p);
                if (clean == cur) continue;
                mat.SetTexture("_BaseColorMap", clean);
                if (mat.HasProperty("_EmissiveColorMap") && mat.GetTexture("_EmissiveColorMap") == cur) mat.SetTexture("_EmissiveColorMap", clean);
                if (mat.HasProperty("_Smoothness") && mat.GetFloat("_Smoothness") <= 0.3f && mat.GetFloat("_Metallic") < 0.1f) mat.SetFloat("_Smoothness", 0.5f);
                EditorUtility.SetDirty(mat); n++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[SkyTrack] clean look applied to {n} materials");
        }

        /// <summary>The game's material kinds on HDRP/Lit and HDRP/Unlit (see <see cref="SkyTrackHDRP"/>).</summary>
        static void BuildMaterials()
        {
            Dir(Root + "/Materials");
            if (!SkyTrackHDRP.Lit || !SkyTrackHDRP.Unlit) { report.errors.Add("HDRP shaders not found (is the HDRP package installed?)"); return; }
            foreach (var m in data.materials)
            {
                string path = $"{Root}/Materials/{m.key}.mat";
                var tex = m.shader == "standard" || m.shader == "emissive" ? CleanTex(m.texture) : Tex(m.texture);
                if (!string.IsNullOrEmpty(m.texture) && !tex) report.warnings.Add($"material {m.key}: texture missing {m.texture}");
                Color col = m.color != null && m.color.Length >= 4 ? new Color(m.color[0], m.color[1], m.color[2], m.color[3]) : Color.white;
                var emission = m.emission != null && m.emission.Length >= 3 ? new Color(m.emission[0], m.emission[1], m.emission[2]) : Color.black;
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                bool fresh = !mat;
                if (fresh) mat = new Material(SkyTrackHDRP.Lit);
                else { mat.shader = SkyTrackHDRP.Lit; mat.shaderKeywords = new string[0]; }
                mat.name = m.key;
                switch (m.shader)
                {
                    case "unlit":                    // sky dome
                        SkyTrackHDRP.SetupUnlit(mat, tex, col);
                        break;
                    case "additive":                 // flames, clouds, glowing decals (the game's SELFILLUM pass)
                        SkyTrackHDRP.SetupUnlit(mat, tex, col);
                        SkyTrackHDRP.MakeTransparent(mat, SkyTrackHDRP.Blend.Additive, true);
                        break;
                    case "sky_haze":                 // white alpha gradient tinted to the haze colour, behind everything
                        SkyTrackHDRP.SetupUnlit(mat, tex, col);
                        SkyTrackHDRP.MakeTransparent(mat, SkyTrackHDRP.Blend.Alpha, true, -10);
                        mat.SetFloat("_EnableFogOnTransparent", 0);
                        break;
                    case "unlit_transparent":
                        SkyTrackHDRP.SetupUnlit(mat, tex, col);
                        SkyTrackHDRP.MakeTransparent(mat, SkyTrackHDRP.Blend.Alpha, false);
                        break;
                    case "vehicle_paint":            // paint + stripes are baked into the base map by VehiclePaint
                        SkyTrackHDRP.SetupLit(mat, tex, Color.white, m.metallic, m.smoothness);
                        break;
                    case "glass":
                        SkyTrackHDRP.SetupLit(mat, tex, col, m.metallic, m.smoothness);
                        SkyTrackHDRP.MakeTransparent(mat, SkyTrackHDRP.Blend.Premultiplied, false);
                        break;
                    default:                         // standard, chrome, emissive
                        SkyTrackHDRP.SetupLit(mat, tex, col, m.metallic, m.shader == "standard" && m.smoothness <= 0.3f ? 0.5f : m.smoothness);
                        if (m.shader == "emissive") SkyTrackHDRP.SetEmission(mat, emission, tex);
                        break;
                }
                SkyTrackHDRP.Validate(mat);
                mat.enableInstancing = true;
                if (fresh) AssetDatabase.CreateAsset(mat, path); else EditorUtility.SetDirty(mat);
                mats[m.key] = mat;
                report.materials++;
            }
        }

        // ---------------------------------------------------------------- meshes + prefabs
        static MshFile Msh(string rel)
        {
            if (!mshCache.TryGetValue(rel, out var f))
                mshCache[rel] = f = new MshFile(File.ReadAllBytes(Path.Combine(DataDir, rel)));
            return f;
        }

        static Mesh SaveMesh(Mesh mesh, string folder)
        {
            Dir(folder);
            string path = $"{folder}/{mesh.name}.asset";
            var old = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (old) { EditorUtility.CopySerialized(mesh, old); report.meshes++; return old; }
            AssetDatabase.CreateAsset(mesh, path);
            report.meshes++;
            return mesh;
        }

        static string PrefabFolder(RbModel m) => m.kind switch
        {
            "platform" => Root + "/Prefabs/Platforms",
            "structure" => Root + "/Prefabs/Structures",
            "decor" => Root + "/Prefabs/Decors",
            "sky" => Root + "/Prefabs/Sky",
            _ => Root + "/Prefabs/Vehicles",
        };

        static void BuildModelPrefab(RbModel m)
        {
            MshFile msh;
            try { msh = Msh(m.mesh); }
            catch (Exception e) { report.errors.Add($"{m.gamePath}: {e.Message}"); return; }
            string meshFolder = $"{Root}/Meshes/{m.category}/{m.name}";
            var root = new GameObject(m.name);
            var info = root.AddComponent<SkyTrackObject>();
            info.gamePath = m.gamePath;
            info.definitionPath = m.definitionPath;
            info.kind = m.kind;
            info.properties = m.properties ?? new List<KeyValue>();
            info.effects = (m.effects ?? new List<RbEffect>()).Select(e => new ObjectEffect { type = e.type, props = e.props }).ToList();
            info.lightmaps = (m.lightmaps ?? new List<string>()).Select(Tex).Where(t => t).ToList();
            info.bumpTexture = Tex(m.bump);

            var visual = new GameObject("Visual").transform; visual.SetParent(root.transform, false);
            Transform collision = null;
            bool dynamicBody = m.kind == "decor";
            foreach (var s in m.slots)
            {
                if (!msh.slots.TryGetValue(s.slot, out var subs)) { report.warnings.Add($"{m.name}: slot {s.slot} missing"); continue; }
                if (s.collider == "skip") continue;
                Transform parent;
                if (s.helper)
                {
                    if (!collision) { collision = new GameObject("Collision").transform; collision.SetParent(root.transform, false); }
                    parent = collision;
                }
                else parent = visual;
                if (subs.Count > 1)
                {
                    var g = new GameObject(s.name).transform; g.SetParent(parent, false); parent = g;
                }
                foreach (var sub in subs)
                {
                    string nm = subs.Count > 1 ? $"{s.name}_inst{sub.instance}" : s.name;
                    var mesh = SaveMesh(MshFile.ToMesh(sub, nm), meshFolder);
                    var go = new GameObject(nm);
                    go.transform.SetParent(parent, false);
                    // multi-instance slots are alternatives (platform decor variants, the 4 wheel spheres are not)
                    if (subs.Count > 1 && m.kind == "platform" && sub.instance > 0) go.SetActive(false);
                    if (s.helper)
                    {
                        var mc = go.AddComponent<MeshCollider>();
                        mc.sharedMesh = mesh;
                        mc.convex = s.collider == "convex";
                    }
                    else
                    {
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;
                        var mr = go.AddComponent<MeshRenderer>();
                        if (mats.TryGetValue(s.material ?? "", out var mat)) mr.sharedMaterial = mat;
                        else report.warnings.Add($"{m.name} slot {s.slot}: material '{s.material}' not found");
                        if (m.kind == "sky" || s.role == "glass" || s.role == "selfillum")
                            mr.shadowCastingMode = ShadowCastingMode.Off;
                        if (m.kind == "sky") mr.receiveShadows = false;
                    }
                }
            }

            if (m.name == "Start") AddPadRamp(root.transform, ref collision);

            if (msh.points.Count > 0)
            {
                var pts = new GameObject("HelperPoints").transform; pts.SetParent(root.transform, false);
                foreach (var kv in msh.points)
                    for (int k = 0; k < kv.Value.Count; k++)
                    {
                        var p = new GameObject($"G{kv.Key:00}_{k:00}").transform;
                        p.SetParent(pts, false); p.localPosition = kv.Value[k];
                    }
            }

            if (m.kind == "platform")
            {
                var tp = root.AddComponent<TrackPiece>();
                if (msh.points.TryGetValue(7, out var path)) tp.path = path;
                if (msh.points.TryGetValue(9, out var spots)) tp.decorSpots = spots;
                if (msh.points.TryGetValue(8, out var ex) && ex.Count >= 3)
                {
                    var exit = new GameObject("Exit").transform; exit.SetParent(root.transform, false);
                    Vector3 right = (ex[1] - ex[0]).normalized, up = (ex[0] - ex[2]).normalized;
                    exit.localPosition = ex[0];
                    exit.localRotation = Quaternion.LookRotation(Vector3.Cross(right, up), up);
                    tp.exit = exit;
                }
                else report.warnings.Add($"{m.name}: no exit frame (group 8)");
            }
            if (m.kind == "sky") root.AddComponent<SkyFollow>();
            if (dynamicBody)
            {
                var rb = root.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(1f, info.GetFloat("Base Weight (kg)", 10f));
            }
            SetStaticRecursive(root, m.kind == "platform" || m.kind == "structure");

            string folder = PrefabFolder(m); Dir(folder);
            prefabs[m.name] = PrefabUtility.SaveAsPrefabAsset(root, $"{folder}/{m.name}.prefab");
            UnityEngine.Object.DestroyImmediate(root);
            report.prefabs++;
        }

        /// <summary>
        /// The start / finish pad's deck (y = 2.96) ends in a 47 degree lip that drops to the track deck (y = 0) at
        /// z ~ 31..33: harmless driving off the start, but a 3-unit wall for every car arriving at the finish (the pad
        /// faces back down the track). A thin hidden ramp from the track deck up to the pad deck takes cars over it.
        /// </summary>
        static void AddPadRamp(Transform root, ref Transform collision)
        {
            if (!collision) { collision = new GameObject("Collision").transform; collision.SetParent(root, false); }
            const float deckY = 2.96f, rimZ = 30f, length = 16f, width = 34f, thick = 1f;   // ends under the back grid row (z 45)
            float angle = Mathf.Atan2(deckY, length);
            var normal = new Vector3(0f, Mathf.Cos(angle), Mathf.Sin(angle));
            var go = new GameObject("pad_ramp");
            go.transform.SetParent(collision, false);
            go.transform.localRotation = Quaternion.Euler(angle * Mathf.Rad2Deg, 0f, 0f);
            go.transform.localPosition = new Vector3(0f, deckY * 0.5f, rimZ + length * 0.5f) - normal * (thick * 0.5f);
            go.AddComponent<BoxCollider>().size = new Vector3(width, thick, Mathf.Sqrt(length * length + deckY * deckY));
        }

        static void SetStaticRecursive(GameObject go, bool on)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, on ? StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic : 0);
        }

        // ---------------------------------------------------------------- assembled vehicle
        static void BuildVehiclePrefab()
        {
            if (!prefabs.TryGetValue("Vehicle01_Main", out var bodyPrefab) || !prefabs.TryGetValue("Vehicle01_Wheels", out var wheelPrefab))
            { report.errors.Add("vehicle body/wheels prefab missing"); return; }
            var bodyMsh = Msh(data.models.First(m => m.name == "Vehicle01_Main").mesh);
            var wheelMsh = Msh(data.models.First(m => m.name == "Vehicle01_Wheels").mesh);
            var defn = bodyPrefab.GetComponent<SkyTrackObject>();
            string carName = (defn.Get("Vehicle model") ?? "Vehicle01").Replace(' ', '_');

            var car = (GameObject)PrefabUtility.InstantiatePrefab(bodyPrefab);
            PrefabUtility.UnpackPrefabInstance(car, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            car.name = carName;
            // the 4 collision spheres (slot 18) sit exactly on the wheel centres; keep the hull as the body collider
            var spheres = car.transform.Find("Collision/slot18_collision_spheres");
            var centres = new List<Vector3>();
            foreach (var sub in bodyMsh.slots[18])
            {
                Vector3 c = Vector3.zero; foreach (var p in sub.positions) c += p; centres.Add(c / sub.positions.Length);
            }
            if (spheres) UnityEngine.Object.DestroyImmediate(spheres.gameObject);

            var wheelsRoot = new GameObject("Wheels").transform; wheelsRoot.SetParent(car.transform, false);
            var ctl = car.AddComponent<SkyTrackCar>();
            var wheelList = new List<SkyTrackCar.Wheel>();
            string[] names = { "FL", "RL", "FR", "RR" };
            for (int i = 0; i < centres.Count; i++)
            {
                bool front = centres[i].z > 0;
                var sub = wheelMsh.slots[0][front ? 0 : 1];
                float radius = sub.positions.Max(p => p.y);
                var w = new GameObject("Wheel_" + (i < names.Length ? names[i] : i.ToString())).transform;
                w.SetParent(wheelsRoot, false);
                w.localPosition = centres[i];
                foreach (var slotName in new[] { "slot00_main", "slot06_chrome" })
                {
                    var src = wheelPrefab.transform.Find($"Visual/{slotName}/{slotName}_inst{(front ? 0 : 1)}");
                    if (!src) { report.warnings.Add("wheel part missing: " + slotName); continue; }
                    var part = new GameObject(slotName).transform; part.SetParent(w, false);
                    part.gameObject.AddComponent<MeshFilter>().sharedMesh = src.GetComponent<MeshFilter>().sharedMesh;
                    part.gameObject.AddComponent<MeshRenderer>().sharedMaterial = src.GetComponent<MeshRenderer>().sharedMaterial;
                }
                wheelList.Add(new SkyTrackCar.Wheel { visual = w, anchor = centres[i], radius = radius, front = front, driven = true });
            }
            ctl.wheels = wheelList.ToArray();
            ctl.engineForce = 600f;                 // tuned up from the stock definition (Base Engine Force 400)
            ctl.maxSpeedKmh = 200f;                 // tuned up from the stock definition (Base Max Speed 160)
            ctl.airGravity = 20f;                   // AH_020 jumps land on their landing ramp from ~150 to 200 km/h
            ctl.acceleration = defn.GetFloat("Base Acceleration", 0.2f);
            ctl.steering = defn.GetFloat("Base Steering", 0.6f);
            ctl.frontFriction = defn.GetFloat("Front Tyre Friction", 1.5f);
            ctl.rearFriction = defn.GetFloat("Rear  Tyre Friction", 2f);
            ctl.suspensionStiffness = defn.GetFloat("Suspension Stiffness", 150);
            ctl.suspensionDamping = defn.GetFloat("Suspension Damping", 15);
            var rb = car.GetComponent<Rigidbody>();
            if (!rb) rb = car.AddComponent<Rigidbody>();
            rb.mass = 1200f;          // definition says 150 kg in Bullet units; 1200 keeps Unity's solver stable
            rb.drag = 0.02f; rb.angularDrag = 2f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var bodyRenderer = car.transform.Find("Visual/slot00_main")?.GetComponent<MeshRenderer>();
            var paint = car.AddComponent<VehiclePaint>();
            paint.body = bodyRenderer;
            paint.decals = Enumerable.Range(1, 11).Select(i => Tex($"{Root}/Textures/Vehicles/Decal{i:00}.jpg")).Where(t => t).ToArray();

            var engine = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Root}/Audio/En01.mp3") ?? AssetDatabase.LoadAssetAtPath<AudioClip>($"{Root}/Audio/En01.wav");
            if (engine)
            {
                var src = car.AddComponent<AudioSource>();
                src.clip = engine; src.loop = true; src.playOnAwake = true; src.spatialBlend = 0.6f; src.volume = 0.5f;
                ctl.engineAudio = src;
            }
            Dir(Root + "/Prefabs/Vehicles");
            prefabs["Car"] = PrefabUtility.SaveAsPrefabAsset(car, $"{Root}/Prefabs/Vehicles/{carName}.prefab");
            UnityEngine.Object.DestroyImmediate(car);
            report.prefabs++;
        }

        // ---------------------------------------------------------------- scenes
        static Scene NewScene()
        {
            var sc = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // HDRP: sky, ambient light, fog and exposure come from the shared volume profile
            SkyTrackHDRP.AddSceneVolume();
            var sun = SkyTrackHDRP.AddSun(new Color(1f, 0.93f, 0.82f), Quaternion.Euler(42, -35, 0));
            RenderSettings.sun = sun;
            return sc;
        }

        static GameObject Place(string prefab, Vector3 pos, Quaternion rot, Transform parent = null)
        {
            if (!prefabs.TryGetValue(prefab, out var p)) { report.warnings.Add("scene: prefab missing " + prefab); return null; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(p);
            go.transform.SetPositionAndRotation(pos, rot);
            if (parent) go.transform.SetParent(parent, true);
            return go;
        }

        static Camera MakeCamera(string name, Vector3 pos, Quaternion rot)
        {
            var cam = new GameObject(name).AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.transform.SetPositionAndRotation(pos, rot);
            cam.nearClipPlane = 0.3f; cam.farClipPlane = 6000f; cam.fieldOfView = 60;
            cam.gameObject.AddComponent<AudioListener>();
            return cam;
        }

        static void AddSkyDome(Camera cam)
        {
            var sky = Place("SkyDome", cam.transform.position, Quaternion.identity);
            if (!sky) return;
            sky.transform.localScale = Vector3.one * 1000f;     // dome radius 4 -> 4000 units, inside the far plane
            sky.GetComponent<SkyFollow>().target = cam.transform;
        }

        static void BuildShowcaseScene()
        {
            var sc = NewScene();
            var groups = new[] { "structure", "decor", "platform" };
            float z = 0;
            var gallery = new GameObject("Gallery").transform;
            foreach (var kind in groups)
            {
                float x = 0, rowDepth = 0;
                foreach (var m in data.models.Where(mm => mm.kind == kind))
                {
                    var p = prefabs[m.name];
                    var b = Bounds(p);
                    float w = Mathf.Max(b.size.x, 4f);
                    var go = Place(m.name, new Vector3(x + w / 2 - b.center.x, -b.min.y, z - b.min.z), Quaternion.identity, gallery);
                    if (go && go.GetComponent<Rigidbody>()) go.GetComponent<Rigidbody>().isKinematic = true;
                    x += w + 20; rowDepth = Mathf.Max(rowDepth, b.size.z);
                }
                z += rowDepth + 60;
            }
            var car = Place("Car", new Vector3(-30, 1.2f, 0), Quaternion.Euler(0, 30, 0), gallery);
            if (car) { car.GetComponent<Rigidbody>().isKinematic = true; car.GetComponent<SkyTrackCar>().enabled = false; }
            var gb = new Bounds(Vector3.zero, Vector3.zero);
            foreach (var r in gallery.GetComponentsInChildren<Renderer>()) gb.Encapsulate(r.bounds);
            var cam = MakeCamera("Camera", gb.center + new Vector3(-gb.extents.x * 0.55f, gb.extents.magnitude * 0.45f, -gb.extents.magnitude * 1.05f), Quaternion.identity);
            cam.transform.LookAt(gb.center);
            cam.gameObject.AddComponent<FlyCamera>();
            AddSkyDome(cam);
            Save(sc, "Showcase");
        }

        // ---------------------------------------------------------------- course generator data
        public const string LibraryPath = Root + "/TrackLibrary.asset";
        static TrackLibrary library;

        static void BuildTrackLibrary()
        {
            library = AssetDatabase.LoadAssetAtPath<TrackLibrary>(LibraryPath);
            bool create = !library;
            if (create) library = ScriptableObject.CreateInstance<TrackLibrary>();
            library.shapes = new List<TrackShape>();
            foreach (var name in TrackGenerator.Platforms)
            {
                var model = data.models.FirstOrDefault(m => m.kind == "platform" && m.name == name);
                if (model == null) { report.errors.Add("track library: platform missing " + name); library.shapes.Add(new TrackShape { name = name }); continue; }
                var msh = Msh(model.mesh);
                int Inst(int slot) => msh.slots.TryGetValue(slot, out var l) ? l.Count : 0;
                Vector3[] Pts(int g) => msh.points.TryGetValue(g, out var l) ? l.ToArray() : new Vector3[0];
                var shape = new TrackShape
                {
                    name = name, prefab = prefabs.TryGetValue(name, out var pf) ? pf : null,
                    centre = Pts(7), exit = Pts(8), decorSpots = Pts(9),
                    variantsA = Inst(8), variantsB = Inst(9), extras = Inst(10),
                    randomDecors = (model.properties ?? new List<KeyValue>()).Any(kv => kv.key == "Platform Decors Type" && kv.value == "DECORS_TYPE_RANDOM"),
                };
                if (shape.exit.Length < 3 || shape.centre.Length < 3) report.errors.Add("track library: bad helper points on " + name);
                library.shapes.Add(shape);
            }
            GameObject P(string n) { if (prefabs.TryGetValue(n, out var g)) return g; report.errors.Add("track library: prefab missing " + n); return null; }
            library.start = P("Start"); library.gate = P("LFrame");
            library.infoLeft = P("Main_L"); library.infoRight = P("Main_R");
            library.boxes = P("Boxes_Main01"); library.signs = P("Signs_Main01");
            library.towerMain = new[] { P("Main01"), P("Main02"), P("Main03") };
            library.towerUp = new[] { P("Up01"), P("Up02") };
            Material M(string key) { if (mats.TryGetValue(key, out var m)) return m; report.errors.Add("track library: material missing " + key); return null; }
            var tl = data.trackLibrary ?? new RbTrackLibrary();
            library.platformVariant1 = (tl.platformVariant1 ?? new List<string>()).Select(M).ToArray();
            library.platformVariant2 = (tl.platformVariant2 ?? new List<string>()).Select(M).ToArray();
            library.boxMaterials = (tl.boxMaterials ?? new List<string>()).Select(M).ToArray();
            if (library.boxes && library.signs)
            {
                var bb = Bounds(library.boxes); var sb = Bounds(library.signs);
                library.decorTemplates = new TrackScenery.DecorTemplates { boxSize = bb.size, boxMinY = bb.min.y, signMinY = sb.min.y };
            }
            prefabs.TryGetValue("Car", out library.car);
            if (!library.car) report.errors.Add("track library: car prefab missing");
            VehicleStats Stats(string file)
            {
                var d = data.vehicleDefinitions?.FirstOrDefault(v => v.path.EndsWith(file));
                if (d == null) { report.errors.Add("vehicle definition missing: " + file); return default; }
                float F(string key, float def) { var kv = d.properties.FirstOrDefault(p => p.key == key); return kv != null && float.TryParse(kv.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def; }
                return new VehicleStats
                {
                    engineForce = F("Base Engine Force", 400), maxSpeed = F("Base Max Speed", 160), acceleration = F("Base Acceleration", 0.2f),
                    steering = F("Base Steering", 0.6f), frontFriction = F("Front Tyre Friction", 1.5f), rearFriction = F("Rear  Tyre Friction", 2f),
                };
            }
            library.statsMin = Stats("Main_Min.obj");
            library.statsMax = Stats("Main_Max.obj");
            var rn = data.racerNames ?? new RbRacerNames();
            library.menFirst = (rn.menFirst ?? new List<string>()).ToArray(); library.menLast = (rn.menLast ?? new List<string>()).ToArray();
            library.womenFirst = (rn.womenFirst ?? new List<string>()).ToArray(); library.womenLast = (rn.womenLast ?? new List<string>()).ToArray();
            if (library.menFirst.Length == 0 || library.womenLast.Length == 0) report.errors.Add("track library: racer names missing");
            if (create) AssetDatabase.CreateAsset(library, LibraryPath); else EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Default course of TrackDemo: the game's generator, all 19 pieces, medium length.</summary>
        public const int DemoSeed = 12345, DemoPieceSet = 2, DemoLength = 1, DemoStyle = 1;

        static void BuildTrackScene()
        {
            var sc = NewScene();
            var raceGo = new GameObject("Race");
            var race = raceGo.AddComponent<SkyTrackRace>();
            race.library = library;
            race.seed = DemoSeed; race.pieceSet = DemoPieceSet; race.length = DemoLength; race.style = DemoStyle;

            var car = Place("Car", new Vector3(0, 1.2f, 30f), Quaternion.identity);
            var cam = MakeCamera("ChaseCamera", new Vector3(0, 4, 20), Quaternion.identity);
            var chase = cam.gameObject.AddComponent<ChaseCamera>();
            if (car) { chase.target = car.transform; race.car = car.GetComponent<SkyTrackCar>(); }
            race.chase = chase;
            var director = raceGo.AddComponent<RaceDirector>();
            director.race = race;
            race.Build();
            if (race.Layout == null) report.errors.Add("TrackDemo: course generation failed");
            else
                foreach (var p in race.Pieces) if (!p) report.errors.Add("TrackDemo: platform prefab without TrackPiece");
            AddSkyDome(cam);

            var music = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Root}/Audio/MusTrks/TrR01.mp3");
            if (music)
            {
                var src = new GameObject("Music").AddComponent<AudioSource>();
                src.clip = music; src.loop = true; src.playOnAwake = true; src.volume = 0.6f; src.spatialBlend = 0;
            }
            Save(sc, "TrackDemo");
        }

        static void Save(Scene sc, string name)
        {
            Dir(Root + "/Scenes");
            EditorSceneManager.SaveScene(sc, $"{Root}/Scenes/{name}.unity");
            report.scenes++;
        }

        public static Bounds Bounds(GameObject prefab)
        {
            var rs = prefab.GetComponentsInChildren<MeshFilter>(false);
            if (rs.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
            Bounds b = default; bool init = false;
            foreach (var mf in rs)
            {
                if (!mf.sharedMesh) continue;
                var mb = mf.sharedMesh.bounds;
                var m = prefab.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var c = m.MultiplyPoint3x4(mb.center);
                var e = mb.extents;
                var wb = new Bounds(c, Vector3.zero);
                for (int i = 0; i < 8; i++)
                    wb.Encapsulate(m.MultiplyPoint3x4(mb.center + Vector3.Scale(e, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, ((i & 4) >> 1) - 1))));
                if (!init) { b = wb; init = true; } else b.Encapsulate(wb);
            }
            return b;
        }

        static void Dir(string assetFolder)
        {
            var parts = assetFolder.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = cur + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }
    }
}
