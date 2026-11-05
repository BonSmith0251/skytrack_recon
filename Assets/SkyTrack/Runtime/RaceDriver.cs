using UnityEngine;

namespace SkyTrack
{
    /// <summary>
    /// One racer: tracks progress along the <see cref="RacePath"/>, keeps the car's respawn point on the
    /// track, and (for opponents) drives it. The AI aims at a look-ahead point in its lane, limits its speed
    /// by the turning ahead (loops and jumps are taken flat out), changes lane to pass slower cars, kicks
    /// cars alongside (the game's Q / W kicks), and recovers when stuck or off the track.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(SkyTrackCar))]
    public class RaceDriver : MonoBehaviour
    {
        public string driverName = "Player";
        public bool isPlayer;
        [Range(0, 2)] public int skill = 1;
        [Tooltip("Speed factor on top of the car's stats (driver talent)")]
        public float pace = 1f;
        public float lane;                         // lateral offset from the centre line
        [Header("Race state (read-only)")]
        public float progress;
        public int position;
        public bool finished;
        public float finishTime;
        public int finalPosition;                   // fixed on crossing the line, or for everyone when the player does (0 = open)
        public bool estimated;                      // finishTime projected from the pace when the player finished
        public int respawns;

        [System.NonSerialized] public RacePath path;
        [System.NonSerialized] public RaceDirector director;
        public SkyTrackCar Car { get; private set; }

        int index;
        float laneTarget, laneNoise, stuckTimer, offTrackTimer, kickCooldown, lastProgress;
        Rigidbody rb;
        const float TrackHalfWidth = 14f;

        void Awake() { Car = GetComponent<SkyTrackCar>(); rb = GetComponent<Rigidbody>(); laneNoise = Random.value * 100f; }

        public void Begin(RacePath p, RaceDirector d, float startLane)
        {
            path = p; director = d;
            if (!Car) Awake();
            lane = laneTarget = startLane;
            index = path.Nearest(transform.position, 0, 0, path.Count);
            progress = lastProgress = path.Project(transform.position, index);
            finished = estimated = false; finishTime = 0; finalPosition = 0; respawns = 0; stuckTimer = offTrackTimer = 0; lastRespawnAt = -1000f;
            Car.SetRespawn(transform.position, transform.rotation);
            Car.respawnHandler = RespawnOnTrack;
        }

        void FixedUpdate() { if (Application.isPlaying) Drive(Time.fixedDeltaTime); }

        /// <summary>One control step; the director calls it directly in edit-mode simulations.</summary>
        public void Drive(float dt)
        {
            if (path == null || path.Count < 2) return;
            // follow the line sample by sample; ignore other parts of the course that pass nearby
            int cand = path.Nearest(transform.position, index, 8, 12);
            if ((path.points[cand] - transform.position).sqrMagnitude < 30f * 30f) index = cand;
            float maxStep = Mathf.Abs(SpeedMs) * dt * 1.5f + 1f;
            progress = Mathf.Max(progress, Mathf.Min(path.Project(transform.position, index), progress + maxStep));
            if (!finished && director && progress >= director.FinishDistance) director.Finished(this);
            UpdateRespawnPoint();
            kickCooldown -= dt;
            if (isPlayer) Recover(dt);

            bool racing = director == null || director.Racing;
            if (isPlayer)
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                if (Application.isPlaying && racing && kickCooldown <= 0)
                {
                    if (Input.GetKeyDown(KeyCode.Q)) KickSide(-1);
                    if (Input.GetKeyDown(KeyCode.W)) KickSide(1);
                }
#endif
                return;
            }
            if (!racing) { Car.SetInput(0, 0); return; }
            if (finished) { Car.SetInput(SpeedMs > 8f ? -0.6f : 0f, Steer(14f)); return; }

            float v = SpeedMs;
            ChooseLane(dt);
            float look = Mathf.Clamp(9f + v * 0.55f, 10f, 34f);
            float steer = Steer(look);

            // speed limit from the turning ahead: v = sqrt(aLat * R), with room to brake before the bend
            float grip = 21f * (0.8f + 0.12f * skill);
            float limit = Car.maxSpeedKmh / 3.6f * pace;
            for (float d = 15f; d <= 90f; d += 15f)
            {
                float ang = Mathf.Abs(path.TurnAngle(progress + d - 15f, 15f + 10f));
                if (ang < 0.05f) continue;
                float radius = 25f / ang;
                float vc = Mathf.Sqrt(grip * radius);
                limit = Mathf.Min(limit, Mathf.Sqrt(vc * vc + 2f * 14f * Mathf.Max(0f, d - 15f)));
            }
            float throttle = v < limit - 1f ? 1f : v > limit + 3f ? -0.7f : 0.25f;
            Car.SetInput(throttle, steer);

