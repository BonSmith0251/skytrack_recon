using System.Collections.Generic;
using UnityEngine;

namespace SkyTrack
{
    /// <summary>
    /// Builds a race course exactly like the game: <see cref="TrackGenerator"/> for the platforms (seed, piece
    /// set, length) and <see cref="TrackScenery"/> for decks, decors, screens, towers, start/finish pads and gates.
    /// Keys at runtime: N new track (the game's CHANGE TRACK), C roll a Career race, P piece set, L length,
    /// S scenery style, H hide this panel.
    /// </summary>
    public class SkyTrackRace : MonoBehaviour
    {
        public TrackLibrary library;
        [Header("Course (game parameters)")]
        public int seed = 12345;
        [Range(0, 2)] public int pieceSet = 2;
        [Range(0, 2)] public int length = 1;
        [Range(0, 2)] public int style = 1;
        [Tooltip("The game re-seeds scenery from the clock; -1 uses the course seed so it is reproducible")]
        public int scenerySeed = -1;
        public bool decorations = true;
        public string title = "";

        [Header("Scene")]
        public SkyTrackCar car;
        public ChaseCamera chase;
        public Transform built;
        public bool showPanel = true;
        [Tooltip("Rebuild the course when entering play mode (otherwise the course saved in the scene is used)")]
        public bool buildOnStart;

        public TrackLayout Layout { get; private set; }
        public List<SceneryItem> Scenery { get; private set; }
        public List<TrackPiece> Pieces { get; } = new List<TrackPiece>();
        string status = "";

        void Start()
        {
            if (buildOnStart || !built) Build();
            else { CollectPieces(); SetPropsMovable(true); PlaceCar(); StartRace(); }
        }

        /// <summary>Loose props (boxes, signs: 20 kg dynamic bodies in the game) are frozen while the course is
        /// built in the editor, and a saved scene keeps them frozen; free them for play.</summary>
        public void SetPropsMovable(bool movable)
        {
            if (!built) return;
            var scenery = built.Find("Scenery");
            if (!scenery) return;
            foreach (var rb in scenery.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = !movable;
        }

        [ContextMenu("Build course")]
        public void Build()
        {
            if (!library) { status = "no TrackLibrary assigned"; return; }
            Clear();
            try { Layout = TrackGenerator.Build(library.shapes, seed, pieceSet, length); }
            catch (TrackBuildException e) { status = e.Message; return; }
            var rng = new MsvcRandom(scenerySeed < 0 ? seed : scenerySeed);
            var lanes = TrackScenery.ChooseVariants(library.shapes, Layout, style, rng, decorations);

            built = new GameObject("Course").transform;
            built.SetParent(transform, false);
            var platforms = new GameObject("Platforms").transform; platforms.SetParent(built, false);
            var scenery = new GameObject("Scenery").transform; scenery.SetParent(built, false);
            Pieces.Clear();
            foreach (var p in Layout.pieces)
            {
                var go = Spawn(library.shapes[p.type].prefab, p.matrix, platforms);
                if (!go) continue;
                go.name = $"{p.index:00}_{p.name}";
                ApplyPieceVariants(go, p);
                Pieces.Add(go.GetComponent<TrackPiece>());
            }
            Physics.SyncTransforms();
            Scenery = TrackScenery.Generate(library.shapes, Layout, style, rng, lanes, library.decorTemplates, ProbePiece, decorations);
            foreach (var s in Scenery)
            {
                var prefab = PrefabFor(s);
                var go = Spawn(prefab, s.matrix, scenery);
                if (!go) continue;
                if (s.kind == "Boxes" && s.variant >= 1 && s.variant <= library.boxMaterials.Length)
                    foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true)) r.sharedMaterial = library.boxMaterials[s.variant - 1];
                var rb = go.GetComponent<Rigidbody>();
                if (rb) rb.isKinematic = !Application.isPlaying;
            }
            status = $"{Layout.pieces.Count} pieces, {Layout.CentreLength:0} units, {Scenery.Count} scenery objects";
            PlaceCar();
            StartRace();
        }

        /// <summary>Vertical segment test against one platform's colliders (the game's 0x42e1c0).</summary>
        bool ProbePiece(int piece, Vector3 from, Vector3 to, out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.zero;
            if (piece < 0 || piece >= Pieces.Count || !Pieces[piece]) return false;
            var ray = new Ray(from, (to - from).normalized);
            float len = Vector3.Distance(from, to), best = float.MaxValue;
            foreach (var c in Pieces[piece].GetComponentsInChildren<Collider>())
                if (c.Raycast(ray, out var hit, len) && hit.distance < best) { best = hit.distance; point = hit.point; normal = hit.normal; }
            return best < float.MaxValue;
        }

        /// <summary>Opponents and grid (play mode only; the saved scene holds just the player's car).</summary>
        void StartRace()
        {
            var director = GetComponent<RaceDirector>();
            if (director && Application.isPlaying) director.SetupRace(true);
        }

        public void Clear()
        {
            Pieces.Clear();
            if (!built) return;
            if (Application.isPlaying) Destroy(built.gameObject); else DestroyImmediate(built.gameObject);
            built = null;
        }

        void CollectPieces()
        {
            Pieces.Clear();
            Pieces.AddRange(built.GetComponentsInChildren<TrackPiece>());
            EnsureLayout();
        }

        /// <summary>The layout isn't serialized: regenerate it (deterministic) for a course loaded with the scene.</summary>
        public void EnsureLayout()
        {
            if (Layout != null || !library) return;
            try { Layout = TrackGenerator.Build(library.shapes, seed, pieceSet, length); } catch (TrackBuildException) { }
        }

