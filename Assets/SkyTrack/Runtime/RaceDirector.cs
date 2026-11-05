using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SkyTrack
{
    /// <summary>Car stats as in the vehicle definitions (Data/Vehicles/01/Main_Min.obj .. Main_Max.obj).</summary>
    [System.Serializable]
    public struct VehicleStats
    {
        public float engineForce, maxSpeed, acceleration, steering, frontFriction, rearFriction;
        public static VehicleStats Lerp(VehicleStats a, VehicleStats b, float t) => new VehicleStats
        {
            engineForce = Mathf.Lerp(a.engineForce, b.engineForce, t), maxSpeed = Mathf.Lerp(a.maxSpeed, b.maxSpeed, t),
            acceleration = Mathf.Lerp(a.acceleration, b.acceleration, t), steering = Mathf.Lerp(a.steering, b.steering, t),
            frontFriction = Mathf.Lerp(a.frontFriction, b.frontFriction, t), rearFriction = Mathf.Lerp(a.rearFriction, b.rearFriction, t),
        };
        public void ApplyTo(SkyTrackCar c)
        {
            c.engineForce = engineForce; c.maxSpeedKmh = maxSpeed; c.acceleration = acceleration;
            c.steering = steering; c.frontFriction = frontFriction; c.rearFriction = rearFriction;
        }
    }

    /// <summary>
    /// Runs a race on the course built by <see cref="SkyTrackRace"/>: like the game, 9 / 12 / 15 / 18 racers
    /// (RACERS option x 3 + 9) on a grid three abreast (lanes -6 / 0 / +6), the player starting from the back
    /// row; opponents named from Data/Misc/RacersName.obj with stats between Main_Min and Main_Max by skill.
    /// Countdown, positions by distance along the race line, finish at the finish pad.
    /// Keys: O racers, K skill, T restart, Q / W kick left / right.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public class RaceDirector : MonoBehaviour
    {
        public SkyTrackRace race;
        [Range(0, 3)] public int racersOption = 0;
        [Range(0, 2)] public int skill = 1;
        public float gridStart = 45f, rowSpacing = 11f, laneSpacing = 6f;
        public float countdownSeconds = 3f;
        [Tooltip("How far before the end of the race line the finish counts (the line ends mid finish pad; its front rim is ~35 back)")]
        public float finishBeforeEnd = 35f;
        public int aiSeed = -1;                          // -1: random each race
        [Header("Player car (stock definition: 400 / 160 km/h)")]
        public float playerEngineForce = 600f;
        public float playerMaxSpeedKmh = 200f;

        public enum State { Idle, Countdown, Racing, Finished }
        public State state = State.Idle;
        public float raceTime, countdown;
        public RacePath Path { get; private set; }
        public readonly List<RaceDriver> drivers = new List<RaceDriver>();
        public RaceDriver Player { get; private set; }
        public bool Racing => state == State.Racing || state == State.Finished;
        public int RacerCount => racersOption * 3 + 9;
        /// <summary>Distance along the race line at which a car has finished.</summary>
        public float FinishDistance => Path == null ? 0f : Mathf.Max(Path.Length - Mathf.Max(finishBeforeEnd, 2f), 1f);

        Transform opponentsRoot;

        /// <summary>Builds the race line, (re)spawns the opponents and lines everybody up on the grid.</summary>
        public void SetupRace(bool spawnOpponents)
        {
            if (!race) race = GetComponent<SkyTrackRace>();
            ClearOpponents();
            drivers.Clear();
            state = State.Idle;
            if (race) race.EnsureLayout();
            if (!race || race.Layout == null || !race.library || !race.car) return;
            var lib = race.library;
            Path = new RacePath(lib.shapes, race.Layout);
            var rng = new System.Random(aiSeed < 0 ? Random.Range(0, int.MaxValue) : aiSeed);

            int total = spawnOpponents ? RacerCount : 1;
            int rows = (total + 2) / 3;
            opponentsRoot = new GameObject("Opponents").transform;
            opponentsRoot.SetParent(transform, false);
            var used = new HashSet<string>();
            for (int i = 0; i < total; i++)
            {
                bool player = i == total - 1;
                float lane = (i % 3 - 1) * laneSpacing;
                float dist = gridStart + rowSpacing * (rows - 1 - i / 3);
                var pose = Path.PoseAt(dist, lane, 1.2f);
                SkyTrackCar car;
                if (player) car = race.car;
                else
                {
                    if (!lib.car) break;
                    var go = Instantiate(lib.car, pose.position, pose.rotation, opponentsRoot);
                    car = go.GetComponent<SkyTrackCar>();
                    car.useKeyboard = false;
                    Dress(go, rng, i);
                }
                car.Init();
                car.Teleport(pose.position, pose.rotation);     // moves the Rigidbody too (interpolated bodies)
                car.SetRespawn(pose.position, pose.rotation);
                var d = car.GetComponent<RaceDriver>();
                if (!d) d = car.gameObject.AddComponent<RaceDriver>();
                d.isPlayer = player;
                d.skill = skill;
                if (player)
                {
                    d.driverName = "Player";
                    var st = lib.statsMin;
                    st.engineForce = playerEngineForce;
                    st.maxSpeed = playerMaxSpeedKmh;
                    st.ApplyTo(car);
                }
                else
                {
                    d.driverName = PickName(lib, rng, used);
                    float t = Mathf.Clamp01(new[] { 0f, 0.15f, 0.4f }[skill] + (float)rng.NextDouble() * 0.3f);
                    VehicleStats.Lerp(lib.statsMin, lib.statsMax, t).ApplyTo(car);
                    d.pace = 0.93f + (float)rng.NextDouble() * 0.07f;
                }
                d.Begin(Path, this, lane);
                drivers.Add(d);
                if (player) Player = d;
            }
            raceTime = 0;
            countdown = countdownSeconds;
            state = State.Countdown;
            foreach (var d in drivers) d.Car.inputLocked = true;
            if (race.chase) race.chase.Snap();
            if (Application.isPlaying && System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-skytrackAutotest") >= 0 && !GetComponent<RaceAutotest>())
                gameObject.AddComponent<RaceAutotest>();
            Debug.Log($"[SkyTrack] race on track #{race.seed}: {drivers.Count} racers, skill {skill}, line {Path.Length:0} units");
        }

        public void ClearOpponents()
        {
            foreach (var d in drivers) if (d && !d.isPlayer && (!race || d.Car != race.car)) Kill(d.gameObject);   // never the scene's own car
            var old = transform.Find("Opponents");
            if (old) Kill(old.gameObject);
        }

        static void Kill(GameObject g) { if (Application.isPlaying) Destroy(g); else DestroyImmediate(g); }

        void Dress(GameObject go, System.Random rng, int i)
        {
            go.name = $"Opponent_{i:00}";
            var paint = go.GetComponent<VehiclePaint>();
            if (paint)
            {
                paint.paint = Color.HSVToRGB((float)rng.NextDouble(), 0.55f + (float)rng.NextDouble() * 0.4f, 0.35f + (float)rng.NextDouble() * 0.6f);
                paint.stripes = rng.Next(3) == 0 ? new Color(0.08f, 0.08f, 0.09f) : new Color(0.92f, 0.92f, 0.9f);
                paint.decal = paint.decals != null && paint.decals.Length > 0 ? rng.Next(paint.decals.Length) : 0;
                paint.Apply();
            }
            var src = go.GetComponent<AudioSource>();
            if (src) { src.spatialBlend = 1f; src.volume = 0.35f; src.rolloffMode = AudioRolloffMode.Linear; src.minDistance = 4f; src.maxDistance = 140f; }
        }

        static string PickName(TrackLibrary lib, System.Random rng, HashSet<string> used)
        {
            for (int tries = 0; tries < 20; tries++)
            {
                bool woman = rng.Next(2) == 0;
                var first = woman ? lib.womenFirst : lib.menFirst;
                var last = woman ? lib.womenLast : lib.menLast;
                if (first == null || last == null || first.Length == 0 || last.Length == 0) return "Racer";
                string n = first[rng.Next(first.Length)] + " " + last[rng.Next(last.Length)];
                if (used.Add(n)) return n;
            }
            return "Racer " + used.Count;
        }

        void FixedUpdate() { if (Application.isPlaying) Tick(Time.fixedDeltaTime); }

        /// <summary>Race clock, countdown and standings; drivers steer themselves in their own FixedUpdate.</summary>
        public void Tick(float dt)
        {
            if (state == State.Idle) return;
            UpdateStandings();
            if (state == State.Countdown)
            {
                countdown -= dt;
                if (countdown <= 0f)
                {
                    state = State.Racing;
                    foreach (var d in drivers) d.Car.inputLocked = false;
                }
                return;
            }
            raceTime += dt;
            if (Application.isPlaying && Mathf.FloorToInt(raceTime / 10f) != Mathf.FloorToInt((raceTime - dt) / 10f))
                Debug.Log($"[SkyTrack] t={raceTime:0}s " + string.Join(", ", drivers.OrderBy(d => d.position).Take(3).Select(d => $"{d.position}. {d.driverName} {d.progress:0}")) + $" | player {Player?.position} {Player?.progress:0}");
        }

        /// <summary>Live order: finishers by finish time, then everybody else by distance along the line.
        /// Fixed positions (<see cref="RaceDriver.finalPosition"/>) never move.</summary>
        public void UpdateStandings()
        {
            if (state == State.Finished) { foreach (var d in drivers) d.position = d.finalPosition; return; }
            var order = drivers.OrderByDescending(d => d.finished)
                               .ThenBy(d => d.finished ? d.finishTime : 0f)
                               .ThenByDescending(d => d.progress).ToList();
            for (int i = 0; i < order.Count; i++) order[i].position = i + 1;
        }

        /// <summary>Edit-mode simulation step (Physics.simulationMode = Script): director, drivers, cars.</summary>
        public void SimulateStep(float dt)
        {
            Tick(dt);
            foreach (var d in drivers) d.Drive(dt);
            foreach (var d in drivers) d.Car.Step(dt);
        }

        public void Finished(RaceDriver d)
        {
            if (d.finished) return;
            d.finished = true;
            if (state == State.Finished) return;                      // classification (and projected time) already fixed
            d.finishTime = raceTime;
            d.finalPosition = drivers.Count(o => o.finished);
            d.position = d.finalPosition;
            if (d == Player) FinishRace();
        }

        /// <summary>The player crossed the line: fix the classification. Cars still on the course keep their
        /// order on the line and get a finish time projected from their average pace.</summary>
        void FinishRace()
        {
            int next = drivers.Count(o => o.finished);
            foreach (var o in drivers.Where(o => !o.finished).OrderByDescending(o => o.progress))
            {
                float pace = Mathf.Max(o.progress / Mathf.Max(raceTime, 1f), 5f);
                o.finalPosition = ++next;
                o.finishTime = raceTime + Mathf.Max(0f, FinishDistance - o.progress) / pace;
                o.estimated = true;
            }
            state = State.Finished;
            Player.Car.inputLocked = true;                            // coast to a stop past the line
            UpdateStandings();
            Debug.Log($"[SkyTrack] finish: player {Ordinal(Player.finalPosition)} of {drivers.Count} in {FormatTime(Player.finishTime)} | " +
                      string.Join(", ", drivers.OrderBy(o => o.finalPosition).Select(o => $"{o.finalPosition}. {o.driverName} {(o.estimated ? "~" : "")}{FormatTime(o.finishTime)}")));
        }

        /// <summary>Nearest car ahead in (about) the same lane.</summary>
        public RaceDriver Ahead(RaceDriver me, float range, float width)
        {
            RaceDriver best = null; float bd = range;
            foreach (var o in drivers)
            {
                if (o == me) continue;
                float dp = o.progress - me.progress;
                if (dp <= 0 || dp >= bd) continue;
                if (Mathf.Abs(Vector3.Dot(o.transform.position - me.transform.position, me.transform.right)) > width) continue;
                best = o; bd = dp;
            }
            return best;
        }

        /// <summary>A car right beside <paramref name="me"/> (side -1 = left, +1 = right; 0 = either).</summary>
        public RaceDriver Alongside(RaceDriver me, out int side, int wantSide = 0)
        {
            side = 0;
            foreach (var o in drivers)
            {
                if (o == me) continue;
                Vector3 rel = o.transform.position - me.transform.position;
                float fwd = Vector3.Dot(rel, me.transform.forward), lat = Vector3.Dot(rel, me.transform.right);
                if (Mathf.Abs(fwd) > 4.5f || Mathf.Abs(lat) < 1.5f || Mathf.Abs(lat) > 7.5f || Mathf.Abs(Vector3.Dot(rel, me.transform.up)) > 3f) continue;
                int s = lat > 0 ? 1 : -1;
                if (wantSide != 0 && s != wantSide) continue;
                side = s;
                return o;
            }
            return null;
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        void Update()
        {
            bool restart = false;
            if (Input.GetKeyDown(KeyCode.O)) { racersOption = (racersOption + 1) % 4; restart = true; }
            if (Input.GetKeyDown(KeyCode.K)) { skill = (skill + 1) % 3; restart = true; }
            if (Input.GetKeyDown(KeyCode.T)) restart = true;
            if (restart && race && race.Layout != null) SetupRace(true);
        }
#endif

        static GUIStyle big, rank, label, row, rowRight, small;
        static Texture2D shade;

        void OnGUI()
        {
            if (state == State.Idle || Player == null) return;
            if (big == null)
            {
                big = new GUIStyle(GUI.skin.label) { fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                rank = new GUIStyle(GUI.skin.label) { fontSize = 44, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft, richText = true };
                label = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
                row = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, richText = true };
                rowRight = new GUIStyle(row) { alignment = TextAnchor.MiddleRight };
                small = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.UpperRight };
                shade = new Texture2D(1, 1); shade.SetPixel(0, 0, Color.white); shade.Apply();
            }
            float w = Screen.width;
            StandingsPanel(new Rect(w - 322, 10, 312, 0));

            if (state == State.Countdown)
                GUI.Label(new Rect(0, Screen.height * 0.3f, w, 100), Mathf.CeilToInt(countdown).ToString(), big);
            else if (raceTime < 1.2f)
                GUI.Label(new Rect(0, Screen.height * 0.3f, w, 100), "GO!", big);

            if (state == State.Finished)
            {
                var order = drivers.OrderBy(d => d.finalPosition).ToList();
                var box = new Rect(w / 2 - 230, Screen.height * 0.16f, 460, 96 + 20 * order.Count);
                Fill(box, new Color(0f, 0f, 0f, 0.7f));
                GUI.Label(new Rect(box.x, box.y + 8, box.width, 50), $"FINISHED {Ordinal(Player.finalPosition).ToUpperInvariant()}", new GUIStyle(big) { fontSize = 36 });
                float y = box.y + 62;
                foreach (var d in order)
                {
                    var r = new Rect(box.x + 16, y, box.width - 32, 20);
                    if (d == Player) Fill(r, new Color(1f, 0.8f, 0.1f, 0.3f));
                    GUI.Label(new Rect(r.x, r.y, 34, r.height), $"{d.finalPosition}.", rowRight);
                    GUI.Label(new Rect(r.x + 44, r.y, 260, r.height), d == Player ? "<b>YOU</b>" : d.driverName, row);
                    GUI.Label(new Rect(r.xMax - 120, r.y, 120, r.height), (d.estimated ? "~" : "") + FormatTime(d.finishTime), rowRight);
                    y += 20;
                }
                GUI.Label(new Rect(box.x, y + 6, box.width - 16, 20), (order.Any(d => d.estimated) ? "~ still racing: time projected from pace  ·  " : "") + "T restart · N new track", small);
            }
        }

        /// <summary>Top-right HUD: the player's position, time and speed, then every racer in race order with
        /// the gap to the player along the line (finishers show their time).</summary>
        void StandingsPanel(Rect at)
        {
            var order = drivers.OrderBy(d => d.position).ToList();
            const float rowH = 19f;
            var box = new Rect(at.x, at.y, at.width, 104 + rowH * order.Count + 22);
            Fill(box, new Color(0f, 0f, 0f, 0.45f));
            float x = box.x + 10, wIn = box.width - 20;
            GUI.Label(new Rect(x, box.y + 4, wIn, 52), $"{Ordinal(Player.position)}<size=22> / {drivers.Count}</size>", rank);
            GUI.Label(new Rect(x, box.y + 8, wIn, 24), FormatTime(Player.finished ? Player.finishTime : raceTime), label);
            GUI.Label(new Rect(x, box.y + 32, wIn, 24), $"{Mathf.Max(0, Player.Car.SpeedKmh):0} km/h", label);
            float pct = 100f * Mathf.Clamp01(Player.progress / FinishDistance);
            var bar = new Rect(x, box.y + 66, wIn, 6);
            Fill(bar, new Color(1f, 1f, 1f, 0.2f));
            Fill(new Rect(bar.x, bar.y, bar.width * pct / 100f, bar.height), new Color(1f, 0.8f, 0.1f, 0.9f));
            GUI.Label(new Rect(x, box.y + 74, wIn, 20), $"{pct:0}% of the course", small);

            float y = box.y + 98;
            foreach (var d in order)
            {
                var r = new Rect(x, y, wIn, rowH);
                bool me = d == Player;
                if (me) Fill(r, new Color(1f, 0.8f, 0.1f, 0.35f));
                string gap;
                if (d.finished || d.estimated) gap = (d.estimated ? "~" : "") + FormatTime(d.finishTime);
                else if (me) gap = "";
                else { float g = d.progress - Player.progress; gap = $"{(g >= 0 ? "+" : "-")}{Mathf.Abs(g):0} m"; }
                string name = me ? "<b>YOU</b>" : d.driverName;
                GUI.Label(new Rect(r.x, r.y, 26, rowH), $"{d.position}", rowRight);
                GUI.Label(new Rect(r.x + 34, r.y, wIn - 34 - 76, rowH), name, row);
                GUI.Label(new Rect(r.xMax - 76, r.y, 74, rowH), gap, rowRight);
                y += rowH;
            }
            GUI.Label(new Rect(x, y + 2, wIn, 18), $"O racers · K skill {skill} · T restart · Q/W kick", small);
        }

        static void Fill(Rect r, Color c)
        {
            var old = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, shade);
            GUI.color = old;
        }

        static string FormatTime(float t) => $"{(int)(t / 60)}:{t % 60:00.0}";
        static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
    }
}
