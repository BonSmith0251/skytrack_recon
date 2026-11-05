using System;
using System.Collections.Generic;
using UnityEngine;

namespace SkyTrack
{
    [Serializable]
    public class SceneryItem
    {
        public string kind;        // Start, Finish, Gate1, Gate2, InfoL, InfoR, TowerMain, TowerUp, Boxes, Signs
        public int variant;        // tower model 1..3 / 1..2, box texture 1..6
        public int piece = -1;     // owning platform (decors)
        public EngineMatrix matrix;
    }

    /// <summary>
    /// Scenery the game adds after BuildTrack's platform loop (0x431220 decks/decors, 0x430050 info screens,
    /// 0x42fc20 towers, then the start/finish pads and LFrame gates). The game re-seeds rand() with the clock
    /// before this pass, so the scenery differs between races even on the same course; here it is
    /// reproducible from <c>scenerySeed</c> but follows the same rules and probabilities.
    /// </summary>
    public static class TrackScenery
    {
        static readonly HashSet<int> NoInfoScreen = new HashSet<int> { 5, 6, 7, 12, 13, 14, 15, 16 };

        /// <summary>Surface probe: vertical segment against platform <c>piece</c> (0x42e1c0) -> hit point and normal.</summary>
        public delegate bool Probe(int piece, Vector3 from, Vector3 to, out Vector3 point, out Vector3 normal);

        /// <summary>Box / sign template sizes the decor pass needs (mesh bounding boxes).</summary>
        [Serializable] public struct DecorTemplates { public Vector3 boxSize; public float boxMinY, signMinY; }

        /// <summary>
        /// First part of 0x431220: deck sections (slots 8/9) and canopy (slot 10) for every platform. Deck
        /// variants 1..3 are lane obstacles; their lane is remembered for the decor pass (table 0x969ec4).
        /// Call before spawning the platforms, then <see cref="Generate"/> with the same random stream.
        /// </summary>
        public static Dictionary<int, int> ChooseVariants(IList<TrackShape> shapes, TrackLayout t, int style, MsvcRandom rng, bool decorations = true)
        {
            var lanes = new Dictionary<int, int>();
            var pieces = t.pieces;
            int n = pieces.Count;
            foreach (var p in new[] { pieces[0], pieces[n - 1] })
            {
                var s = shapes[p.type];
                p.variantA = s.variantsA > 0 ? 0 : -1; p.variantB = s.variantsB > 0 ? 0 : -1; p.extra = -1;
            }
            for (int k = 1; k < n - 1; k++)
            {
                var p = pieces[k]; var s = shapes[p.type];
                int r = rng.Next() % 101;
                if (r > (4 - style) * 25 && decorations)
                {
                    if (s.variantsA > 0) p.variantA = rng.Next() % s.variantsA;
                    if (s.variantsB > 0) p.variantB = rng.Next() % s.variantsB;
                }
                else { p.variantA = s.variantsA > 0 ? 0 : -1; p.variantB = s.variantsB > 0 ? 0 : -1; }
                if (s.extras > 0) p.extra = rng.Next() % 2 - 1;
                // unsigned compares in the original: -1 (slot absent) counts as "set" and stores lane -2
                if ((uint)p.variantA > 0) lanes[Nearest(t, p.matrix.Transform(s.centre[1]))] = p.variantA - 1;
                if ((uint)p.variantB > 0) lanes[Nearest(t, p.matrix.Transform(s.centre[5]))] = p.variantB - 1;
            }
            return lanes;
        }

