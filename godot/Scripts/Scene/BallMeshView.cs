using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DeformGrip.App.Data;
using Godot;

namespace DeformGrip.App.Scene
{
    /// <summary>
    /// Renders a deformable surface mesh from model/<level>/surface.json.
    ///
    /// Vertex-update method (godot/README.md Task 2):
    /// We use RenderingServer.MeshSurfaceUpdateVertexRegion with Mesh.ArrayFormat.FlagUseDynamicUpdate
    /// (GL_DYNAMIC_DRAW on OpenGL 3 Compatibility) and WITHOUT FlagCompressAttributes so vertex positions
    /// remain uncompressed float32 (12 bytes/vertex). Byte offsets and strides for both positions and
    /// normals are queried dynamically from RenderingServer and verified against Godot's own initial
    /// vertex_data at build time.
    ///
    /// Why this method:
    /// - Zero per-frame heap allocations (writes directly into a preallocated byte[] matching GPU VBO layout).
    /// - Single VBO sub-data upload per rendered frame; index buffer and vertex colors stay immutable on GPU.
    /// - Avoids ArrayMesh.ClearSurfaces()+AddSurfaceFromArrays() reallocation stalls and MeshDataTool GC churn.
    /// - Automatic safe fallback to ArrayMesh surface rebuild if an unexpected driver/format layout is ever seen.
    /// </summary>
    public partial class BallMeshView : MeshInstance3D
    {
        private ArrayMesh? _mesh;
        private Rid _meshRid;
        private StandardMaterial3D? _material;

        private int _nVerts;
        private int _nTris;
        private double _radiusM = 0.035;

        // Rest data in Godot coordinates (x, z, -y)
        private Vector3[] _restPositions = Array.Empty<Vector3>();
        private Vector3[] _deformedPositions = Array.Empty<Vector3>();
        private Vector3[] _normals = Array.Empty<Vector3>();
        private int[] _ccwTrisFlat = Array.Empty<int>(); // Original CCW order for outward cross-product normals
        private int[] _cwIndices = Array.Empty<int>();   // Reversed CW order for Godot front faces
        private Color[] _vertexColors = Array.Empty<Color>();

        private enum NormalEncoding
        {
            None,
            Float32X3,        // 12 bytes: (nx, ny, nz) as 3x float32
            OctahedralU16,    // 4 bytes (or 8 bytes with 4-byte trailing tangent/pad preserved)
            OctahedralF32,    // 8 bytes: (ox, oy) as 2x float32 in [0, 1]
            Half4,            // 8 bytes: (nx, ny, nz, 0) as 4x float16
            Snorm16X4         // 8 bytes: (nx, ny, nz, 0) as 4x int16 SNORM
        }

        // Dynamically queried GPU vertex buffer layout
        private bool _useDirectVertexRegion;
        private int _vertexOffset;
        private int _vertexStride;
        private int _normalOffset;
        private int _normalStride;
        private NormalEncoding _normalEncoding = NormalEncoding.None;
        private byte[] _gpuVertexBuffer = Array.Empty<byte>();

        public string ActiveUpdateMethod { get; private set; } = "uninitialized";
        public bool BufferLayoutVerified { get; private set; }

