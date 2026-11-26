using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DeformGrip.Core
{
    /// <summary>
    /// Precomputed linear-elasticity surface influence matrix G (CONTRACT §4.3, §4.4, §6.5).
    /// Stored as column-major float32 of shape (3*NSurface, 3*NSurface), units m/N at modulus E0.
    /// </summary>
    public sealed class InfluenceMatrix
    {
        private readonly float[] _data;

        public int NSurface { get; }
        public int NDof { get; }

        private InfluenceMatrix(float[] data, int nSurface)
        {
            _data = data;
            NSurface = nSurface;
            NDof = 3 * nSurface;
        }

        /// <summary>
        /// Loads a column-major float32 G.bin file and validates its exact byte size.
        /// </summary>
        public static InfluenceMatrix Load(string gBinPath, int nSurface)
        {
            if (nSurface <= 0)
                throw new ArgumentOutOfRangeException(nameof(nSurface), "nSurface must be positive.");
            if (string.IsNullOrWhiteSpace(gBinPath))
                throw new ArgumentException("gBinPath must not be empty.", nameof(gBinPath));
            if (!File.Exists(gBinPath))
                throw new FileNotFoundException($"Influence matrix binary not found: {gBinPath}", gBinPath);

            int nDof = 3 * nSurface;
            long expectedFloats = (long)nDof * nDof;
            long expectedBytes = expectedFloats * sizeof(float);

            var fi = new FileInfo(gBinPath);
            if (fi.Length != expectedBytes)
            {
                throw new InvalidDataException(
                    $"G.bin byte size mismatch for n_surface={nSurface}: got {fi.Length}, expected {expectedBytes}.");
            }

            float[] data = new float[expectedFloats];
            using var fs = new FileStream(gBinPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            Span<byte> bytes = MemoryMarshal.AsBytes(data.AsSpan());
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = fs.Read(bytes.Slice(offset));
                if (read == 0)
                    throw new EndOfStreamException($"Unexpected EOF while reading {gBinPath}.");
                offset += read;
            }

            return new InfluenceMatrix(data, nSurface);
        }

        /// <summary>
        /// Computes outDisp = gain * sum_i (f * G[:, 3*row + c]) in interleaved xyz layout.
        /// Zero components are skipped; performs no heap allocations.
        /// </summary>
        public void Apply(ReadOnlySpan<NodalLoad> loads, float gain, float[] outDisp)
        {
            if (outDisp == null || outDisp.Length < NDof)
                throw new ArgumentException($"outDisp must have length >= {NDof}.", nameof(outDisp));

            Array.Clear(outDisp, 0, NDof);
            if (gain == 0f || loads.Length == 0)
                return;

            int nDof = NDof;
            float[] g = _data;

            for (int i = 0; i < loads.Length; i++)
            {
                ref readonly NodalLoad load = ref loads[i];
                if ((uint)load.Row >= (uint)NSurface)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(loads),
                        $"Surface row {load.Row} is out of range [0, {NSurface}).");
                }

                int baseCol = 3 * load.Row;
                if (load.Fx != 0.0)
                    AccumulateColumn(g, baseCol, (float)(load.Fx * gain), nDof, outDisp);
                if (load.Fy != 0.0)
                    AccumulateColumn(g, baseCol + 1, (float)(load.Fy * gain), nDof, outDisp);
                if (load.Fz != 0.0)
                    AccumulateColumn(g, baseCol + 2, (float)(load.Fz * gain), nDof, outDisp);
            }
        }

        private static void AccumulateColumn(float[] g, int col, float scale, int nDof, float[] outDisp)
        {
            if (scale == 0f)
                return;

            int offset = col * nDof;
            int r = 0;

            if (Vector.IsHardwareAccelerated && nDof >= Vector<float>.Count)
            {
                int vecLen = Vector<float>.Count;
                int limit = nDof - vecLen;
                var vScale = new Vector<float>(scale);
                for (; r <= limit; r += vecLen)
                {
                    var vG = new Vector<float>(g, offset + r);
                    var vOut = new Vector<float>(outDisp, r);
                    (vOut + vScale * vG).CopyTo(outDisp, r);
                }
            }

            for (; r < nDof; r++)
            {
                outDisp[r] += scale * g[offset + r];
            }
        }
    }
}
