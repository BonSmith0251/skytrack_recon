using UnityEngine;

namespace SkyTrack
{
    /// <summary>
    /// Raycast-suspension arcade car. Parameters are seeded from the vehicle definition
    /// (Base Engine Force / Max Speed / Steering, tyre friction, suspension) by the rebuilder.
    /// While the wheels touch the track, gravity follows the track normal so loops are drivable.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class SkyTrackCar : MonoBehaviour
    {
        [System.Serializable]
        public class Wheel
        {
            public Transform visual;
            public Vector3 anchor;          // local wheel centre at rest
            public float radius = 0.45f;
            public bool front;
            public bool driven = true;
            [System.NonSerialized] public bool grounded;
            [System.NonSerialized] public float compression, spin;
            [System.NonSerialized] public Vector3 contactNormal;
        }

        public Wheel[] wheels = new Wheel[0];
        [Header("From vehicle definition")]
        public float engineForce = 400f;          // definition units
        public float maxSpeedKmh = 160f;
        public float acceleration = 0.2f;
        public float steering = 0.6f;
        public float frontFriction = 1.5f, rearFriction = 2.0f;
        public float suspensionStiffness = 150f, suspensionDamping = 15f;
        [Header("Tuning")]
        public float suspensionTravel = 0.45f;
        [Tooltip("Compression (fraction of travel) where the bump stop starts: the body hull touches the deck at ~1.0, " +
                 "so this keeps it off the road in dips and at the foot of climbs")]
        public float bumpStopStart = 0.7f;
        public float bumpStopStiffness = 8f;        // x the spring rate, rising with the square of the overtravel
        public float maxSteerAngle = 32f;
        public float gravity = 25f;
        [Tooltip("Gravity while no wheel touches the deck. The AH_020 jump (24 degree lip, 80-unit gap, landing ramp to z 172) " +
                 "lands from ~150 km/h to the 200 km/h top speed at 20; at 9.81 every jump overshoots onto the next piece")]
        public float airGravity = 20f;
        public float trackStick = 1f;               // 0 = world gravity only
        public AudioSource engineAudio;

        [Header("Input (read-only at runtime)")]
        public float throttle;
        public float steer;
        public bool useKeyboard = true;
        [Tooltip("Held at the start line (countdown): no drive, wheels braked")]
        public bool inputLocked;

        Rigidbody rb;
        Vector3 gravityDir = Vector3.down;
        Vector3 spawnPos;
        Quaternion spawnRot;

        public float SpeedKmh => rb ? Vector3.Dot(rb.velocity, transform.forward) * 3.6f : 0f;
        public int GroundedWheels { get; private set; }

        void Awake() { Init(); }

        static PhysicMaterial bodyMaterial;
        static PhysicMaterial BodyMaterial => bodyMaterial ? bodyMaterial : bodyMaterial = new PhysicMaterial("CarBody")
        {
            dynamicFriction = 0.05f, staticFriction = 0.05f, bounciness = 0f,
            frictionCombine = PhysicMaterialCombine.Minimum, bounceCombine = PhysicMaterialCombine.Minimum,
        };

        public void Init()
        {
            rb = GetComponent<Rigidbody>();
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.centerOfMass = new Vector3(0, -0.35f, 0);
            // the body must slide when it touches the deck or a wall (default friction grabs and stops the car dead)
            foreach (var c in GetComponentsInChildren<Collider>(true))
                if (!c.isTrigger && !c.sharedMaterial) c.sharedMaterial = BodyMaterial;
            spawnPos = transform.position;
            spawnRot = transform.rotation;
        }

        public void SetInput(float throttle, float steer)
        {
            if (inputLocked) throttle = 0;
            this.throttle = Mathf.Clamp(throttle, -1, 1);
            this.steer = Mathf.Clamp(steer, -1, 1);
        }

        void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (useKeyboard)
            {
                SetInput(Input.GetAxis("Vertical"), Input.GetAxis("Horizontal"));
                if (Input.GetKeyDown(KeyCode.R)) respawnRequested = true;
            }
#endif
            foreach (var w in wheels)
            {
                if (!w.visual) continue;
                w.spin += Vector3.Dot(rb.velocity, transform.forward) / Mathf.Max(w.radius, 0.05f) * Mathf.Rad2Deg * Time.deltaTime;
                float yaw = w.front ? steer * maxSteerAngle * Mathf.Clamp01(steering / 0.6f) : 0f;
                w.visual.localPosition = w.anchor + Vector3.up * (w.compression - suspensionTravel * 0.5f);
                w.visual.localRotation = Quaternion.Euler(0, yaw, 0) * Quaternion.Euler(w.spin, 0, 0);
            }
            if (engineAudio)
                engineAudio.pitch = 0.6f + Mathf.Abs(SpeedKmh) / Mathf.Max(maxSpeedKmh, 1f) * 1.4f;
        }

        void FixedUpdate() { Step(Time.fixedDeltaTime); }

        /// <summary>Where <see cref="Respawn"/> (R key, falling off) puts the car; the race moves it along the track.</summary>
        public void SetRespawn(Vector3 pos, Quaternion rot) { spawnPos = pos; spawnRot = rot; }