        /// <summary>Rest of the scenery pass, after the platforms exist (the decor pass probes their surface).</summary>
        public static List<SceneryItem> Generate(IList<TrackShape> shapes, TrackLayout t, int style, MsvcRandom rng,
                                                 Dictionary<int, int> lanes, DecorTemplates templates, Probe probe, bool decorations = true)
        {
            var items = new List<SceneryItem>();
            var pieces = t.pieces;
            int n = pieces.Count;

            // --- boxes and signs on the deck
            if (decorations && probe != null) Decors(shapes, t, style, rng, lanes, templates, probe, items);

            // --- info screens beside straights / gentle pieces without a canopy
            for (int k = 1; k < n - 1; k++)
            {
                var p = pieces[k];
                if (p.extra != -1 || p.type >= TrackGenerator.LoopType || NoInfoScreen.Contains(p.type)) continue;
                var c = shapes[p.type].centre;
                Vector3 a = p.matrix.Transform(c[3]), b = p.matrix.Transform(c[5]);
                float y = (float)(((double)b.y + a.y) * 0.5 - 2.0);
                a.y = y; b.y = y;
                var mid = new Vector3((float)((float)((double)b.x + a.x) * 0.5), y, (float)((float)((double)a.z + b.z) * 0.5));
                var m = EngineMath.LookAt(a, b, Vector3.up);
                for (int side = 0; side < 2; side++)
                {
                    if (rng.Next() % 101 > 50) continue;
                    Vector3 v = m.right * 34f;
                    var pos = mid - v + v * (side * 2f);
                    if (!Clear(t, pos + new Vector3(0, 20, 0), 30f, false, k)) continue;
                    items.Add(new SceneryItem { kind = side == 0 ? "InfoL" : "InfoR", piece = k, matrix = EngineMath.LookAt(mid, pos, Vector3.up) });
                }
            }

            // --- tower stacks on a 16 x 16 grid of 200-unit cells around the course, below it
            if (decorations)
            {
                Bounds(t, out var centre, out float minY);
                var grid = new bool[256];
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    int gx = rng.Next() % 16, gz = rng.Next() % 16;
                    float baseY = (float)((minY - 120.0) - rng.Next() % 81);
                    var pos = new Vector3((float)(gx * 200.0 + (centre.x - 1600.0)), baseY, (float)(gz * 200.0 + (centre.z - 1600.0)));
                    if (grid[gx * 16 + gz] || !Clear(t, pos, 100f, true, -1)) continue;
                    grid[gx * 16 + gz] = true;
                    int segments = rng.Next() % 2 + 3;
                    for (int b = 0; b < segments; b++)
                    {
                        bool top = b == segments - 1;
                        int model = top ? rng.Next() % 2 + 1 : rng.Next() % 3 + 1;
                        int rx, rz;
                        do { rx = rng.Next() % 2001 - 1000; rz = rng.Next() % 2001 - 1000; } while (rx == 0 && rz == 0);
                        items.Add(new SceneryItem { kind = top ? "TowerUp" : "TowerMain", variant = model, matrix = EngineMath.LookAt(pos, pos + new Vector3(rx, 0, rz), Vector3.up) });
                        pos.y += 200f;
                    }
                }
            }

            // --- start / finish pads and the two LFrame gates (exact, independent of the scenery seed)
            TrackGenerator.StartFinish(shapes, t, out var start, out var finish, out var gate1, out var gate2);
            items.Add(new SceneryItem { kind = "Start", matrix = start });
            items.Add(new SceneryItem { kind = "Finish", matrix = finish });
            items.Add(new SceneryItem { kind = "Gate1", variant = 1, matrix = gate1 });
            items.Add(new SceneryItem { kind = "Gate2", variant = 2, matrix = gate2 });
            return items;
        }

        /// <summary>0x42fb10: no centre-line point (except those of piece <paramref name="except"/>) within radius.</summary>
        public static bool Clear(TrackLayout t, Vector3 pos, float radius, bool ignoreY, int except)
        {
            for (int k = 0; k < t.centreLine.Count; k++)
            {
                if (t.centreOwner[k] == except) continue;
                var q = t.centreLine[k];
                var d = new Vector3(q.x - pos.x, ignoreY ? 0f : q.y - pos.y, q.z - pos.z);
                if (radius > d.magnitude) return false;
            }
            return true;
        }

