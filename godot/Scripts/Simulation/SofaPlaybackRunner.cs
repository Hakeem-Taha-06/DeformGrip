using System;
using DeformGrip.App.Data;

namespace DeformGrip.App.Simulation
{
    /// <summary>
    /// Plays back precomputed SOFA force-table displacements (CONTRACT §6.7)
    /// by finding frames bracketing the current amplitude and linearly interpolating.
    /// Zero per-frame heap allocations.
    /// </summary>
    public sealed class SofaPlaybackRunner
    {
        private SofaPlaybackData? _data;
        private float[] _dispBuffer;

        public bool IsLoaded => _data != null;
        public int NSurface => _data?.NSurface ?? 0;
        public float[] Displacements => _dispBuffer;
        public float MaxDisplacementM { get; private set; }

        public SofaPlaybackRunner(int initialNSurface)
        {
            _dispBuffer = new float[Math.Max(1, initialNSurface) * 3];
        }

        public void SetPlaybackData(SofaPlaybackData? data, int nSurface)
        {
            _data = data;
            int required = Math.Max(1, nSurface) * 3;
            if (_dispBuffer.Length != required)
                _dispBuffer = new float[required];
            Array.Clear(_dispBuffer, 0, _dispBuffer.Length);
            MaxDisplacementM = 0f;
        }

        public void EvaluateAtAmplitude(double amplitudeN)
        {
            if (_data == null)
            {
                Array.Clear(_dispBuffer, 0, _dispBuffer.Length);
                MaxDisplacementM = 0f;
                return;
            }

            double[] forces = _data.Meta.ForcesN;
            int nFrames = forces.Length;
            int nSurface = _data.NSurface;
            int stride = 3 * nSurface;
            float[] table = _data.Displacements;

            if (nFrames == 1 || amplitudeN <= forces[0])
            {
                Array.Copy(table, 0, _dispBuffer, 0, stride);
                MaxDisplacementM = OursRunner.ComputeMaxDisplacement(_dispBuffer, nSurface);
                return;
            }

            if (amplitudeN >= forces[nFrames - 1])
            {
                Array.Copy(table, (nFrames - 1) * stride, _dispBuffer, 0, stride);
                MaxDisplacementM = OursRunner.ComputeMaxDisplacement(_dispBuffer, nSurface);
                return;
            }

            // Locate bracketing interval [i0, i0+1] (forces_N is ascending, typically 11 entries)
            int i0 = 0;
            for (int i = 0; i < nFrames - 1; i++)
            {
                if (amplitudeN <= forces[i + 1])
                {
                    i0 = i;
                    break;
                }
            }

            double f0 = forces[i0];
            double f1 = forces[i0 + 1];
            float alpha = (f1 > f0) ? (float)((amplitudeN - f0) / (f1 - f0)) : 0f;
            float oneMinusAlpha = 1f - alpha;

            int off0 = i0 * stride;
            int off1 = (i0 + 1) * stride;
            for (int k = 0; k < stride; k++)
            {
                _dispBuffer[k] = oneMinusAlpha * table[off0 + k] + alpha * table[off1 + k];
            }

            MaxDisplacementM = OursRunner.ComputeMaxDisplacement(_dispBuffer, nSurface);
        }
    }
}
