using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SkyTrack.EditorTools
{
    /// <summary>
    /// Reader for Sky Track .msh files (format reverse engineered from game.exe 0x406600):
    ///   u32 instances[32], u32 vertices[32], u32 triangles[32]
    ///   per used slot: aabb[inst][6], center[inst][3], radius[inst]
    ///   per used slot: inst * verts * {pos3, normal3, uv2}, then tris * u16[3]
    ///   u32 points[32], then vec3 helper points per group
    /// Direct3D and Unity are both left-handed / Y-up, so positions are used as-is; V is flipped and the
    /// triangle winding is reversed (the game's front faces are counter-clockwise).
    /// </summary>
    public class MshFile
    {
        public class Sub
        {
            public int slot, instance;
            public Vector3[] positions, normals;
            public Vector2[] uvs;
            public int[] indices;
        }

        public readonly SortedDictionary<int, List<Sub>> slots = new SortedDictionary<int, List<Sub>>();
        public readonly SortedDictionary<int, List<Vector3>> points = new SortedDictionary<int, List<Vector3>>();

        public MshFile(byte[] data)
        {
            using var r = new BinaryReader(new MemoryStream(data));
            var inst = new int[32]; var nv = new int[32]; var nt = new int[32];
            for (int i = 0; i < 32; i++) inst[i] = r.ReadInt32();
            for (int i = 0; i < 32; i++) nv[i] = r.ReadInt32();
            for (int i = 0; i < 32; i++) nt[i] = r.ReadInt32();
            for (int s = 0; s < 32; s++) r.BaseStream.Seek(inst[s] * (24 + 12 + 4), SeekOrigin.Current);  // bounds
            for (int s = 0; s < 32; s++)
            {
                if (inst[s] == 0) continue;
                var list = new List<Sub>();
                for (int k = 0; k < inst[s]; k++)
                {
                    var sub = new Sub { slot = s, instance = k, positions = new Vector3[nv[s]], normals = new Vector3[nv[s]], uvs = new Vector2[nv[s]] };
                    for (int v = 0; v < nv[s]; v++)
                    {
                        sub.positions[v] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        sub.normals[v] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        float u = r.ReadSingle(), vv = r.ReadSingle();
                        sub.uvs[v] = new Vector2(u, 1f - vv);
                    }
                    list.Add(sub);
                }
                // The game culls clockwise (front faces are counter-clockwise, verified against the stored
                // normals for every slot); Unity's front faces are clockwise, so swap two indices per triangle.
                var idx = new int[nt[s] * 3];
                for (int i = 0; i < idx.Length; i += 3)
                {
                    idx[i] = r.ReadUInt16();
                    idx[i + 2] = r.ReadUInt16();
                    idx[i + 1] = r.ReadUInt16();
                }
                foreach (var sub in list) sub.indices = idx;
                slots[s] = list;
            }
            var np = new int[32];
            for (int i = 0; i < 32; i++) np[i] = r.ReadInt32();
            for (int g = 0; g < 32; g++)
            {
                if (np[g] == 0) continue;
                var pl = new List<Vector3>();
                for (int k = 0; k < np[g]; k++) pl.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
                points[g] = pl;
            }
            if (r.BaseStream.Position != r.BaseStream.Length)
                throw new InvalidDataException($"msh parse mismatch: {r.BaseStream.Length - r.BaseStream.Position} bytes left");
        }

        public static Mesh ToMesh(Sub s, string name)
        {
            var m = new Mesh { name = name };
            if (s.positions.Length > 65535) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = s.positions;
            var n = new Vector3[s.normals.Length];
            for (int i = 0; i < n.Length; i++) n[i] = s.normals[i].sqrMagnitude > 1e-8f ? s.normals[i].normalized : Vector3.up;
            m.normals = n;
            m.uv = s.uvs;
            m.triangles = s.indices;
            m.RecalculateBounds();
            if (s.uvs.Length > 0) m.RecalculateTangents();
            return m;
        }
    }
}