            // kicks: shove a car that is right alongside
            if (kickCooldown <= 0 && director && Random.value < 0.02f * (1 + skill))
            {
                var other = director.Alongside(this, out int side);
                if (other) KickSide(side);
            }
            Recover(dt);
        }

        float SpeedMs => rb ? Vector3.Dot(rb.velocity, transform.forward) : 0f;

        public float AutoSteer(float look) => Steer(look);

        float Steer(float look)
        {
            Vector3 target = path.PointAt(progress + look, lane);
            Vector3 local = transform.InverseTransformPoint(target);
            float angle = Mathf.Atan2(local.x, Mathf.Max(local.z, 0.5f)) * Mathf.Rad2Deg;
            return Mathf.Clamp(angle / Mathf.Max(Car.maxSteerAngle * 0.8f, 1f), -1f, 1f);
        }

        float hazardTimer;
        bool dodging;
        static readonly float[] Lanes = { -9f, 0f, 9f };

        /// <summary>
        /// True if the deck at (d, lateral) has a pit (deck variants 1..3 cut a 7 x 14 hole into one lane) or
        /// a box pile on it. Gaps across the whole deck (jumps) are not hazards: see <see cref="LaneHazardAhead"/>.
        /// </summary>
        public bool Hazard(float d, float lateral)
        {
            if (d >= path.Length) return false;
            int i = path.IndexAt(d);
            Vector3 origin = path.PointAt(d, lateral) + path.up[i] * 3f;
            float deck = float.MaxValue;
            foreach (var h in Physics.RaycastAll(origin, -path.up[i], 4.6f, ~0, QueryTriggerInteraction.Ignore))
                if (!h.rigidbody) deck = Mathf.Min(deck, h.distance);
            if (deck > 4.3f) return true;
            // boxes and signs (loose decor bodies) standing in the lane
            Vector3 c = origin - path.up[i] * (deck - 1.8f);
            foreach (var col in Physics.OverlapSphere(c, 2.4f, ~0, QueryTriggerInteraction.Ignore))
                if (col.attachedRigidbody && !col.attachedRigidbody.GetComponent<SkyTrackCar>()) return true;
            return false;
        }

        /// <summary>First distance ahead (within range) where this lane is blocked but some other lane is not.</summary>
        float LaneHazardAhead(float lateral, float from, float to)
        {
            for (float d = from; d <= to; d += 5f)
            {
                if (!Hazard(progress + d, lateral)) continue;
                foreach (var l in Lanes) if (Mathf.Abs(l - lateral) > 3f && !Hazard(progress + d, l)) return d;   // not a jump gap
            }
            return -1f;
        }

        bool LaneClear(float lateral, float from, float to)
        {
            for (float d = from; d <= to; d += 5f) if (Hazard(progress + d, lateral)) return false;
            return true;
        }

        void ChooseLane(float dt)
        {
            // wander a little; move over to pass a slower car ahead in the same lane
            float wander = (Mathf.PerlinNoise(laneNoise, Time.time * 0.07f) - 0.5f) * 8f;
            if (director)
            {
                var ahead = director.Ahead(this, 30f, 4f);
                if (ahead && ahead.SpeedOf() < SpeedMs + 3f)
                    laneTarget = ahead.lane > 0 ? -7f : 7f;
                else if (Mathf.Abs(laneTarget - wander) > 6f && Random.value < 0.01f)
                    laneTarget = wander;
            }
            laneTarget = Mathf.Clamp(laneTarget, -TrackHalfWidth + 4f, TrackHalfWidth - 4f);

            // pits and box piles: look up to ~2 s ahead and move to a free lane early
            hazardTimer -= dt;
            if (hazardTimer <= 0f)
            {
                hazardTimer = 0.1f;
                float reach = Mathf.Clamp(SpeedMs * 2f, 30f, 80f);
                float hz = Mathf.Max(LaneHazardAhead(lane, 4f, reach), LaneHazardAhead(laneTarget, 4f, reach));
                dodging = false;
                if (hz >= 0f)
                {
                    float bestLane = laneTarget, bestCost = float.MaxValue;
                    foreach (var l in Lanes)
                        if (LaneClear(l, Mathf.Max(4f, hz - 10f), hz + 20f) && Mathf.Abs(l - lane) < bestCost) { bestCost = Mathf.Abs(l - lane); bestLane = l; }
                    laneTarget = bestLane;
                    dodging = true;
                }
            }
            lane = Mathf.MoveTowards(lane, laneTarget, (dodging ? 9f : 3.5f) * dt);
        }

        public float SpeedOf() => SpeedMs;

        void KickSide(int side)
        {
            if (!director) return;
            var other = director.Alongside(this, out int s, side);
            kickCooldown = 1.5f;
            if (!other) return;
            other.Car.Kick(transform.right * s * 7f);
            Car.Kick(-transform.right * s * 1.5f);
        }