        GameObject PrefabFor(SceneryItem s)
        {
            switch (s.kind)
            {
                case "Start": case "Finish": return library.start;
                case "Gate1": case "Gate2": return library.gate;
                case "InfoL": return library.infoLeft;
                case "InfoR": return library.infoRight;
                case "Boxes": return library.boxes;
                case "Signs": return library.signs;
                case "TowerMain": return s.variant >= 1 && s.variant <= library.towerMain.Length ? library.towerMain[s.variant - 1] : null;
                case "TowerUp": return s.variant >= 1 && s.variant <= library.towerUp.Length ? library.towerUp[s.variant - 1] : null;
            }
            return null;
        }

        static GameObject Spawn(GameObject prefab, EngineMatrix m, Transform parent)
        {
            if (!prefab) return null;
            GameObject go;
#if UNITY_EDITOR
            if (!Application.isPlaying) go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, parent);
            else
#endif
            go = Instantiate(prefab, parent);
            go.transform.SetPositionAndRotation(m.position, m.Rotation);
            return go;
        }

        /// <summary>Deck sections (slots 8/9 + their colliders 11/12), canopy (10/13) and the Main01/Main02 texture.</summary>
        void ApplyPieceVariants(GameObject go, PlacedPiece p)
        {
            SelectInstance(go, "Visual/slot08_variant_a", p.variantA);
            SelectInstance(go, "Collision/slot11_collision_variant_a", p.variantA);
            SelectInstance(go, "Visual/slot09_variant_b", p.variantB);
            SelectInstance(go, "Collision/slot12_collision_variant_b", p.variantB);
            SelectInstance(go, "Visual/slot10_extra", p.extra);
            SelectInstance(go, "Collision/slot13_collision_extra", p.extra);
            if (p.texture == 2)
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    int i = System.Array.IndexOf(library.platformVariant1, r.sharedMaterial);
                    if (i >= 0 && i < library.platformVariant2.Length && library.platformVariant2[i]) r.sharedMaterial = library.platformVariant2[i];
                }
        }

        static void SelectInstance(GameObject go, string path, int instance)
        {
            var t = go.transform.Find(path);
            if (!t) return;
            if (t.GetComponent<MeshFilter>() || t.GetComponent<MeshCollider>()) { t.gameObject.SetActive(instance == 0); return; }   // single instance
            t.gameObject.SetActive(true);
            for (int i = 0; i < t.childCount; i++) t.GetChild(i).gameObject.SetActive(i == instance);
        }

        [Tooltip("Start position along the first straight (the start pad's front rim drops onto the deck at z ~ 31..38)")]
        public float startDistance = 45f;

        /// <summary>The start grid: on the first straight, just past the start pad.</summary>
        public Pose StartPose()
        {
            var first = Layout != null && Layout.pieces.Count > 0 ? Layout.pieces[0].matrix : EngineMatrix.Identity;
            float y = startDistance < 31f ? 4.3f : 1.2f;          // on the pad deck (y = 3) or on the track deck
            return new Pose(first.Transform(new Vector3(0, y, startDistance)), first.Rotation);
        }

        public void PlaceCar()
        {
            if (!car) return;
            var pose = StartPose();
            car.Init();
            car.Teleport(pose.position, pose.rotation);
            car.SetRespawn(pose.position, pose.rotation);
            if (chase) chase.Snap();
        }

        public void NewSeed() { seed = Random.Range(0, 32768); title = ""; }

        public void RollCareerRace()
        {
            var r = new MsvcRandom(Random.Range(0, int.MaxValue));
            var c = CareerRace.Roll(r, r.Next() % 3);
            seed = c.seed; style = c.style; pieceSet = c.pieceSet; length = c.length; title = c.title;
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        void Update()
        {
            bool rebuild = false;
            if (Input.GetKeyDown(KeyCode.N)) { NewSeed(); rebuild = true; }
            if (Input.GetKeyDown(KeyCode.C)) { RollCareerRace(); rebuild = true; }
            if (Input.GetKeyDown(KeyCode.P)) { pieceSet = (pieceSet + 1) % 3; rebuild = true; }
            if (Input.GetKeyDown(KeyCode.L)) { length = (length + 1) % 3; rebuild = true; }
            if (Input.GetKeyDown(KeyCode.S)) { style = (style + 1) % 3; rebuild = true; }
            if (Input.GetKeyDown(KeyCode.H)) showPanel = !showPanel;
            if (rebuild) Build();
        }
#endif

        void OnGUI()
        {
            if (!showPanel) return;
            var box = new Rect(10, 10, 430, 150);
            GUI.Box(box, GUIContent.none);
            GUILayout.BeginArea(new Rect(box.x + 8, box.y + 6, box.width - 16, box.height - 10));
            GUILayout.Label(string.IsNullOrEmpty(title) ? $"Track #{seed}" : $"{title}  (track #{seed})");
            GUILayout.Label($"Pieces: {TrackGenerator.PieceSetNames[Mathf.Clamp(pieceSet, 0, 2)]}   Length: {TrackGenerator.LengthNames[Mathf.Clamp(length, 0, 2)]}   Style: {style}");
            GUILayout.Label(status);
            GUILayout.Label("N new track · C career race · P pieces · L length · S style · R respawn · H hide");
            GUILayout.Label("Arrows/WASD drive · Q/W kick · O racers · K skill · T restart race");
            GUILayout.EndArea();
        }
    }
}
