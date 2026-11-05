using UnityEngine;

namespace SkyTrack
{
    /// <summary>Free-fly camera: hold right mouse to look, WASD/QE to move, Shift for speed, scroll to change speed.</summary>
    public class FlyCamera : MonoBehaviour
    {
        public float speed = 40f;
        float yaw, pitch;

        void Start()
        {
            var e = transform.eulerAngles;
            yaw = e.y; pitch = e.x;
        }

        void Update()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxis("Mouse X") * 3f;
                pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 3f, -89f, 89f);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            }
            speed = Mathf.Clamp(speed * (1f + Input.mouseScrollDelta.y * 0.1f), 2f, 2000f);
            Vector3 m = new Vector3(Input.GetAxis("Horizontal"), (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0), Input.GetAxis("Vertical"));
            transform.position += transform.TransformDirection(m) * speed * (Input.GetKey(KeyCode.LeftShift) ? 4f : 1f) * Time.deltaTime;
#endif
        }
    }
}
