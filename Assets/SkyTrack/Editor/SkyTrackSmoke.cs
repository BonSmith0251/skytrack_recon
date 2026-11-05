using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SkyTrack.EditorTools
{
    /// <summary>
    /// Smoke test: reference integrity of all prefabs/scenes, an edit-mode physics drive test on the
    /// TrackDemo course, screenshots, and (optionally) a Windows player build.
    ///   -executeMethod SkyTrack.EditorTools.SkyTrackSmoke.RunBatch [-buildPlayer]
    /// Writes RebuildData/smoke_report.json + smoke_report.md + shots/*.png. Exit 0 = no blocking problems.
    /// </summary>
    public static class SkyTrackSmoke
    {
        [Serializable]
        public class Report
        {
            public int prefabs, renderers, colliders;
            public List<string> missingMeshes = new List<string>(), missingMaterials = new List<string>(),
                missingTextures = new List<string>(), missingScripts = new List<string>(), blocking = new List<string>();
            public int trackPieces; public float trackLength; public string course = "";
            public int sceneryObjects;
            public int generatorChecked, generatorMismatches; public List<string> generatorDiffs = new List<string>();
            public float driveSeconds, driveDistance, driveMaxKmh, driveGroundedFraction, driveMinY, driveEndZ;
            public bool drivePassed;
            public int raceCars; public float raceSeconds, raceMinProgress, raceMedianProgress, raceLength; public int raceRespawns, raceFinished;
            public bool racePassed; public List<string> raceTable = new List<string>();
            public bool finishPassed; public string finishResult = "";
            public List<string> driveTrace = new List<string>();
            public List<string> screenshots = new List<string>();
            public string playerBuild = "not run";
            public List<string> consoleErrors = new List<string>();
        }

        static string Out => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "RebuildData"));

        public static void RunBatch()
        {
            var r = new Report();
            Application.logMessageReceived += (msg, st, type) =>
            {
                if ((type == LogType.Error || type == LogType.Exception) && r.consoleErrors.Count < 50) r.consoleErrors.Add(msg);
            };
            int code = 0;
            try
            {
                CheckPrefabs(r);
                CheckGenerator(r);
                DriveTest(r);
                RaceTest(r);
                FinishTest(r);
                Shots(r);
                if (Environment.GetCommandLineArgs().Contains("-buildPlayer")) BuildPlayer(r);
            }
            catch (Exception e) { r.blocking.Add("exception: " + e); Debug.LogException(e); }
            if (r.missingMeshes.Count + r.missingMaterials.Count + r.missingScripts.Count > 0) r.blocking.Add("broken references in prefabs");
            if (!r.drivePassed) r.blocking.Add("drive test failed");
            if (!r.racePassed) r.blocking.Add("AI race test failed");
            if (!r.finishPassed) r.blocking.Add("final position test failed");
            if (r.generatorChecked == 0 || r.generatorMismatches > 0) r.blocking.Add("track generator differs from the reference port");
            if (r.playerBuild.StartsWith("FAILED")) r.blocking.Add("player build failed");
            if (r.blocking.Count > 0) code = 3;
            File.WriteAllText(Path.Combine(Out, "smoke_report.json"), JsonUtility.ToJson(r, true));
            File.WriteAllText(Path.Combine(Out, "smoke_report.md"), Markdown(r));
            EditorApplication.Exit(code);
        }

        static void CheckPrefabs(Report r)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/SkyTrack/Prefabs" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                r.prefabs++;
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    foreach (var c in t.GetComponents<Component>())
                        if (c == null) r.missingScripts.Add($"{path}:{t.name}");
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                    if (!mf.sharedMesh) r.missingMeshes.Add($"{path}:{mf.name}");
                foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                {
                    r.renderers++;
                    var m = mr.sharedMaterial;
                    if (!m) { r.missingMaterials.Add($"{path}:{mr.name}"); continue; }
                    if (!m.shader || !m.shader.name.StartsWith("HDRP/") || !m.shader.isSupported)
                        r.missingMaterials.Add($"{path}:{mr.name} not on an HDRP shader ({(m.shader ? m.shader.name : "none")})");
                }
                foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true))
                {
                    r.colliders++;
                    if (!mc.sharedMesh) r.missingMeshes.Add($"{path}:{mc.name} (collider)");
                }
            }
            r.missingTextures = r.missingTextures.Distinct().ToList();
        }

        [Serializable] class RefTrack { public int seed, pieceSet, length, count, randCalls; public int[] types, textures; public float[] positions, forwards; }
        [Serializable] class RefTracks { public List<RefTrack> tracks; }

        /// <summary>The C# BuildTrack port must reproduce tools/track_gen.py (the annotated port) exactly.</summary>
        static void CheckGenerator(Report r)
        {
            string path = Path.Combine(Out, "reference_tracks.json");
            var lib = AssetDatabase.LoadAssetAtPath<TrackLibrary>(SkyTrackRebuilder.LibraryPath);
            if (!File.Exists(path) || !lib) { r.generatorDiffs.Add("reference_tracks.json or TrackLibrary missing"); return; }
            var refs = JsonUtility.FromJson<RefTracks>(File.ReadAllText(path));
            foreach (var t in refs.tracks)
            {
                r.generatorChecked++;
                string id = $"seed {t.seed} set {t.pieceSet} len {t.length}";
                TrackLayout g;
                try { g = TrackGenerator.Build(lib.shapes, t.seed, t.pieceSet, t.length); }
                catch (Exception e) { r.generatorMismatches++; r.generatorDiffs.Add($"{id}: {e.Message}"); continue; }
                string diff = null;
                if (g.count != t.count) diff = $"count {g.count} != {t.count}";
                else if (g.pieces.Count != t.types.Length) diff = $"pieces {g.pieces.Count} != {t.types.Length}";
                else if (g.randCalls != t.randCalls) diff = $"rand calls {g.randCalls} != {t.randCalls}";
                else
                    for (int i = 0; i < g.pieces.Count && diff == null; i++)
                    {
                        var p = g.pieces[i];
                        var pos = new Vector3(t.positions[i * 3], t.positions[i * 3 + 1], t.positions[i * 3 + 2]);
                        var fwd = new Vector3(t.forwards[i * 3], t.forwards[i * 3 + 1], t.forwards[i * 3 + 2]);
                        if (p.type != t.types[i]) diff = $"piece {i}: type {p.type} != {t.types[i]}";
                        else if (p.texture != t.textures[i]) diff = $"piece {i}: texture {p.texture} != {t.textures[i]}";
                        else if (p.matrix.position != pos || p.matrix.forward != fwd)
                            diff = $"piece {i}: pose {p.matrix.position:F6}/{p.matrix.forward:F6} != {pos:F6}/{fwd:F6}";
                    }
                if (diff != null) { r.generatorMismatches++; r.generatorDiffs.Add($"{id}: {diff}"); }
            }
        }

        static void DriveTest(Report r)
        {
            EditorSceneManager.OpenScene("Assets/SkyTrack/Scenes/TrackDemo.unity");
            var pieces = UnityEngine.Object.FindObjectsByType<TrackPiece>(FindObjectsSortMode.None);
            r.trackPieces = pieces.Length;
            var race = UnityEngine.Object.FindFirstObjectByType<SkyTrackRace>();
            if (race)
            {
                r.course = $"seed {race.seed}, piece set {race.pieceSet}, length {race.length}, style {race.style}";
                if (race.built) r.sceneryObjects = race.built.Find("Scenery")?.childCount ?? 0;
            }
            foreach (var p in pieces)
                for (int i = 1; i < p.path.Count; i++) r.trackLength += Vector3.Distance(p.path[i - 1], p.path[i]);
            var car = UnityEngine.Object.FindFirstObjectByType<SkyTrackCar>();
            if (!car) { r.blocking.Add("no car in TrackDemo"); return; }
            car.Init();
            var rb = car.GetComponent<Rigidbody>();
            var prev = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            Physics.SyncTransforms();
            Vector3 start = car.transform.position;
            const float dt = 0.02f; int steps = 600, grounded = 0;   // 12 s: the start pad drop costs speed
            r.driveMinY = start.y;
            for (int i = 0; i < steps; i++)
            {
                car.SetInput(i < 50 ? 0 : 1, 0);
                car.Step(dt);
                Physics.Simulate(dt);
                // wheels on the deck, or flying clear over it (jumps): no deck within 6 units below
                bool flying = car.GroundedWheels == 0 && !Physics.Raycast(car.transform.position, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore);
                if (i >= 50 && (car.GroundedWheels >= 3 || flying)) grounded++;
                r.driveMaxKmh = Mathf.Max(r.driveMaxKmh, car.SpeedKmh);
                r.driveMinY = Mathf.Min(r.driveMinY, car.transform.position.y);
                if (i % 25 == 0)
                    r.driveTrace.Add($"t={i * dt:0.00} pos={car.transform.position} fwd={car.transform.forward} v={rb.velocity} g={car.GroundedWheels} comp={string.Join(",", car.wheels.Select(w => w.compression.ToString("0.00")))}");
            }
            r.driveSeconds = steps * dt;
            r.driveDistance = Vector3.Distance(start, car.transform.position);
            r.driveEndZ = car.transform.position.z;
            r.driveGroundedFraction = grounded / (float)(steps - 50);
            r.drivePassed = r.driveDistance > 60 && r.driveGroundedFraction > 0.8f && r.driveMinY > start.y - 6 && r.driveEndZ > start.z + 50;
            var cam = UnityEngine.Object.FindFirstObjectByType<ChaseCamera>();
            if (cam) { cam.Snap(); Shot(r, cam.GetComponent<Camera>(), "track_after_drive"); }
            Physics.simulationMode = prev;
        }

        /// <summary>All racers (the player's car too) driven by the AI for a minute of race time.</summary>
        static void RaceTest(Report r)
        {
            EditorSceneManager.OpenScene("Assets/SkyTrack/Scenes/TrackDemo.unity");
            var director = UnityEngine.Object.FindFirstObjectByType<RaceDirector>();
            if (!director) { r.raceTable.Add("no RaceDirector in TrackDemo"); return; }
            director.aiSeed = 7;
            director.SetupRace(true);
            r.raceCars = director.drivers.Count;
            if (r.raceCars < 2 || director.Path == null) { r.raceTable.Add("race setup failed"); return; }
            director.Player.isPlayer = false;
            director.Player.Car.useKeyboard = false;
            var prev = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            director.race.SetPropsMovable(true);                       // as in play mode
            Physics.SyncTransforms();
            Shot(r, director.race.chase.GetComponent<Camera>(), "race_grid");
            const float dt = 0.02f;
            int steps = 3000;
            var events = new List<string>();
            var lay = director.race.Layout;
            System.Action<RaceDriver, float, string> onRespawn = (d, at, why) =>
            {
                int k = director.Path.IndexAt(at);
                var p = director.Path.points[k];
                int piece = 0; float bd = float.MaxValue;
                for (int c = 0; c < lay.centreLine.Count; c++) { float dd = (lay.centreLine[c] - p).sqrMagnitude; if (dd < bd) { bd = dd; piece = lay.centreOwner[c]; } }
                var lp = Quaternion.Inverse(lay.pieces[piece].matrix.Rotation) * (d.transform.position - lay.pieces[piece].matrix.position);
                events.Add($"{why} at {at:0} ({piece:00}_{lay.pieces[piece].name})");
                r.raceTable.Add($"  {d.driverName}: {why} on {lay.pieces[piece].name} piece-local ({lp.x:0.0}, {lp.y:0.0}, {lp.z:0.0}) up·n {Vector3.Dot(d.transform.up, lay.pieces[piece].matrix.up):0.00}");
            };
            RaceDriver.Respawned += onRespawn;
            for (int i = 0; i < steps; i++)
            {
                director.SimulateStep(dt);
                Physics.Simulate(dt);
                if (i == 1100)
                {
                    var cam = director.race.chase;
                    var old = cam.target; cam.target = director.drivers.OrderBy(d => d.position).ElementAt(Mathf.Min(2, director.drivers.Count - 1)).transform;
                    cam.Snap(); Shot(r, cam.GetComponent<Camera>(), "race_pack"); cam.target = old;
                }
            }
            Physics.simulationMode = prev;
            RaceDriver.Respawned -= onRespawn;
            director.race.SetPropsMovable(false);
            director.Player.isPlayer = true;
            director.Player.Car.useKeyboard = true;
            foreach (var g in events.GroupBy(e => e.Substring(e.IndexOf('(')) + " " + e.Substring(0, e.IndexOf(" at"))).OrderByDescending(g => g.Count()))
                r.raceTable.Add($"respawns: {g.Count()} x {g.Key}");
            r.raceSeconds = steps * dt;
            r.raceLength = director.Path.Length;
            var prog = director.drivers.Select(d => d.progress).OrderBy(x => x).ToList();
            r.raceMinProgress = prog[0];
            r.raceMedianProgress = prog[prog.Count / 2];
            r.raceRespawns = director.drivers.Sum(d => d.respawns);
            r.raceFinished = director.drivers.Count(d => d.finished);
            foreach (var d in director.drivers.OrderBy(d => d.position))
                r.raceTable.Add($"{d.position,2}. {d.driverName,-22} {d.progress,6:0} units  respawns {d.respawns}{(d.finished ? $"  finished {d.finishTime:0.0}s" : "")}  top {d.Car.maxSpeedKmh:0} km/h");
            r.racePassed = r.raceMinProgress > 1000f && r.raceMedianProgress > 1200f && r.raceRespawns <= 2 * r.raceCars;
        }

        /// <summary>Classification at the line: one opponent finishes, then the player (2nd); the rest are
        /// ranked by distance and must not move afterwards, even when a car behind crosses the line later.</summary>
        static void FinishTest(Report r)
        {
            var director = UnityEngine.Object.FindFirstObjectByType<RaceDirector>();
            if (!director) { r.finishResult = "no RaceDirector"; return; }
            director.aiSeed = 7;
            director.SetupRace(true);
            var path = director.Path; var p = director.Player;
            var ai = director.drivers.Where(d => d != p).ToList();
            if (ai.Count < 3) { r.finishResult = "not enough racers"; return; }
            director.countdown = 0f;
            director.Tick(0.02f);
            for (int i = 0; i < ai.Count; i++) ai[i].progress = path.Length * (0.5f + 0.04f * i);
            director.raceTime = 100f; ai[0].progress = path.Length; director.Finished(ai[0]);
            director.raceTime = 110f; p.progress = path.Length; director.Finished(p);
            var before = director.drivers.ToDictionary(d => d, d => d.position);
            bool ok = director.state == RaceDirector.State.Finished && p.finalPosition == 2 && p.position == 2 && ai[0].finalPosition == 1
                      && director.drivers.Select(d => d.finalPosition).OrderBy(x => x).SequenceEqual(Enumerable.Range(1, director.drivers.Count))
                      && ai.Skip(1).All(d => d.estimated && d.finishTime > 110f);
            var last = ai[1];                                            // furthest behind crosses the line later
            director.raceTime = 120f; last.progress = path.Length; director.Finished(last);
            for (int i = 0; i < 5; i++) director.Tick(0.02f);
            ok &= director.drivers.All(d => d.position == before[d]) && p.position == 2;
            r.finishPassed = ok;
            r.finishResult = string.Join(", ", director.drivers.OrderBy(d => d.finalPosition).Select(d => $"{d.finalPosition}.{(d == p ? "Player" : d.driverName)}{(d.estimated ? " ~" + d.finishTime.ToString("0") + "s" : "")}"));
            director.SetupRace(true);
        }

        static void Shots(Report r)
        {
            var sc = EditorSceneManager.OpenScene("Assets/SkyTrack/Scenes/TrackDemo.unity");
            var cam = UnityEngine.Object.FindFirstObjectByType<ChaseCamera>();
            if (cam) { cam.Snap(); Shot(r, cam.GetComponent<Camera>(), "track_start"); }
            var c = Camera.main;
            if (c)
            {
                var pieces = UnityEngine.Object.FindObjectsByType<TrackPiece>(FindObjectsSortMode.None);
                var b = new Bounds(Vector3.zero, Vector3.zero);
                foreach (var p in pieces) b.Encapsulate(p.transform.position);
                c.transform.position = b.center + new Vector3(-b.extents.magnitude * 0.9f, b.extents.magnitude * 0.9f, -b.extents.magnitude * 0.9f);
                c.transform.LookAt(b.center);
                c.GetComponent<ChaseCamera>().enabled = false;
                Shot(r, c, "track_overview");
                c.transform.position = new Vector3(60, 45, -70); c.transform.LookAt(new Vector3(0, 20, 40));
                Shot(r, c, "start_gate");
                // plan view of the generated course
                var pb = new Bounds(pieces.Length > 0 ? pieces[0].transform.position : Vector3.zero, Vector3.zero);
                foreach (var p in pieces) foreach (var rr in p.GetComponentsInChildren<Renderer>()) pb.Encapsulate(rr.bounds);
                c.orthographic = true;
                c.orthographicSize = Mathf.Max(pb.extents.z, pb.extents.x / (16f / 9f)) * 1.08f;
                c.transform.SetPositionAndRotation(pb.center + Vector3.up * 2500f, Quaternion.Euler(90, 0, 0));
                Shot(r, c, "course_plan");
                c.orthographic = false;
            }
            EditorSceneManager.OpenScene("Assets/SkyTrack/Scenes/Showcase.unity");
            c = Camera.main;
            if (c)
            {
                Shot(r, c, "showcase");
                var car = UnityEngine.Object.FindFirstObjectByType<SkyTrackCar>();
                if (car)
                {
                    c.transform.position = car.transform.TransformPoint(new Vector3(3.2f, 1.6f, 4.2f));
                    c.transform.LookAt(car.transform.position + Vector3.up * 0.2f);
                    c.fieldOfView = 45;
                    Shot(r, c, "car");
                }
            }
        }

        static void Shot(Report r, Camera cam, string name)
        {
            var dir = Path.Combine(Out, "shots"); Directory.CreateDirectory(dir);
            foreach (var sky in UnityEngine.Object.FindObjectsByType<SkyFollow>(FindObjectsSortMode.None))
                sky.transform.position = cam.transform.position;     // LateUpdate doesn't run in edit mode
            var rt = new RenderTexture(1280, 720, 24) { antiAliasing = 4 };
            var old = cam.targetTexture;
            cam.targetTexture = rt; cam.Render(); cam.Render(); cam.targetTexture = old;   // HDRP: first frame bakes the sky ambient
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
            RenderTexture.active = null;
            string p = Path.Combine(dir, name + ".png");
            File.WriteAllBytes(p, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex); rt.Release();
            r.screenshots.Add(p);
        }

        static void BuildPlayer(Report r)
        {
            string exe = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Build", "SkyTrackRebuilt.exe"));
            var opts = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray(),
                locationPathName = exe, target = BuildTarget.StandaloneWindows64, options = BuildOptions.None,
            };
            var rep = BuildPipeline.BuildPlayer(opts);
            r.playerBuild = rep.summary.result == BuildResult.Succeeded
                ? $"OK {exe} ({rep.summary.totalSize / 1048576f:0.0} MB, {rep.summary.totalTime.TotalSeconds:0}s)"
                : $"FAILED {rep.summary.result}, {rep.summary.totalErrors} errors";
        }

        static string Markdown(Report r)
        {
            var sb = new System.Text.StringBuilder("# Smoke report\n\n");
            sb.AppendLine($"- Blocking: {(r.blocking.Count == 0 ? "none" : string.Join("; ", r.blocking))}");
            sb.AppendLine($"- Prefabs {r.prefabs}, renderers {r.renderers}, mesh colliders {r.colliders}");
            sb.AppendLine($"- Missing meshes {r.missingMeshes.Count}, materials {r.missingMaterials.Count}, scripts {r.missingScripts.Count}, textures {r.missingTextures.Count}");
            sb.AppendLine($"- Track: {r.course}: {r.trackPieces} pieces, centre line {r.trackLength:0} units, {r.sceneryObjects} scenery objects");
            sb.AppendLine($"- Generator vs reference port: {r.generatorChecked} courses, {r.generatorMismatches} mismatches");
            foreach (var d in r.generatorDiffs.Take(10)) sb.AppendLine("  - " + d);
            sb.AppendLine($"- Drive test: {(r.drivePassed ? "PASS" : "FAIL")} — {r.driveDistance:0.0} units in {r.driveSeconds:0.0}s, top {r.driveMaxKmh:0} km/h, on deck or clear jump {r.driveGroundedFraction:P0}, min y {r.driveMinY:0.0}");
            sb.AppendLine($"- Final position: {(r.finishPassed ? "PASS" : "FAIL")} — {r.finishResult}");
            sb.AppendLine($"- AI race: {(r.racePassed ? "PASS" : "FAIL")} — {r.raceCars} cars, {r.raceSeconds:0}s, course {r.raceLength:0} units, slowest {r.raceMinProgress:0}, median {r.raceMedianProgress:0}, respawns {r.raceRespawns}, finished {r.raceFinished}");
            foreach (var l in r.raceTable) sb.AppendLine("  - " + l);
            sb.AppendLine($"- Player build: {r.playerBuild}");
            sb.AppendLine($"- Console errors: {r.consoleErrors.Count}");
            foreach (var e in r.consoleErrors.Take(15)) sb.AppendLine("  - " + e.Split('\n')[0]);
            foreach (var l in r.missingTextures) sb.AppendLine("  - untextured: " + l);
            return sb.ToString();
        }
    }
}