        public void InitializeMesh(SurfaceMeshData surface, Color baseColor, Color fixedCapColor)
        {
            _nVerts = surface.NSurface;
            _nTris = surface.SurfaceTriangles.Length;
            _radiusM = surface.RadiusM;

            _restPositions = new Vector3[_nVerts];
            _deformedPositions = new Vector3[_nVerts];
            _normals = new Vector3[_nVerts];
            _vertexColors = new Color[_nVerts];

            var fixedNodeSet = new HashSet<int>(surface.FixedNodes);
            for (int i = 0; i < _nVerts; i++)
            {
                double[] p = surface.SurfacePositions[i];
                Vector3 gPos = CoordMap.ContractToGodot(p[0], p[1], p[2]);
                _restPositions[i] = gPos;
                _deformedPositions[i] = gPos;

                bool isFixed = fixedNodeSet.Contains(surface.SurfaceNodes[i]);
                _vertexColors[i] = isFixed ? fixedCapColor : baseColor;
            }

            _ccwTrisFlat = new int[_nTris * 3];
            for (int t = 0; t < _nTris; t++)
            {
                int[] tri = surface.SurfaceTriangles[t];
                _ccwTrisFlat[3 * t] = tri[0];
                _ccwTrisFlat[3 * t + 1] = tri[1];
                _ccwTrisFlat[3 * t + 2] = tri[2];
            }
            _cwIndices = CoordMap.BuildGodotCwIndices(surface.SurfaceTriangles);

            RecomputeSmoothNormals(_restPositions, _normals);

            _mesh = new ArrayMesh();
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _restPositions;
            arrays[(int)Mesh.ArrayType.Normal] = _normals;
            arrays[(int)Mesh.ArrayType.Color] = _vertexColors;
            arrays[(int)Mesh.ArrayType.Index] = _cwIndices;

            // Explicitly use FlagUseDynamicUpdate (GL_DYNAMIC_DRAW) and omit FlagCompressAttributes
            // so vertex positions are uncompressed 3x float32 (12 bytes).
            Mesh.ArrayFormat flags = Mesh.ArrayFormat.FlagUseDynamicUpdate;
            _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);

            // Set generous AABB so visual gain up to x50 is never frustum-culled
            float bound = (float)(_radiusM * 8.0);
            _mesh.CustomAabb = new Aabb(new Vector3(-bound, -bound, -bound), new Vector3(2 * bound, 2 * bound, 2 * bound));

            _material = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 0.62f,
                Metallic = 0.04f,
                CullMode = BaseMaterial3D.CullModeEnum.Back
            };
            _mesh.SurfaceSetMaterial(0, _material);

            Mesh = _mesh;
            _meshRid = _mesh.GetRid();

