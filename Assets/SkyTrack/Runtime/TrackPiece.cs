using System.Collections.Generic;
using UnityEngine;

namespace SkyTrack
{
    /// <summary>
    /// A track platform. Pieces start at their origin heading +Z; <see cref="exit"/> is where the next
    /// piece attaches (from helper point group 8: position, +right, -up). <see cref="path"/> is the
    /// centre line (group 7) and <see cref="decorSpots"/> the decor spawn points (group 9).
    /// </summary>
    public class TrackPiece : MonoBehaviour
    {
        public Transform exit;
        public List<Vector3> path = new List<Vector3>();
        public List<Vector3> decorSpots = new List<Vector3>();

        public Vector3 WorldPathPoint(int i) => transform.TransformPoint(path[i]);

        void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            for (int i = 1; i < path.Count; i++)
                Gizmos.DrawLine(WorldPathPoint(i - 1), WorldPathPoint(i));
            if (exit)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawRay(exit.position, exit.forward * 10f);
                Gizmos.color = Color.yellow;
                Gizmos.DrawRay(exit.position, exit.up * 5f);
            }
        }
    }
}
