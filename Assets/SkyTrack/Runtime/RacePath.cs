using System.Collections.Generic;
using UnityEngine;

namespace SkyTrack
{
    /// <summary>
    /// The race line: the course centre line (helper group 7 of every placed platform, ending at the finish
    /// exit) resampled every few units, with a frame (forward / up / right) per sample. The up vector is
    /// carried along by parallel transport inside a platform and reset to the platform's own frame at every
    /// joint, so it follows the deck through loops and twisted turns.
    /// </summary>
    public class RacePath
    {
        public readonly List<Vector3> points = new List<Vector3>();
        public readonly List<Vector3> forward = new List<Vector3>();
        public readonly List<Vector3> up = new List<Vector3>();
        public readonly List<Vector3> right = new List<Vector3>();
        public readonly List<float> distance = new List<float>();
        public float Length => distance.Count > 0 ? distance[distance.Count - 1] : 0f;
        public int Count => points.Count;

        public RacePath(IList<TrackShape> shapes, TrackLayout t, float spacing = 4f)
        {
            // coarse points with the platform frame at each platform's first point
            var coarse = new List<Vector3>();
            var coarseUp = new List<Vector3>();
            var joint = new List<bool>();
            foreach (var p in t.pieces)
            {
                var c = shapes[p.type].centre;
                for (int j = 0; j < c.Length - 1; j++)
                {
                    coarse.Add(p.matrix.Transform(c[j]));
                    coarseUp.Add(p.matrix.up);
                    joint.Add(j == 0);
                }
            }
            var last = t.pieces[t.pieces.Count - 1];
            var lc = shapes[last.type].centre;
            coarse.Add(last.matrix.Transform(lc[lc.Length - 1]));
            coarseUp.Add(last.matrix.up);
            joint.Add(true);

            // resample (Catmull-Rom) and transport the up vector
            Vector3 curUp = coarseUp[0];
            for (int i = 0; i < coarse.Count - 1; i++)
            {
                Vector3 p0 = coarse[Mathf.Max(i - 1, 0)], p1 = coarse[i], p2 = coarse[i + 1], p3 = coarse[Mathf.Min(i + 2, coarse.Count - 1)];
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(p1, p2) / spacing));
                if (joint[i]) curUp = coarseUp[i];
                for (int s = 0; s < steps; s++)
                {
                    float u = s / (float)steps;
                    Add(CatmullRom(p0, p1, p2, p3, u), ref curUp);
                }
            }
            Add(coarse[coarse.Count - 1], ref curUp);
            if (points.Count > 1) { forward[0] = (points[1] - points[0]).normalized; FrameAt(0); }
        }

        void Add(Vector3 p, ref Vector3 curUp)
        {
            int n = points.Count;
            Vector3 f = n > 0 ? (p - points[n - 1]).normalized : Vector3.forward;
            if (n > 0)
            {
                // parallel transport: rotate up by the rotation that takes the previous forward to this one
                curUp = Quaternion.FromToRotation(forward[n - 1], f) * curUp;
                distance.Add(distance[n - 1] + Vector3.Distance(points[n - 1], p));
            }
            else distance.Add(0f);
            curUp = Vector3.ProjectOnPlane(curUp, f).normalized;
            points.Add(p); forward.Add(f); up.Add(curUp); right.Add(Vector3.Cross(curUp, f).normalized);
        }

        void FrameAt(int i)
        {
            up[i] = Vector3.ProjectOnPlane(up[i], forward[i]).normalized;
            right[i] = Vector3.Cross(up[i], forward[i]).normalized;
        }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        /// <summary>Sample index at a distance along the line.</summary>
        public int IndexAt(float d)
        {
            if (d <= 0) return 0;
            if (d >= Length) return Count - 1;
            int lo = 0, hi = Count - 1;
            while (hi - lo > 1) { int mid = (lo + hi) / 2; if (distance[mid] <= d) lo = mid; else hi = mid; }
            return lo;
        }

        public Vector3 PointAt(float d, float lateral = 0f)
        {
            int i = IndexAt(d);
            if (i >= Count - 1) return points[Count - 1] + right[Count - 1] * lateral;
            float seg = distance[i + 1] - distance[i];
            float u = seg > 1e-4f ? (d - distance[i]) / seg : 0f;
            return Vector3.Lerp(points[i], points[i + 1], u) + Vector3.Lerp(right[i], right[i + 1], u).normalized * lateral;
        }

        /// <summary>Nearest sample to <paramref name="p"/>, searched around <paramref name="hint"/> (the track can pass close to itself).</summary>
        public int Nearest(Vector3 p, int hint, int back = 8, int ahead = 40)
        {
            int best = Mathf.Clamp(hint, 0, Count - 1);
            float bd = float.MaxValue;
            for (int i = Mathf.Max(0, hint - back); i <= Mathf.Min(Count - 1, hint + ahead); i++)
            {
                float d = (points[i] - p).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>Distance along the line of the projection of <paramref name="p"/> near sample i.</summary>
        public float Project(Vector3 p, int i)
        {
            i = Mathf.Clamp(i, 0, Count - 1);
            return Mathf.Clamp(distance[i] + Vector3.Dot(p - points[i], forward[i]), 0f, Length);
        }

        /// <summary>Horizontal (in-deck) turning over [d, d + span]: radians, sign = direction.</summary>
        public float TurnAngle(float d, float span)
        {
            // accumulated sample by sample about the local up, so loops (pitch) don't count as turns
            int a = IndexAt(d), b = IndexAt(d + span);
            float sum = 0f;
            for (int k = a; k < b; k++)
            {
                Vector3 n = up[k];
                Vector3 f0 = Vector3.ProjectOnPlane(forward[k], n), f1 = Vector3.ProjectOnPlane(forward[k + 1], n);
                if (f0.sqrMagnitude < 1e-6f || f1.sqrMagnitude < 1e-6f) continue;
                sum += Vector3.SignedAngle(f0, f1, n);
            }
            return sum * Mathf.Deg2Rad;
        }

        public Pose PoseAt(float d, float lateral, float height)
        {
            int i = IndexAt(d);
            return new Pose(PointAt(d, lateral) + up[i] * height, Quaternion.LookRotation(forward[i], up[i]));
        }
    }
}
