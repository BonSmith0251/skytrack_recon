using System;
using System.Collections.Generic;
using UnityEngine;

namespace SkyTrack
{
    /// <summary>MSVC CRT rand(): holdrand = holdrand * 214013 + 2531011, returns bits 16..30.</summary>
    public class MsvcRandom
    {
        public uint state;
        public int calls;
        public MsvcRandom(int seed) { state = unchecked((uint)seed); }
        public int Next()
        {
            state = unchecked(state * 214013u + 2531011u);
            calls++;
            return (int)((state >> 16) & 0x7fff);
        }
    }

    /// <summary>Geometry the generator needs from one platform mesh (helper point groups 7, 8, 9).</summary>
    [Serializable]
    public class TrackShape
    {
        public string name;
        public GameObject prefab;
        public Vector3[] centre = new Vector3[0];      // group 7: centre line, first point at the origin
        public Vector3[] exit = new Vector3[0];        // group 8: exit position, +right, -up
        public Vector3[] decorSpots = new Vector3[0];  // group 9
        public int variantsA, variantsB, extras;       // instance counts of mesh slots 8, 9, 10
        public bool randomDecors;                      // 'Platform Decors Type: DECORS_TYPE_RANDOM'
    }

    /// <summary>Direct3D row-vector world matrix as the engine stores it (rows right, up, forward, position).</summary>
    [Serializable]
    public struct EngineMatrix
    {
        public Vector3 right, up, forward, position;
        public static readonly EngineMatrix Identity = new EngineMatrix
        { right = Vector3.right, up = Vector3.up, forward = Vector3.forward, position = Vector3.zero };
        public Quaternion Rotation => Quaternion.LookRotation(forward, up);

        /// <summary>D3DXVec3TransformCoord for an affine matrix, in float32 like the original.</summary>
        public Vector3 Transform(Vector3 v) => new Vector3(Row(v, right.x, up.x, forward.x, position.x),
                                                           Row(v, right.y, up.y, forward.y, position.y),
                                                           Row(v, right.z, up.z, forward.z, position.z));
        static float Row(Vector3 v, float a, float b, float c, float d)
            => EngineMath.Add(EngineMath.Add(EngineMath.Add(EngineMath.Mul(v.x, a), EngineMath.Mul(v.y, b)), EngineMath.Mul(v.z, c)), d);
    }

    /// <summary>The engine's vector helpers with its rounding (double intermediates, float results).</summary>
    public static class EngineMath
    {
        public static float Mul(float a, float b) => (float)((double)a * b);
        public static float Add(float a, float b) => (float)((double)a + b);
        public static float Sub(float a, float b) => (float)((double)a - b);
        public static Vector3 Sub(Vector3 a, Vector3 b) => new Vector3(Sub(a.x, b.x), Sub(a.y, b.y), Sub(a.z, b.z));

        public static Vector3 Normalize(Vector3 v)
        {
            float l2 = (float)((double)v.x * v.x + (double)v.y * v.y + (double)v.z * v.z);
            float inv = l2 == 0f ? 0f : (float)(1.0 / (float)Math.Sqrt(l2));
            return new Vector3(Mul(inv, v.x), Mul(inv, v.y), Mul(inv, v.z));
        }

        /// <summary>0x434010: forward = target - pos, right = up x forward, up = forward x right.</summary>
        public static EngineMatrix LookAt(Vector3 pos, Vector3 target, Vector3 up)
        {
            Vector3 r1 = Normalize(up);
            Vector3 r2 = Normalize(Sub(target, pos));
            var r0 = new Vector3((float)((double)r1.y * r2.z - (double)r2.y * r1.z),
                                 (float)((double)r2.x * r1.z - (double)r1.x * r2.z),
                                 (float)((double)r1.x * r2.y - (double)r2.x * r1.y));
            r1 = new Vector3((float)((double)r0.z * r2.y - (double)r0.y * r2.z),
                             (float)((double)r0.x * r2.z - (double)r2.x * r0.z),
                             (float)((double)r2.x * r0.y - (double)r0.x * r2.y));
            return new EngineMatrix { right = Normalize(r0), up = Normalize(r1), forward = r2, position = pos };
        }

        /// <summary>0x433f80.</summary>
        public static float Dist(Vector3 a, Vector3 b)
        {
            float dx = Sub(a.x, b.x), dy = Sub(a.y, b.y), dz = Sub(a.z, b.z);
            float s = Add(Add(Mul(dy, dy), Mul(dx, dx)), Mul(dz, dz));
            return (float)Math.Sqrt(s);
        }
    }