        /// <summary>A side kick (the game's Q / W): velocity change applied at the centre of mass.</summary>
        public void Kick(Vector3 deltaV)
        {
            if (!rb) Init();
            rb.AddForce(deltaV, ForceMode.VelocityChange);
        }

        /// <summary>
        /// Moves the car (Transform and Rigidbody together: with interpolation, moving only the Transform is
        /// undone by the body), stops it and points gravity at the deck under the new pose.
        /// </summary>
        public void Teleport(Vector3 pos, Quaternion rot)
        {
            if (!rb) Init();
            transform.SetPositionAndRotation(pos, rot);
            rb.position = pos;
            rb.rotation = rot;
            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            gravityDir = -(rot * Vector3.up);
            foreach (var w in wheels) w.compression = 0;
            Physics.SyncTransforms();
        }

        public void Respawn() => Teleport(spawnPos, spawnRot);

        /// <summary>Set by the R key (Update); carried out in the next physics step.</summary>
        public bool respawnRequested;
        /// <summary>Replaces the plain respawn (the race puts the car back on the track instead).</summary>
        public System.Action respawnHandler;

        /// <summary>One physics step; callable manually (Physics.simulationMode = Script) for tests.</summary>
        public void Step(float dt)
        {
            if (!rb) Init();
            if (respawnRequested)
            {
                respawnRequested = false;
                if (respawnHandler != null) respawnHandler(); else Respawn();
            }
            float mass = rb.mass;
            float springK = suspensionStiffness / 150f * mass * gravity / (wheels.Length * suspensionTravel * 0.5f);
            float damper = suspensionDamping / 15f * 2f * Mathf.Sqrt(springK * mass / Mathf.Max(wheels.Length, 1)) * 0.6f;
            Vector3 normalSum = Vector3.zero;
            GroundedWheels = 0;

            foreach (var w in wheels)
            {
                Vector3 top = transform.TransformPoint(w.anchor + Vector3.up * suspensionTravel * 0.5f);
                Vector3 down = -transform.up;
                float len = suspensionTravel + w.radius;
                w.grounded = Physics.Raycast(top, down, out var hit, len, ~0, QueryTriggerInteraction.Ignore)
                             && !hit.collider.transform.IsChildOf(transform);
                if (!w.grounded) { w.compression = 0; continue; }
                GroundedWheels++;
                normalSum += hit.normal;
                w.contactNormal = hit.normal;
                float comp = len - hit.distance;
                Vector3 pv = rb.GetPointVelocity(hit.point);
                float compSpeed = -Vector3.Dot(pv, transform.up);
                float spring = Mathf.Max(0, comp * springK + compSpeed * damper);
                float over = comp / Mathf.Max(suspensionTravel, 0.01f) - bumpStopStart;
                if (over > 0) spring += springK * suspensionTravel * bumpStopStiffness * over * (1f + 4f * over) + Mathf.Max(0, compSpeed) * damper * 2f;
                w.compression = comp;
                rb.AddForceAtPosition(transform.up * spring, hit.point);

                // tyre: steering direction, drive and lateral grip
                float yaw = w.front ? steer * maxSteerAngle * Mathf.Clamp01(steering / 0.6f) : 0f;
                Vector3 fwd = Vector3.ProjectOnPlane(Quaternion.AngleAxis(yaw, transform.up) * transform.forward, hit.normal).normalized;
                Vector3 side = Vector3.Cross(hit.normal, fwd);
                float lat = Vector3.Dot(pv, side);
                float grip = (w.front ? frontFriction : rearFriction);
                rb.AddForceAtPosition(-side * lat * mass / wheels.Length / dt * Mathf.Clamp(grip * 0.12f, 0f, 0.9f), hit.point);

                float vFwd = Vector3.Dot(rb.velocity, fwd);
                float vMax = maxSpeedKmh / 3.6f;
                if (w.driven && Mathf.Abs(throttle) > 0.01f && (vFwd * Mathf.Sign(throttle) < vMax))
                {
                    float drive = throttle * engineForce * (0.5f + acceleration) * mass / 50f / wheels.Length;
                    rb.AddForceAtPosition(fwd * drive, hit.point);
                }
                // rolling resistance (strong when held at the start line)
                rb.AddForceAtPosition(-fwd * vFwd * mass * (inputLocked ? 2f : 0.05f) / wheels.Length, hit.point);
            }

            Vector3 target = GroundedWheels > 0 ? -(normalSum / GroundedWheels).normalized : Vector3.down;
            target = Vector3.Slerp(Vector3.down, target, trackStick);
            gravityDir = Vector3.Slerp(gravityDir, target, GroundedWheels > 0 ? 0.5f : 0.03f).normalized;
            rb.AddForce(gravityDir * (GroundedWheels > 0 ? gravity : airGravity), ForceMode.Acceleration);
            if (GroundedWheels == 0)            // keep the nose level-ish in the air
                rb.AddTorque(Vector3.Cross(transform.up, -gravityDir) * mass * 2f);

            if (transform.position.y < spawnPos.y - 150f) { if (respawnHandler != null) respawnHandler(); else Respawn(); }
        }
    }
}