        /// <summary>Start of a gap across the whole deck (a jump) within <paramref name="range"/> after d, or -1.</summary>
        float GapAhead(float d, float range)
        {
            for (float dd = d; dd <= Mathf.Min(path.Length - 1f, d + range); dd += 4f)
                if (Hazard(dd, -9f) && Hazard(dd, 0f) && Hazard(dd, 9f)) return dd;
            return -1f;
        }

        /// <summary>First distance at or after d with deck under the race line (jumps have gaps).</summary>
        float DeckAhead(float d)
        {
            for (float dd = d; dd < Mathf.Min(path.Length, d + 160f); dd += 4f)
            {
                int i = path.IndexAt(dd);
                var origin = path.PointAt(dd) + path.up[i] * 3f;
                foreach (var hit in Physics.RaycastAll(origin, -path.up[i], 8f, ~0, QueryTriggerInteraction.Ignore))
                    if (!hit.rigidbody) return dd;
            }
            return Mathf.Min(d, path.Length - 10f);
        }

        void UpdateRespawnPoint()
        {
            // respawn a little behind the furthest point reached on the deck
            if (Car.GroundedWheels >= 2 && OnTrack()) lastProgress = progress;
            var pose = path.PoseAt(Mathf.Max(0f, lastProgress - 8f), Mathf.Clamp(lane, -6f, 6f), 1.4f);
            Car.SetRespawn(pose.position, pose.rotation);
        }

        bool OnTrack()
        {
            Vector3 p = path.points[index];
            Vector3 local = transform.position - p;
            float h = Vector3.Dot(local, path.up[index]);
            // in the air (jumps) the car may arc well above the line; below it, it has fallen off
            return Mathf.Abs(Vector3.Dot(local, path.right[index])) < TrackHalfWidth + 4f && h > -12f && h < (Car.GroundedWheels == 0 ? 35f : 12f);
        }

        void Recover(float dt)
        {
            bool racing = director == null || director.Racing;
            if (!racing || finished) return;
            bool slow = Mathf.Abs(SpeedMs) < 1.5f;
            // AI: stuck anywhere; player: only when stranded (no wheels down) or upside down, not when waiting
            bool stuck = isPlayer ? slow && (Car.GroundedWheels == 0 || Vector3.Dot(transform.up, path.up[index]) < 0.2f) : slow;
            stuckTimer = stuck ? stuckTimer + dt : 0f;
            offTrackTimer = !OnTrack() ? offTrackTimer + dt : 0f;
            if (stuckTimer > (isPlayer ? 2.5f : 3.5f)) { respawnReason = "stuck"; RespawnOnTrack(); }
            else if (offTrackTimer > (isPlayer ? 3f : 2.5f)) { respawnReason = "off track"; RespawnOnTrack(); }
        }

        float lastRespawnAt = -1000f;
        string respawnReason = "fell";
        /// <summary>(driver, distance along the line, reason) for every respawn; diagnostics / tests.</summary>
        public static event System.Action<RaceDriver, float, string> Respawned;

        public void RespawnOnTrack()
        {
            Respawned?.Invoke(this, lastProgress, respawnReason);
            respawnReason = "fell";
            respawns++;
            stuckTimer = offTrackTimer = 0;
            float at = Mathf.Max(0f, lastProgress - 8f);
            float gapAt = GapAhead(lastProgress, 50f);
            if (gapAt >= 0f) at = DeckAhead(gapAt + 4f) + 6f;                         // fell into a jump gap: land side
            else if (Mathf.Abs(at - lastRespawnAt) < 12f) at = DeckAhead(at + 30f);   // failed here twice: skip the obstacle
            lastRespawnAt = at;
            lastProgress = Mathf.Max(lastProgress, at + 8f);
            // a lane of the deck with no car on it
            float best = 0f, bestGap = -1f;
            foreach (float l in new[] { Mathf.Clamp(lane, -6f, 6f), 0f, -6f, 6f })
            {
                if (Hazard(at, l) || Hazard(at + 8f, l)) continue;
                var p = path.PointAt(at, l);
                float gap = float.MaxValue;
                if (director) foreach (var o in director.drivers) if (o != this) gap = Mathf.Min(gap, Vector3.Distance(o.transform.position, p));
                if (gap > bestGap) { bestGap = gap; best = l; }
                if (gap > 7f) break;
            }
            lane = laneTarget = best;
            var pose = path.PoseAt(at, best, 1.4f);
            Car.SetRespawn(pose.position, pose.rotation);
            Car.Respawn();
            index = path.IndexAt(at);
            progress = Mathf.Max(progress, at);
        }
    }
}