    [Serializable]
    public class PlacedPiece
    {
        public int index, type;
        public string name;
        public int texture;                 // 1 = Main01/Bump01, 2 = Main02/Bump02
        public EngineMatrix matrix;
        public int variantA = -1, variantB = -1, extra = -1;   // chosen instances of slots 8/9/10 (scenery pass)
    }

    [Serializable]
    public class TrackLayout
    {
        public int seed, pieceSet, length, count, randCalls, backtracks, tries;
        public List<PlacedPiece> pieces = new List<PlacedPiece>();
        public List<Vector3> centreLine = new List<Vector3>();
        public List<int> centreOwner = new List<int>();       // piece index of each centre-line point
        public float CentreLength
        {
            get { float d = 0; for (int i = 1; i < centreLine.Count; i++) d += Vector3.Distance(centreLine[i - 1], centreLine[i]); return d; }
        }
    }

    public class TrackBuildException : Exception { public TrackBuildException(string m) : base(m) { } }

    /// <summary>
    /// Port of Sky Track's BuildTrack (game.exe 0x4327f0). The game stores no track layouts: every race
    /// course is generated from (seed, piece set, length) with the MSVC rand(), so the same parameters give
    /// the same course as the original. See tools/track_gen.py for the annotated reference implementation.
    /// </summary>
    public static class TrackGenerator
    {
        /// <summary>game.exe 0x5a0340, in the generator's index order (AH_020 appears twice).</summary>
        public static readonly string[] Platforms =
        {
            "AH_000", "AD_020", "AU_020", "LH_090", "RH_090", "AH_020", "LH_180", "RH_180",
            "LD_090", "RD_090", "LU_090", "RU_090", "AH_020", "LD_180", "RD_180", "LU_180",
            "RU_180", "A1_360", "A2_360",
        };
        public static readonly int[] TypeCount = { 5, 12, 19 };
        public static readonly string[] PieceSetNames = { "Basic (5 pieces)", "Advanced (12 pieces)", "All (19 pieces, loops)" };
        public static readonly string[] LengthNames = { "Short", "Medium", "Long" };
        public const int LoopType = 17;
        const float MinDist = 25.5f, FinishDist = 80f, OriginDist = 80f, StartDist = 40f;
        static readonly Vector3 StartPoint = new Vector3(0, 0, 100);

        public static TrackLayout Build(IList<TrackShape> shapes, int seed, int pieceSet, int length)
        {
            var rng = new MsvcRandom(seed);
            int typeCount = pieceSet >= 0 && pieceSet < TypeCount.Length ? TypeCount[pieceSet] : 5;
            var t = new TrackLayout { seed = seed, pieceSet = pieceSet, length = length };
            t.count = rng.Next() % 6 + (length + 1) * 12;
            var records = new int[128];
            for (int k = 0; k < records.Length; k++) records[k] = -1;
            var placed = t.pieces;
            var path = t.centreLine;
            var owner = t.centreOwner;

            bool Fits(PlacedPiece prev, TrackShape cand, EngineMatrix m, bool final)
            {
                var c = cand.centre;
                int n = c.Length, last = path.Count;
                for (int j = 0; j < n; j++)
                {
                    float thr = final && j >= n - 3 ? FinishDist : MinDist;
                    Vector3 p = m.Transform(c[j]);
                    if (OriginDist > EngineMath.Dist(p, Vector3.zero) || StartDist > EngineMath.Dist(p, StartPoint)) return false;
                    for (int k = 0; k < path.Count; k++)
                    {
                        if (owner[k] == prev.index)
                        {
                            if (j < 2) continue;                                   // prototypes carry type -1 (>= 17 unsigned)
                            if (prev.type >= LoopType && k > last - 2) continue;
                        }
                        if (thr > EngineMath.Dist(p, path[k])) return false;
                    }
                }
                return true;
            }

            EngineMatrix ExitMatrix(PlacedPiece prev)
            {
                var ex = shapes[prev.type].exit;
                Vector3 a = prev.matrix.Transform(ex[0]), b = prev.matrix.Transform(ex[1]), cp = prev.matrix.Transform(ex[2]);
                Vector3 down = EngineMath.Sub(cp, a);
                var m1 = EngineMath.LookAt(a, b, down);
                var target = new Vector3(EngineMath.Add(EngineMath.Mul(m1.right.x, 100f), a.x),
                                         EngineMath.Add(EngineMath.Mul(m1.right.y, 100f), a.y),
                                         EngineMath.Add(EngineMath.Mul(m1.right.z, 100f), a.z));
                return EngineMath.LookAt(a, target, -down);
            }

            int Pick(int i, PlacedPiece prev, EngineMatrix m)
            {
                if (i == t.count - 1) return Fits(prev, shapes[0], m, true) ? 0 : -1;     // finish straight
                for (int tryNo = 0; tryNo < 256; tryNo++)
                {
                    int r = rng.Next() % typeCount;
                    t.tries++;
                    if (Fits(prev, shapes[r], m, false)) return r;
                }
                return -1;
            }

            int idx = 0, type = 0;
            EngineMatrix mat = EngineMatrix.Identity;
            while (idx < t.count)
            {
                if (idx > 0)
                {
                    while (true)
                    {
                        var prev = placed[placed.Count - 1];
                        mat = ExitMatrix(prev);
                        type = Pick(idx, prev, mat);
                        if (type != -1 && type != records[idx]) break;
                        t.backtracks++;                                             // drop the previous piece, re-pick there
                        for (int k = path.Count - 1; k >= 0; k--)
                            if (owner[k] == prev.index) { path.RemoveAt(k); owner.RemoveAt(k); }
                        records[idx] = -1;
                        placed.RemoveAt(placed.Count - 1);
                        idx--;
                        if (idx <= 0) throw new TrackBuildException("BuildTrack() error! Can't get next platform type!");
                    }
                }
                records[idx] = type;
                var piece = new PlacedPiece { index = idx, type = type, name = Platforms[type], matrix = mat, texture = rng.Next() % 2 + 1 };
                placed.Add(piece);
                var c = shapes[type].centre;
                for (int j = 0; j < c.Length - 1; j++) { path.Add(mat.Transform(c[j])); owner.Add(idx); }
                idx++;
            }
            t.randCalls = rng.calls;
            return t;
        }

