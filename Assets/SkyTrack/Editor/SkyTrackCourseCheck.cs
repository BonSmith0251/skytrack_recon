using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SkyTrack.EditorTools
{
    /// <summary>
    /// Drives a single AI car round many generated courses (every piece set x length, several seeds, the three
    /// grid lanes) and records every hard stop: a drop in forward speed no brake can produce, or a stall with the
    /// throttle open. For each one it names the piece, the piece-local position and the collider the car's body
    /// was touching, so collision that sticks out of the visible deck (or a step at a joint) shows up as a
    /// cluster at the same spot of the same piece type. Report: RebuildData/course_check.md (+ .json).
    /// Batch: -executeMethod SkyTrack.EditorTools.SkyTrackCourseCheck.RunBatch [-courseSeeds 101,2024]
    /// </summary>
    public static class SkyTrackCourseCheck
    {
        [Serializable] public class Hit { public string course, piece, collider, kind; public float at, speedBefore, speedAfter, wheelMinUp, maxCompression, impulse; public Vector3 local, normal; public int lane; }
        [Serializable] public class CourseResult { public string course; public float length, time, progress; public int hits, stalls, respawns; public bool completed; }
        [Serializable] public class Result { public List<CourseResult> courses = new List<CourseResult>(); public List<Hit> hits = new List<Hit>(); public int totalHits, totalStalls, totalRespawns, incomplete; public float totalTime; }

        static string Out => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "RebuildData"));
        public static int[] DefaultSeeds = { 101, 2024, 7777, 31000 };

        [MenuItem("Sky Track/Check All Courses (collision sweep)")]
        public static void RunMenu()
        {
            var r = Run(DefaultSeeds);
            EditorUtility.DisplayDialog("Course check", Summary(r), "OK");
        }

        public static void RunBatch()
        {
            int code = 0;
            try
            {
                var seeds = DefaultSeeds;
                var args = Environment.GetCommandLineArgs();
                int k = Array.IndexOf(args, "-courseSeeds");
                if (k >= 0 && k + 1 < args.Length) seeds = args[k + 1].Split(',').Select(int.Parse).ToArray();
                var r = Run(seeds);
                Debug.Log("[SkyTrack] course check: " + Summary(r));
            }
            catch (Exception e) { Debug.LogException(e); code = 3; }
            EditorApplication.Exit(code);
        }

        static string Summary(Result r) =>
            $"{r.courses.Count} courses x 3 lanes: {r.totalHits} hard hits, {r.totalStalls} stalls, {r.totalRespawns} respawns, {r.incomplete} runs incomplete, {r.totalTime:0} s driven";

        public static Result Run(int[] seeds)
        {
            var result = new Result();
            EditorSceneManager.OpenScene("Assets/SkyTrack/Scenes/TrackDemo.unity");
            var race = UnityEngine.Object.FindFirstObjectByType<SkyTrackRace>();
            var director = UnityEngine.Object.FindFirstObjectByType<RaceDirector>();
            if (!race || !director) throw new Exception("TrackDemo has no SkyTrackRace / RaceDirector");
            int seed0 = race.seed, set0 = race.pieceSet, len0 = race.length;
            var prevMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            string why = null;
            Action<RaceDriver, float, string> onRespawn = (d, at, reason) => why = reason;
            RaceDriver.Respawned += onRespawn;
            try
            {
                foreach (int set in new[] { 0, 1, 2 })
                    foreach (int len in new[] { 0, 1, 2 })
                        foreach (int seed in seeds)
                        {
                            race.seed = seed; race.pieceSet = set; race.length = len;
                            race.Build();
                            if (race.Layout == null) continue;
                            race.SetPropsMovable(true);
                            foreach (float lane in new[] { -6f, 0f, 6f })
                            {
                                var cr = DriveCourse(race, director, lane, result.hits, () => { var w = why; why = null; return w; });
                                result.courses.Add(cr);
                            }
                            EditorUtility.UnloadUnusedAssetsImmediate();
                        }
            }
            finally
            {
                RaceDriver.Respawned -= onRespawn;
                Physics.simulationMode = prevMode;
                race.seed = seed0; race.pieceSet = set0; race.length = len0;
                race.Build();
                director.SetupRace(true);
                director.Player.isPlayer = true;
                director.Player.Car.useKeyboard = true;
            }
            result.totalHits = result.courses.Sum(c => c.hits);
            result.totalStalls = result.courses.Sum(c => c.stalls);
            result.totalRespawns = result.courses.Sum(c => c.respawns);
            result.incomplete = result.courses.Count(c => !c.completed);
            result.totalTime = result.courses.Sum(c => c.time);
            Directory.CreateDirectory(Out);
            File.WriteAllText(Path.Combine(Out, "course_check.json"), JsonUtility.ToJson(result, true));
            File.WriteAllText(Path.Combine(Out, "course_check.md"), Markdown(result));
            return result;
        }

        static CourseResult DriveCourse(SkyTrackRace race, RaceDirector director, float lane, List<Hit> hits, Func<string> takeRespawn)
        {
            string id = $"#{race.seed} set {race.pieceSet} len {race.length}";
            director.SetupRace(false);                      // the player's car only
            var d = director.Player;
            var car = d.Car;
            var rb = car.GetComponent<Rigidbody>();
            var pose = director.Path.PoseAt(director.gridStart, lane, 1.2f);
            car.Teleport(pose.position, pose.rotation);
            car.SetRespawn(pose.position, pose.rotation);
            d.Begin(director.Path, director, lane);
            d.isPlayer = false;
            car.useKeyboard = false;
            director.countdown = 0f;
            var body = car.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.transform.IsChildOf(car.transform)).ToArray();
            foreach (var c in body) c.providesContacts = true;
            stepContact = null; stepImpulse = 0f;
            const float dt = 0.02f;
            float limit = director.FinishDistance;
            int maxSteps = Mathf.CeilToInt((director.Path.Length / 25f + 30f) / dt);
            var cr = new CourseResult { course = $"{id} lane {lane:+0;-0;0}", length = director.Path.Length };
            float vPrev = 0f, slowFor = 0f, lastHitAt = -100f;
            int step = 0;
            Physics.ContactEvent += OnContacts;
            try
            {
            for (step = 0; step < maxSteps && d.progress < limit; step++)
            {
                director.SimulateStep(dt);
                stepContact = null; stepImpulse = 0f; contactCar = car.transform;
                Physics.Simulate(dt);
                float v = Vector3.Dot(rb.velocity, car.transform.forward);
                string resp = takeRespawn();
                if (resp != null) { cr.respawns++; Record(hits, id, (int)lane, "respawn: " + resp, d, car, body, vPrev, v); vPrev = 0; slowFor = 0; continue; }
                // braking gives ~6 m/s^2 (0.12 m/s per step); anything near 1.5 m/s in one step is an impact
                bool impact = vPrev - v > 1.5f && vPrev > 8f && director.raceTime > 1f;
                if (impact && d.progress - lastHitAt > 6f)
                {
                    cr.hits++; lastHitAt = d.progress;
                    Record(hits, id, (int)lane, "hit", d, car, body, vPrev, v);
                }
                bool trying = car.throttle > 0.5f && car.GroundedWheels > 0 && director.raceTime > 3f;
                slowFor = trying && v < 4f ? slowFor + dt : 0f;
                if (slowFor > 0.8f && slowFor - dt <= 0.8f) { cr.stalls++; Record(hits, id, (int)lane, "stall", d, car, body, vPrev, v); }
                vPrev = v;
            }
            }
            finally { Physics.ContactEvent -= OnContacts; foreach (var c in body) c.providesContacts = false; }
            cr.time = step * dt;
            cr.progress = d.progress;
            cr.completed = d.progress >= limit;
            return cr;
        }

        static Collider stepContact; static float stepImpulse; static Vector3 stepNormal, stepPoint; static Transform contactCar;

        /// <summary>Strongest contact of the car's body in the last physics step (resolved contacts leave no penetration to find afterwards).</summary>
        static void OnContacts(PhysicsScene scene, Unity.Collections.NativeArray<ContactPairHeader>.ReadOnly pairs)
        {
            for (int i = 0; i < pairs.Length; i++)
            {
                var hdr = pairs[i];
                for (int j = 0; j < hdr.pairCount; j++)
                {
                    ref readonly var pr = ref hdr.GetContactPair(j);
                    Collider a = pr.collider, b = pr.otherCollider;
                    if (!a || !b || !contactCar) continue;
                    bool aCar = a.transform.IsChildOf(contactCar), bCar = b.transform.IsChildOf(contactCar);
                    if (aCar == bCar) continue;
                    float imp = pr.impulseSum.magnitude;
                    if (imp <= stepImpulse || pr.contactCount == 0) continue;
                    stepImpulse = imp; stepContact = aCar ? b : a;
                    var cp = pr.GetContactPoint(0);
                    stepNormal = aCar ? cp.normal : -cp.normal; stepPoint = cp.position;
                }
            }
        }

        static void Record(List<Hit> hits, string course, int lane, string kind, RaceDriver d, SkyTrackCar car, Collider[] body, float vBefore, float vAfter)
        {
            var h = new Hit { course = course, lane = lane, kind = kind, at = d.progress, speedBefore = vBefore * 3.6f, speedAfter = vAfter * 3.6f, wheelMinUp = 1f };
            foreach (var w in car.wheels)       // the wheel contacts of the step that hit (steep normals = wheel rays catching an edge)
                if (w.grounded) { h.wheelMinUp = Mathf.Min(h.wheelMinUp, Vector3.Dot(w.contactNormal, car.transform.up)); h.maxCompression = Mathf.Max(h.maxCompression, w.compression); }
            // what is the body touching? deepest penetration against anything that is not the car
            float best = -1f; Collider other = null; Vector3 dir = Vector3.zero;
            var b = new Bounds(car.transform.position, Vector3.zero);
            foreach (var c in body) b.Encapsulate(c.bounds);
            b.Expand(0.3f);
            foreach (var o in Physics.OverlapBox(b.center, b.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
            {
                if (o.transform.IsChildOf(car.transform)) continue;
                foreach (var c in body)
                {
                    if (c is MeshCollider mc && !mc.convex) continue;
                    if (Physics.ComputePenetration(c, c.transform.position, c.transform.rotation, o, o.transform.position, o.transform.rotation, out var n, out var dist) && dist > best)
                    { best = dist; other = o; dir = n; }
                }
            }
            if (stepContact && kind == "hit") { other = stepContact; dir = stepNormal; }
            var piece = other ? other.GetComponentInParent<TrackPiece>() : null;
            var refT = piece ? piece.transform : null;
            if (!refT)          // nearest piece to the car
            {
                float bd = float.MaxValue;
                foreach (var p in UnityEngine.Object.FindObjectsByType<TrackPiece>(FindObjectsSortMode.None))
                {
                    float dd = (p.transform.position - car.transform.position).sqrMagnitude;
                    foreach (var q in p.path) dd = Mathf.Min(dd, (p.transform.TransformPoint(q) - car.transform.position).sqrMagnitude);
                    if (dd < bd) { bd = dd; refT = p.transform; }
                }
            }
            h.piece = refT ? refT.name : "?";
            h.collider = other ? Rel(other.transform) + (other.attachedRigidbody && !other.attachedRigidbody.isKinematic ? " (loose prop)" : "") : "nothing touching";
            if (other && other == stepContact && kind == "hit") h.impulse = stepImpulse;
            h.local = refT ? refT.InverseTransformPoint(car.transform.position) : car.transform.position;
            h.normal = refT ? refT.InverseTransformDirection(dir) : dir;
            hits.Add(h);
        }

        static string Rel(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x && !x.GetComponent<TrackPiece>() && x.name != "Scenery"; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts);
        }

        static string PieceType(string name) => name.Length > 3 && name[2] == '_' ? name.Substring(3) : name;

        static string Markdown(Result r)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Course collision check").AppendLine();
            sb.AppendLine("- " + Summary(r));
            sb.AppendLine("- hard hit = forward speed drops > 5.4 km/h in one 20 ms step (braking manages ~0.4); stall = < 14 km/h for 0.8 s with the throttle open").AppendLine();
            sb.AppendLine("## Clusters (piece type, collider, piece-local position rounded to 4 units)").AppendLine();
            foreach (var g in r.hits.GroupBy(h => $"{h.kind,-16} {PieceType(h.piece),-8} {h.collider,-48} ({Mathf.Round(h.local.x / 4) * 4,4:0}, {Mathf.Round(h.local.y / 4) * 4,4:0}, {Mathf.Round(h.local.z / 4) * 4,4:0})").OrderByDescending(g => g.Count()))
            {
                var e = g.First();
                sb.AppendLine($"    {g.Count(),3} x {g.Key}  e.g. {e.course} lane {e.lane} at {e.at:0}: {e.speedBefore:0} -> {e.speedAfter:0} km/h, normal ({e.normal.x:0.00}, {e.normal.y:0.00}, {e.normal.z:0.00}); wheel n·up min {g.Min(x => x.wheelMinUp):0.00} median {g.Select(x => x.wheelMinUp).OrderBy(x => x).ElementAt(g.Count() / 2):0.00}, max compression {g.Max(x => x.maxCompression):0.00}, impulse max {g.Max(x => x.impulse):0} Ns");
            }
            sb.AppendLine().AppendLine("## Runs").AppendLine();
            foreach (var c in r.courses)
                sb.AppendLine($"    {c.course,-28} {(c.completed ? "done" : "INCOMPLETE"),-10} {c.time,6:0.0}s  {c.progress,5:0}/{c.length:0}  hits {c.hits}  stalls {c.stalls}  respawns {c.respawns}");
            return sb.ToString();
        }
    }
}