        /// <summary>0x432250: centre of the centre-line bounds (which always include the origin) and the lowest point.</summary>
        public static void Bounds(TrackLayout t, out Vector3 centre, out float minY)
        {
            Vector3 lo = Vector3.zero, hi = Vector3.zero;
            foreach (var q in t.centreLine) { lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
            centre = (lo + hi) * 0.5f;
            minY = lo.y;
        }

        static int Nearest(TrackLayout t, Vector3 p)
        {
            int best = -1; float bd = 100000f;
            for (int i = 0; i < t.centreLine.Count; i++) { float d = EngineMath.Dist(p, t.centreLine[i]); if (bd > d) { bd = d; best = i; } }
            return best;
        }

        // box pyramids at 0x5a03e8, in box-size units: 3 boxes (2 + 1) or 5 boxes (2 + 2 + 1)
        static readonly Vector3[][] BoxLayouts =
        {
            new[] { new Vector3(-0.65f, 0, 0), new Vector3(0.65f, 0, 0), new Vector3(0, 1, 0) },
            new[] { new Vector3(-0.65f, 0, 0), new Vector3(0.65f, 0, 0), new Vector3(-0.65f, 1, 0), new Vector3(0.65f, 1, 0), new Vector3(0, 2, 0) },
        };

        /// <summary>
        /// Second part of 0x431220: at every decor spot (helper group 9) of a platform with random decors, a box
        /// pyramid in one of three lanes (-9 / 0 / +9 across the centre line) dropped onto the deck, and a sign
        /// four box depths behind it facing back up the track. If the lane already holds a deck obstacle or
        /// earlier decor there are no boxes and the sign stands just ahead of the lane point instead.
        /// </summary>
        static void Decors(IList<TrackShape> shapes, TrackLayout t, int style, MsvcRandom rng, Dictionary<int, int> lanes,
                           DecorTemplates tpl, Probe probe, List<SceneryItem> items)
        {
            var drop = new Vector3(0, 10, 0);
            for (int k = 1; k < t.pieces.Count - 1; k++)
            {
                var p = t.pieces[k]; var s = shapes[p.type];
                if (!s.randomDecors) continue;
                int r = rng.Next() % 101;
                if (r <= (3 - style) * 25 && (uint)p.variantA == 0 && (uint)p.variantB == 0) continue;
                int boxTexture = rng.Next() % 6 + 1;
                foreach (var spot in s.decorSpots)
                {
                    var w = p.matrix.Transform(spot);
                    int pathIdx = Nearest(t, w);
                    int c = -1; float bd = 100000f;
                    for (int j = 0; j < s.centre.Length; j++) { float d = EngineMath.Dist(w, p.matrix.Transform(s.centre[j])); if (bd > d) { bd = d; c = j; } }
                    if (pathIdx < 0 || c < 0 || c + 1 >= s.centre.Length) continue;
                    int layout = rng.Next() % 2;
                    int lane = rng.Next() % 3;
                    bool taken = lanes.TryGetValue(pathIdx, out int stored) && stored != -1;
                    if (taken) lane = stored;
                    Vector3 a = p.matrix.Transform(s.centre[c]), b = p.matrix.Transform(s.centre[c + 1]);
                    var m = EngineMath.LookAt(a, b, Vector3.up);
                    Vector3 q = a + m.right * (9f * (lane - 1));
                    if (!taken)
                        foreach (var o in BoxLayouts[layout])
                        {
                            var at = q + m.right * (o.x * tpl.boxSize.x) + m.forward * (o.z * tpl.boxSize.z);
                            if (!probe(k, at + drop, at - drop, out var hit, out var nrm)) continue;
                            var pos = new Vector3(hit.x, hit.y + 0.05f - tpl.boxMinY + o.y * (tpl.boxSize.y + 0.05f), hit.z);
                            items.Add(new SceneryItem { kind = "Boxes", variant = boxTexture, piece = k, matrix = EngineMath.LookAt(pos, pos + m.forward * 10f, nrm) });
                        }
                    var s0 = taken ? q + m.forward * tpl.boxSize.z : q - m.forward * (4f * tpl.boxSize.z);
                    if (probe(k, s0 + drop, s0 - drop, out var sh, out var sn))
                    {
                        var pos = new Vector3(sh.x, sh.y + 0.05f - tpl.signMinY, sh.z);
                        var dir = Vector3.Cross(Vector3.Cross(q - sh, sn), sn);
                        if (dir.sqrMagnitude > 1e-6f)
                            items.Add(new SceneryItem { kind = "Signs", piece = k, matrix = EngineMath.LookAt(pos, pos + dir, sn) });
                    }
                    lanes[pathIdx] = lane;
                }
            }
        }
    }
}