        // ------------------------------------------------------------ fixed structures (BuildTrack epilogue)
        /// <summary>Start pad at the origin, a second one at the finish facing back, LFrame gates at both straights.</summary>
        public static void StartFinish(IList<TrackShape> shapes, TrackLayout t,
                                       out EngineMatrix start, out EngineMatrix finish, out EngineMatrix gate1, out EngineMatrix gate2)
        {
            start = EngineMath.LookAt(Vector3.zero, Vector3.forward, Vector3.up);
            var last = t.pieces[t.pieces.Count - 1];
            var ex = shapes[last.type].exit;
            Vector3 a = last.matrix.Transform(ex[0]), b = last.matrix.Transform(ex[1]);
            var m = EngineMath.LookAt(a, b, Vector3.up);
            var behind = new Vector3(EngineMath.Add(EngineMath.Mul(m.right.x, 100f), a.x),
                                     EngineMath.Add(EngineMath.Mul(m.right.y, 100f), a.y),
                                     EngineMath.Add(EngineMath.Mul(m.right.z, 100f), a.z));
            finish = EngineMath.LookAt(a, behind, Vector3.up);
            gate1 = EngineMath.LookAt(new Vector3(0, 0, 160), Vector3.zero, Vector3.up);
            var lp = last.matrix.position;
            var back = new Vector3(EngineMath.Sub(lp.x, EngineMath.Mul(last.matrix.forward.x, 100f)),
                                   EngineMath.Sub(lp.y, EngineMath.Mul(last.matrix.forward.y, 100f)),
                                   EngineMath.Sub(lp.z, EngineMath.Mul(last.matrix.forward.z, 100f)));
            gate2 = EngineMath.LookAt(lp, back, Vector3.up);
        }
    }

    /// <summary>A Career race as the game rolls it (0x436400): level 0..2 picks piece set and skill.</summary>
    [Serializable]
    public class CareerRace
    {
        public static readonly string[] Names = { "COMPETITION ", "BATTLE ", "TEST DRIVE ", "TRAINING ", "TEST FOR SPEED " };
        public static readonly string[] Levels = { "for BEGINNERS", "for NEWCOMERS", "for EXPERIENCED", "for ADVANCED", "for PROFESSIONALS", "for ACES" };
        public string title;
        public int seed, style, pieceSet, length, racers, skill;

        public static CareerRace Roll(MsvcRandom r, int level)
        {
            var c = new CareerRace { seed = r.Next(), style = r.Next() % 3, pieceSet = level };
            c.length = r.Next() % (level + 1);
            c.racers = (r.Next() % 4 + 3) * 3;
            c.skill = level;
            c.title = Names[r.Next() % 5] + Levels[r.Next() % 2 + level * 2];
            return c;
        }
    }
}
