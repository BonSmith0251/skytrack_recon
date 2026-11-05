using UnityEngine;

namespace SkyTrack
{
    /// <summary>
    /// Third-person camera that follows the car's own up vector (works through loops). When the car leaves the
    /// ground the camera swings out for a dramatic jump shot: it widens the field of view, pulls back and up, lags
    /// behind the car, levels toward world up and tilts down to the landing; touchdown gives a shake scaled by how
    /// hard the car lands.
    /// </summary>
    public class ChaseCamera : MonoBehaviour
    {
        public Transform target;
        public Vector3 offset = new Vector3(0, 2.6f, -7.5f);
        public float lookAhead = 4f;
        public float smooth = 6f;

        [Header("Airborne")]
        public float airDelay = 0.12f;                     // ignore tiny hops over bumps
        public Vector3 airOffset = new Vector3(0, 4.5f, -11f);
        public float airFovBoost = 18f;                    // degrees added at full air
        public float airSmooth = 2.5f;                     // looser follow = the car pulls away / drops
        public float airLevel = 0.6f;                      // 0 = car up, 1 = world up
        public float airRoll = 8f;                         // degrees of dutch tilt from the car's sideways motion
        public float blendIn = 2.5f, blendOut = 4f;        // per second

        [Header("Landing")]
        public float shakePerMs = 0.05f;                   // shake amount per m/s of landing speed
        public float maxShake = 0.9f;
        public float shakeDecay = 5f;
        public float landingFovPunch = -6f;                // quick zoom-in on a hard landing

        Camera cam;
        SkyTrackCar car;
        Rigidbody rb;
        float baseFov, air, airTime, shake, fovPunch, lastVy;
        bool wasAir;

        void Awake()
        {
            cam = GetComponent<Camera>();
            if (cam) baseFov = cam.fieldOfView;
        }

        void Bind()
        {
            if (target && (!car || car.transform != target))
            {
                car = target.GetComponent<SkyTrackCar>();
                rb = target.GetComponent<Rigidbody>();
            }
        }

        void LateUpdate()
        {
            if (!target) return;
            Bind();
            float dt = Time.deltaTime;

            // --- airborne state
            bool inAir = car && car.GroundedWheels == 0;
            airTime = inAir ? airTime + dt : 0f;
            bool flying = airTime > airDelay;
            air = Mathf.MoveTowards(air, flying ? 1f : 0f, (flying ? blendIn : blendOut) * dt);
            float a = air * air * (3f - 2f * air);             // smoothstep

            Vector3 vel = rb ? rb.velocity : Vector3.zero;
            if (wasAir && !inAir)                               // touchdown
            {
                float impact = Mathf.Max(0f, -lastVy);
                shake = Mathf.Min(maxShake, shake + impact * shakePerMs);
                fovPunch = landingFovPunch * Mathf.Clamp01(impact / 25f);
            }
            wasAir = inAir;
            lastVy = vel.y;

            // --- position
            Vector3 off = Vector3.Lerp(offset, airOffset, a);
            Vector3 want = target.TransformPoint(off);
            Vector3 up = Vector3.Slerp(target.up, Vector3.up, a * airLevel).normalized;
            if (a > 0f)                                         // in the air keep the height offset against world up
                want = Vector3.Lerp(want, target.position + target.forward * off.z + up * off.y, a * airLevel);
            float t = 1f - Mathf.Exp(-Mathf.Lerp(smooth, airSmooth, a) * dt);
            transform.position = Vector3.Lerp(transform.position, want, t);

            // --- aim: look ahead along the flight path, tilting toward where the car will land
            Vector3 aheadDir = vel.sqrMagnitude > 4f ? Vector3.Lerp(target.forward, vel.normalized, a).normalized : target.forward;
            Vector3 aim = target.position + aheadDir * Mathf.Lerp(lookAhead, lookAhead * 2.5f, a) + Vector3.down * (2f * a);
            float side = rb ? Vector3.Dot(vel, target.right) : 0f;
            Quaternion look = Quaternion.LookRotation(aim - transform.position, up)
                            * Quaternion.Euler(0, 0, -Mathf.Clamp(side * 0.4f, -airRoll, airRoll) * a);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, 1f - Mathf.Exp(-smooth * dt));

            // --- landing shake
            if (shake > 0.001f)
            {
                float time = Time.time * 32f;
                Vector3 n = new Vector3(Mathf.PerlinNoise(time, 0.1f) - 0.5f, Mathf.PerlinNoise(0.7f, time) - 0.5f, 0f) * 2f;
                transform.position += transform.rotation * n * shake * 0.6f;
                transform.rotation *= Quaternion.Euler(n.y * shake * 3f, n.x * shake * 2f, n.x * shake * 4f);
                shake = Mathf.MoveTowards(shake, 0f, shake * shakeDecay * dt + 0.01f * dt);
            }

            // --- field of view: speed sense in the air, punch on landing
            if (cam)
            {
                fovPunch = Mathf.Lerp(fovPunch, 0f, 1f - Mathf.Exp(-6f * dt));
                cam.fieldOfView = baseFov + airFovBoost * a + fovPunch;
            }
        }

        public void Snap()
        {
            if (!target) return;
            air = airTime = shake = fovPunch = 0f;
            wasAir = false;
            if (cam) cam.fieldOfView = baseFov;
            transform.position = target.TransformPoint(offset);
            transform.rotation = Quaternion.LookRotation(target.TransformPoint(Vector3.forward * lookAhead) - transform.position, target.up);
        }
    }
}
