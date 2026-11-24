#if USE_CORE_STUB
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DeformGrip.Core
{
    /// <summary>
    /// Temporary Role 1b stub mirroring solver/README.md Role 2b Task 3.
    /// Excluded from compilation once UseCoreStub=false and ../core/ is referenced.
    /// </summary>
    public readonly struct NodalLoad
    {
        public readonly int Row;
        public readonly double Fx;
        public readonly double Fy;
        public readonly double Fz;

        public NodalLoad(int row, double fx, double fy, double fz)
        {
            Row = row;
            Fx = fx;
            Fy = fy;
            Fz = fz;
        }
    }

    /// <summary>
    /// Temporary Role 1b stub implementing CONTRACT 4.3, 4.4, and 6.5:
    /// column-major float32 LE surface influence matrix G of shape (3*nSurface, 3*nSurface).
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

        public static InfluenceMatrix Load(string gBinPath, int nSurface)
        {
            if (nSurface <= 0)
                throw new ArgumentOutOfRangeException(nameof(nSurface));
            if (!File.Exists(gBinPath))
                throw new FileNotFoundException($"Influence matrix binary not found: {gBinPath}", gBinPath);

            int nDof = 3 * nSurface;
            long expectedFloats = (long)nDof * nDof;
            long expectedBytes = expectedFloats * sizeof(float);

            var fileInfo = new FileInfo(gBinPath);
            if (fileInfo.Length != expectedBytes)
            {
                throw new InvalidDataException(
                    $"G.bin size mismatch for n_surface={nSurface}: got {fileInfo.Length} bytes, expected {expectedBytes} bytes.");
            }

            float[] data = new float[expectedFloats];
            using var fs = new FileStream(gBinPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            Span<byte> bytes = MemoryMarshal.AsBytes(data.AsSpan());
            int totalRead = 0;
            while (totalRead < bytes.Length)
            {
                int read = fs.Read(bytes.Slice(totalRead));
                if (read == 0)
                    throw new EndOfStreamException($"Unexpected EOF while reading {gBinPath}.");
                totalRead += read;
            }

            return new InfluenceMatrix(data, nSurface);
        }

        /// <summary>
        /// Writes gain * sum_i (f * G[:, 3*row + c]) into outDisp (length 3*nSurface, interleaved xyz).
        /// Skips exact zero components.
        /// </summary>
        public void Apply(ReadOnlySpan<NodalLoad> loads, float gain, float[] outDisp)
        {
            if (outDisp == null || outDisp.Length < NDof)
                throw new ArgumentException($"outDisp must have length >= {NDof}.", nameof(outDisp));

            Array.Clear(outDisp, 0, NDof);

            int nDof = NDof;
            float[] g = _data;

            for (int i = 0; i < loads.Length; i++)
            {
                ref readonly NodalLoad load = ref loads[i];
                int baseCol = 3 * load.Row;
                if ((uint)load.Row >= (uint)NSurface)
                    throw new ArgumentOutOfRangeException(nameof(loads), $"Surface row {load.Row} out of range [0, {NSurface}).");

                float fx = (float)(load.Fx * gain);
                if (fx != 0f)
                    AccumulateColumn(g, baseCol, fx, nDof, outDisp);

                float fy = (float)(load.Fy * gain);
                if (fy != 0f)
                    AccumulateColumn(g, baseCol + 1, fy, nDof, outDisp);

                float fz = (float)(load.Fz * gain);
                if (fz != 0f)
                    AccumulateColumn(g, baseCol + 2, fz, nDof, outDisp);
            }
        }

        private static void AccumulateColumn(float[] g, int col, float scale, int nDof, float[] outDisp)
        {
            int offset = col * nDof;
            for (int r = 0; r < nDof; r++)
            {
                outDisp[r] += scale * g[offset + r];
            }
        }
    }

    /// <summary>
    /// Temporary Role 1b stub for Stage 2 viscoelastic effective-force state (CONTRACT 4.5 & O1).
    /// Supports "elastic", "sls" (standard linear solid), and "kv" (Kelvin-Voigt fallback).
    /// </summary>
    public sealed class ViscoelasticState
    {
        private readonly int _nDof;
        private readonly double _eInf;
        private readonly double _e1;
        private readonly double _e0;
        private readonly double _tau1;
        private readonly string _model;
        private readonly bool _elasticMode;

        private readonly double[] _p;
        private readonly double[] _fEff;

        public ViscoelasticState(int nSurface, double eInfPa, double e1Pa, double tau1S, string model, bool elasticMode)
        {
            _nDof = 3 * nSurface;
            _eInf = eInfPa;
            _e1 = e1Pa;
            _e0 = eInfPa + e1Pa;
            _tau1 = tau1S;
            _model = model ?? "sls";
            _elasticMode = elasticMode;
            _p = new double[_nDof];
            _fEff = new double[_nDof];
        }

        public void Reset()
        {
            Array.Clear(_p, 0, _nDof);
            Array.Clear(_fEff, 0, _nDof);
        }

        /// <summary>
        /// Advances state by one step dt for piecewise-linear force fPrev -> fNext across all 3*nSurface DOFs.
        /// Returns effective force array fEff (length 3*nSurface).
        /// </summary>
        public ReadOnlySpan<double> Step(ReadOnlySpan<double> fPrev, ReadOnlySpan<double> fNext, double dt)
        {
            if (_elasticMode || dt <= 0.0)
            {
                fNext.Slice(0, _nDof).CopyTo(_fEff);
                return _fEff;
            }

            if (_model == "kv")
            {
                // First-order lag fallback (CONTRACT O1): beta * f_eff' + f_eff = f, with beta = tau1
                double a = dt / _tau1;
                double ea = Math.Exp(-a);
                double invA = a > 1e-12 ? (1.0 - ea) / a : 1.0 - 0.5 * a;
                for (int d = 0; d < _nDof; d++)
                {
                    double fp = fPrev[d];
                    double fn = fNext[d];
                    double df = fn - fp;
                    _fEff[d] = ea * _fEff[d] + fp * (1.0 - ea) + df * (1.0 - invA);
                }
                return _fEff;
            }

            // Standard Linear Solid (CONTRACT 4.5 exact update for linear force on [0, dt]):
            // a = (E_inf / (E0 * tau1)) * dt, ea = exp(-a)
            // p_next = ea*p + (E0/E_inf) * (f_prev*(1 - ea) + df*(1 - (1 - ea)/a))
            // f_eff_next = f_next + (E1/E0) * p_next
            double alpha = (_eInf / (_e0 * _tau1)) * dt;
            double eAlpha = Math.Exp(-alpha);
            double oneMinusE = 1.0 - eAlpha;
            double rampCoeff = alpha > 1e-12 ? (1.0 - oneMinusE / alpha) : 0.5 * alpha;
            double e0OverEInf = _e0 / _eInf;
            double e1OverE0 = _e1 / _e0;

            for (int d = 0; d < _nDof; d++)
            {
                double fp = fPrev[d];
                double fn = fNext[d];
                double df = fn - fp;
                double pNext = eAlpha * _p[d] + e0OverEInf * (fp * oneMinusE + df * rampCoeff);
                _p[d] = pNext;
                _fEff[d] = fn + e1OverE0 * pNext;
            }

            return _fEff;
        }
    }
}
#endif