            InspectAndVerifyVertexBufferLayout();
        }

        private void InspectAndVerifyVertexBufferLayout()
        {
            _useDirectVertexRegion = false;
            BufferLayoutVerified = false;
            ActiveUpdateMethod = "ArrayMesh rebuild fallback";

            if (_mesh == null || !_meshRid.IsValid || _nVerts == 0)
                return;

            var format = (RenderingServer.ArrayFormat)_mesh.SurfaceGetFormat(0);
            _vertexOffset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, _nVerts, (int)RenderingServer.ArrayType.Vertex);
            _vertexStride = (int)RenderingServer.MeshSurfaceGetFormatVertexStride(format, _nVerts);
            _normalOffset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, _nVerts, (int)RenderingServer.ArrayType.Normal);
            _normalStride = (int)RenderingServer.MeshSurfaceGetFormatNormalTangentStride(format, _nVerts);

            Godot.Collections.Dictionary surfInfo = RenderingServer.MeshGetSurface(_meshRid, 0);
            if (!surfInfo.ContainsKey("vertex_data"))
                return;

            byte[] initialVertexData = surfInfo["vertex_data"].AsByteArray();
            if (initialVertexData == null || initialVertexData.Length == 0)
                return;

            bool vertexSupported = _vertexStride == 12 &&
                                   _vertexOffset >= 0 &&
                                   _vertexOffset + _nVerts * _vertexStride <= initialVertexData.Length;
            bool normalSupported = (_normalStride == 4 || _normalStride == 8 || _normalStride == 12) &&
                                   _normalOffset >= 0 &&
                                   _normalOffset + _nVerts * _normalStride <= initialVertexData.Length;

            if (!vertexSupported || !normalSupported)
            {
                GD.PushWarning(
                    $"[BallMeshView] Unexpected GPU vertex layout (vOff={_vertexOffset}, vStride={_vertexStride}, " +
                    $"nOff={_normalOffset}, nStride={_normalStride}, len={initialVertexData.Length}); using safe ArrayMesh rebuild fallback.");
                return;
            }

            _gpuVertexBuffer = (byte[])initialVertexData.Clone();

            // Verify position packing (always uncompressed 3x float32 when FlagCompressAttributes is unset)
            PackPositions(_restPositions, _gpuVertexBuffer);
            float maxPosErr = 0f;
            for (int i = 0; i < _nVerts; i++)
            {
                int vo = _vertexOffset + i * _vertexStride;
                for (int c = 0; c < 3; c++)
                {
                    float a = MemoryMarshal.Read<float>(initialVertexData.AsSpan(vo + 4 * c, 4));
                    float b = MemoryMarshal.Read<float>(_gpuVertexBuffer.AsSpan(vo + 4 * c, 4));
                    float err = MathF.Abs(a - b);
                    if (err > maxPosErr) maxPosErr = err;
                }
            }

            if (maxPosErr > 1e-6f)
            {
                GD.PushWarning($"[BallMeshView] Position layout check failed (maxPosErr={maxPosErr:E2}); using safe ArrayMesh rebuild fallback.");
                return;
            }

            // Detect and verify the exact normal encoding against Godot's own initial vertex_data bytes
            NormalEncoding[] candidates = _normalStride switch
            {
                12 => new[] { NormalEncoding.Float32X3 },
                4 => new[] { NormalEncoding.OctahedralU16 },
                8 => new[] { NormalEncoding.OctahedralU16, NormalEncoding.OctahedralF32, NormalEncoding.Half4, NormalEncoding.Snorm16X4 },
                _ => Array.Empty<NormalEncoding>()
            };

            foreach (NormalEncoding candidate in candidates)
            {
                if (VerifyNormalEncoding(candidate, initialVertexData))
                {
                    _normalEncoding = candidate;
                    _useDirectVertexRegion = true;
                    BufferLayoutVerified = true;
                    ActiveUpdateMethod =
                        $"MeshSurfaceUpdateVertexRegion (vOff={_vertexOffset}, vStr={_vertexStride}, nOff={_normalOffset}, nStr={_normalStride}, norm={_normalEncoding})";
                    return;
                }
            }

            // Log diagnostic bytes of vertex 0 if no encoding matched
            string hex0 = Convert.ToHexString(initialVertexData.AsSpan(_normalOffset, _normalStride));
            GD.PushWarning(
                $"[BallMeshView] Could not match GPU normal encoding (nStride={_normalStride}, n0={_normals[0]}, bytes0={hex0}); using safe ArrayMesh rebuild fallback.");
        }

        private bool VerifyNormalEncoding(NormalEncoding encoding, byte[] referenceVertexData)
        {
            Span<byte> scratch = stackalloc byte[12];
            for (int i = 0; i < _nVerts; i++)
            {
                int no = _normalOffset + i * _normalStride;
                ReadOnlySpan<byte> refSlice = referenceVertexData.AsSpan(no, _normalStride);
                refSlice.CopyTo(scratch);
                PackSingleNormal(_normals[i], encoding, scratch.Slice(0, _normalStride));

                switch (encoding)
                {
                    case NormalEncoding.OctahedralU16:
                    {
                        ushort gx = MemoryMarshal.Read<ushort>(refSlice.Slice(0, 2));
                        ushort gy = MemoryMarshal.Read<ushort>(refSlice.Slice(2, 2));
                        ushort ox = MemoryMarshal.Read<ushort>(scratch.Slice(0, 2));
                        ushort oy = MemoryMarshal.Read<ushort>(scratch.Slice(2, 2));
                        if (Math.Abs(gx - ox) > 2 || Math.Abs(gy - oy) > 2)
                            return false;
                        break;
                    }
                    case NormalEncoding.OctahedralF32:
                    {
                        float gx = MemoryMarshal.Read<float>(refSlice.Slice(0, 4));
                        float gy = MemoryMarshal.Read<float>(refSlice.Slice(4, 4));
                        float ox = MemoryMarshal.Read<float>(scratch.Slice(0, 4));
                        float oy = MemoryMarshal.Read<float>(scratch.Slice(4, 4));
                        if (MathF.Abs(gx - ox) > 1e-4f || MathF.Abs(gy - oy) > 1e-4f)
                            return false;
                        break;
                    }
                    case NormalEncoding.Half4:
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            float g = (float)MemoryMarshal.Read<Half>(refSlice.Slice(2 * c, 2));
                            float o = (float)MemoryMarshal.Read<Half>(scratch.Slice(2 * c, 2));
                            if (MathF.Abs(g - o) > 2e-3f)
                                return false;
                        }
                        break;
                    }
                    case NormalEncoding.Snorm16X4:
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            short g = MemoryMarshal.Read<short>(refSlice.Slice(2 * c, 2));
                            short o = MemoryMarshal.Read<short>(scratch.Slice(2 * c, 2));
                            if (Math.Abs(g - o) > 2)
                                return false;
                        }
                        break;
                    }
                    case NormalEncoding.Float32X3:
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            float g = MemoryMarshal.Read<float>(refSlice.Slice(4 * c, 4));
                            float o = MemoryMarshal.Read<float>(scratch.Slice(4 * c, 4));
                            if (MathF.Abs(g - o) > 1e-5f)
                                return false;
                        }
                        break;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Writes vertex positions = rest + visualGain * u (contract xyz -> Godot (x, z, -y))
        /// and recomputes smooth outward normals once per rendered frame.
        /// </summary>
        public void ApplyDisplacements(float[] contractDisplacements, float visualGain)
        {
            if (_mesh == null || _nVerts == 0)
                return;

            for (int i = 0; i < _nVerts; i++)
            {
                int o = 3 * i;
                float ux = contractDisplacements[o] * visualGain;
                float uy = contractDisplacements[o + 1] * visualGain;
                float uz = contractDisplacements[o + 2] * visualGain;

                Vector3 rest = _restPositions[i];
                // CONTRACT §3: Godot = (x, z, -y)
                _deformedPositions[i] = new Vector3(rest.X + ux, rest.Y + uz, rest.Z - uy);
            }

            RecomputeSmoothNormals(_deformedPositions, _normals);

            if (_useDirectVertexRegion)
            {
                PackPositionsAndNormals(_deformedPositions, _normals, _gpuVertexBuffer);
                RenderingServer.MeshSurfaceUpdateVertexRegion(_meshRid, 0, 0, _gpuVertexBuffer);
            }
            else
            {
                // Safe fallback if GPU layout wasn't matched
                _mesh.ClearSurfaces();
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = _deformedPositions;
                arrays[(int)Mesh.ArrayType.Normal] = _normals;
                arrays[(int)Mesh.ArrayType.Color] = _vertexColors;
                arrays[(int)Mesh.ArrayType.Index] = _cwIndices;
                _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, Mesh.ArrayFormat.FlagUseDynamicUpdate);
                if (_material != null)
                    _mesh.SurfaceSetMaterial(0, _material);
            }
        }

        public void ResetToRest()
        {
            if (_mesh == null || _nVerts == 0)
                return;

            Array.Copy(_restPositions, _deformedPositions, _nVerts);
            RecomputeSmoothNormals(_deformedPositions, _normals);

            if (_useDirectVertexRegion)
            {
                PackPositionsAndNormals(_deformedPositions, _normals, _gpuVertexBuffer);
                RenderingServer.MeshSurfaceUpdateVertexRegion(_meshRid, 0, 0, _gpuVertexBuffer);
            }
        }

        /// <summary>
        /// Computes area-weighted smooth outward vertex normals in Godot right-handed coordinates.
        /// Uses the original contract CCW triangle order (i0, i1, i2): since Godot = (x, z, -y) is
        /// a proper rotation (+90 deg about X, det = +1), (v1 - v0) x (v2 - v0) points outward.
        /// </summary>
        private void RecomputeSmoothNormals(Vector3[] positions, Vector3[] outNormals)
        {
            Array.Clear(outNormals, 0, _nVerts);
            int[] tris = _ccwTrisFlat;
            for (int t = 0; t < _nTris; t++)
            {
                int i0 = tris[3 * t];
                int i1 = tris[3 * t + 1];
                int i2 = tris[3 * t + 2];

                Vector3 p0 = positions[i0];
                Vector3 e1 = positions[i1] - p0;
                Vector3 e2 = positions[i2] - p0;
                // Cross product magnitude equals 2x triangle area -> natural area-weighted smooth normal
                Vector3 fn = e1.Cross(e2);

                outNormals[i0] += fn;
                outNormals[i1] += fn;
                outNormals[i2] += fn;
            }

            for (int i = 0; i < _nVerts; i++)
            {
                Vector3 n = outNormals[i];
                float lenSq = n.LengthSquared();
                outNormals[i] = lenSq > 1e-20f ? n / MathF.Sqrt(lenSq) : Vector3.Up;
            }
        }

        private void PackPositions(Vector3[] positions, byte[] dst)
        {
            Span<byte> span = dst.AsSpan();
            int vOff = _vertexOffset;
            int vStr = _vertexStride;
            for (int i = 0; i < _nVerts; i++)
            {
                Vector3 p = positions[i];
                int vo = vOff + i * vStr;
                MemoryMarshal.Write(span.Slice(vo, 4), in p.X);
                MemoryMarshal.Write(span.Slice(vo + 4, 4), in p.Y);
                MemoryMarshal.Write(span.Slice(vo + 8, 4), in p.Z);
            }
        }

        private void PackPositionsAndNormals(Vector3[] positions, Vector3[] normals, byte[] dst)
        {
            PackPositions(positions, dst);

            Span<byte> span = dst.AsSpan();
            int nOff = _normalOffset;
            int nStr = _normalStride;
            NormalEncoding enc = _normalEncoding;
            for (int i = 0; i < _nVerts; i++)
            {
                PackSingleNormal(normals[i], enc, span.Slice(nOff + i * nStr, nStr));
            }
        }

        private static void PackSingleNormal(Vector3 n, NormalEncoding encoding, Span<byte> dst)
        {
            switch (encoding)
            {
                case NormalEncoding.OctahedralU16:
                {
                    (float ox, float oy) = EncodeOctahedral(n);
                    ushort ux = (ushort)Math.Clamp((int)MathF.Round(ox * 65535f), 0, 65535);
                    ushort uy = (ushort)Math.Clamp((int)MathF.Round(oy * 65535f), 0, 65535);
                    MemoryMarshal.Write(dst.Slice(0, 2), in ux);
                    MemoryMarshal.Write(dst.Slice(2, 2), in uy);
                    break;
                }
                case NormalEncoding.OctahedralF32:
                {
                    (float ox, float oy) = EncodeOctahedral(n);
                    MemoryMarshal.Write(dst.Slice(0, 4), in ox);
                    MemoryMarshal.Write(dst.Slice(4, 4), in oy);
                    break;
                }
                case NormalEncoding.Half4:
                {
                    Half hx = (Half)n.X;
                    Half hy = (Half)n.Y;
                    Half hz = (Half)n.Z;
                    MemoryMarshal.Write(dst.Slice(0, 2), in hx);
                    MemoryMarshal.Write(dst.Slice(2, 2), in hy);
                    MemoryMarshal.Write(dst.Slice(4, 2), in hz);
                    break;
                }
                case NormalEncoding.Snorm16X4:
                {
                    short sx = (short)Math.Clamp((int)MathF.Round(n.X * 32767f), -32767, 32767);
                    short sy = (short)Math.Clamp((int)MathF.Round(n.Y * 32767f), -32767, 32767);
                    short sz = (short)Math.Clamp((int)MathF.Round(n.Z * 32767f), -32767, 32767);
                    MemoryMarshal.Write(dst.Slice(0, 2), in sx);
                    MemoryMarshal.Write(dst.Slice(2, 2), in sy);
                    MemoryMarshal.Write(dst.Slice(4, 2), in sz);
                    break;
                }
                case NormalEncoding.Float32X3:
                {
                    MemoryMarshal.Write(dst.Slice(0, 4), in n.X);
                    MemoryMarshal.Write(dst.Slice(4, 4), in n.Y);
                    MemoryMarshal.Write(dst.Slice(8, 4), in n.Z);
                    break;
                }
            }
        }

        /// <summary>
        /// Exact match to Godot 4's core/math/vector3.cpp Vector3::octahedron_encode() in [0, 1]^2.
        /// </summary>
        private static (float ox, float oy) EncodeOctahedral(Vector3 n)
        {
            float sum = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
            float inv = sum > 1e-12f ? 1f / sum : 0f;
            float nx = n.X * inv;
            float ny = n.Y * inv;
            float nz = n.Z * inv;

            float ox, oy;
            if (nz >= 0f)
            {
                ox = nx;
                oy = ny;
            }
            else
            {
                ox = (1f - MathF.Abs(ny)) * (nx >= 0f ? 1f : -1f);
                oy = (1f - MathF.Abs(nx)) * (ny >= 0f ? 1f : -1f);
            }

            return (Math.Clamp(ox * 0.5f + 0.5f, 0f, 1f), Math.Clamp(oy * 0.5f + 0.5f, 0f, 1f));
        }
    }
}
