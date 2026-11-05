using UnityEngine;

namespace SkyTrack
{
    /// <summary>Keeps the sky dome centred on the camera (the game draws it at infinity).</summary>
    public class SkyFollow : MonoBehaviour
    {
        public Transform target;

        void LateUpdate()
        {
            var t = target ? target : (Camera.main ? Camera.main.transform : null);
            if (t) transform.position = t.position;
        }
    }
}
