using System;
using DeformGrip.App.Core;
using DeformGrip.App.Input;
using DeformGrip.Core;

namespace DeformGrip.App.Simulation
{
    /// <summary>
    /// Executes "ours" precomputed influence-matrix simulation (Stage 1 elastic, plus Stage 2
    /// dt_force viscoelastic stepping when stage2.enabled is true).
    /// All displacements are stored in contract coordinates (x, y, z) and SI metres.
    /// Zero heap allocations per frame.
    /// </summary>
    public sealed class OursRunner
    {
        private readonly IDeformCore _core;
        private readonly double _dtForceS;
        private readonly NodalLoad[] _loadBuffer;
        private readonly float[] _dispBuffer;

        // Preallocated dense buffers used only when Stage 2 is enabled
        private readonly double[] _fPrevDense;
        private readonly double[] _fNextDense;
        private double _lastTimeS;
        private bool _hasStepped;

        public int NSurface => _core.NSurface;
        public bool Stage2Enabled => _core.Stage2Enabled;
        public bool IsStub => _core.IsStub;
        public float[] Displacements => _dispBuffer;
        public float MaxDisplacementM { get; private set; }
        public int ActiveLoadCount { get; private set; }

        public OursRunner(IDeformCore core, double dtForceS)
        {
            _core = core;
            _dtForceS = dtForceS > 0.0 ? dtForceS : 0.001;
            int nSurface = core.NSurface;
            _loadBuffer = new NodalLoad[nSurface];
            _dispBuffer = new float[3 * nSurface];
            _fPrevDense = new double[3 * nSurface];
            _fNextDense = new double[3 * nSurface];
        }

        public void Reset()
        {
            _core.ResetState();
            Array.Clear(_dispBuffer, 0, _dispBuffer.Length);
            Array.Clear(_fPrevDense, 0, _fPrevDense.Length);
            Array.Clear(_fNextDense, 0, _fNextDense.Length);
            _lastTimeS = 0.0;
            _hasStepped = false;
            MaxDisplacementM = 0f;
            ActiveLoadCount = 0;
        }

        /// <summary>
        /// Updates _dispBuffer for simulation time timeSec using any IForceInput implementation.
        /// </summary>
        public void StepFrame(double timeSec, IForceInput forceInput)
        {
            if (!_core.Stage2Enabled)
            {
                int nLoads = forceInput.FillLoads(timeSec, _loadBuffer);
                ActiveLoadCount = nLoads;
                _core.ApplyPhysical(_loadBuffer.AsSpan(0, nLoads), _dispBuffer);
                MaxDisplacementM = ComputeMaxDisplacement(_dispBuffer, NSurface);
                return;
            }

            // Stage 2: advance viscoelastic state in steps of dt_force through all force samples
            // since the last rendered frame (godot/README.md Task 4, CONTRACT §4.5).
            if (!_hasStepped || timeSec < _lastTimeS - 1e-9)
            {
                Reset();
                FillDenseFromInput(timeSec, forceInput, _fPrevDense);
                _core.StepViscoelastic(_fPrevDense, _fPrevDense, 0.0, _dispBuffer);
                _lastTimeS = timeSec;
                _hasStepped = true;
                MaxDisplacementM = ComputeMaxDisplacement(_dispBuffer, NSurface);
                return;
            }

            double elapsed = timeSec - _lastTimeS;
            if (elapsed <= 1e-12)
            {
                return;
            }

            int nSteps = Math.Max(1, (int)Math.Floor(elapsed / _dtForceS + 1e-9));
            double tCursor = _lastTimeS;
            for (int s = 1; s <= nSteps; s++)
            {
                double tNext = (s == nSteps) ? timeSec : (_lastTimeS + s * _dtForceS);
                double stepDt = tNext - tCursor;
                FillDenseFromInput(tNext, forceInput, _fNextDense);
                _core.StepViscoelastic(_fPrevDense, _fNextDense, stepDt, _dispBuffer);
                Array.Copy(_fNextDense, _fPrevDense, _fPrevDense.Length);
                tCursor = tNext;
            }

            _lastTimeS = timeSec;
            MaxDisplacementM = ComputeMaxDisplacement(_dispBuffer, NSurface);
        }

        /// <summary>
        /// Direct Stage 1 evaluation at an explicit amplitude (used for manual slider or dump/benchmark).
        /// </summary>
        public void EvaluateCaseAtAmplitude(CaseForceSource source, double amplitudeN)
        {
            int nLoads = source.FillLoadsForAmplitude(amplitudeN, _loadBuffer);
            ActiveLoadCount = nLoads;
            _core.ApplyPhysical(_loadBuffer.AsSpan(0, nLoads), _dispBuffer);
            MaxDisplacementM = ComputeMaxDisplacement(_dispBuffer, NSurface);
        }

        private void FillDenseFromInput(double timeSec, IForceInput forceInput, double[] denseOut)
        {
            Array.Clear(denseOut, 0, denseOut.Length);
            int nLoads = forceInput.FillLoads(timeSec, _loadBuffer);
            ActiveLoadCount = nLoads;
            for (int i = 0; i < nLoads; i++)
            {
                ref readonly NodalLoad ld = ref _loadBuffer[i];
                int baseIdx = 3 * ld.Row;
                denseOut[baseIdx] = ld.Fx;
                denseOut[baseIdx + 1] = ld.Fy;
                denseOut[baseIdx + 2] = ld.Fz;
            }
        }

        public static float ComputeMaxDisplacement(float[] disp, int nSurface)
        {
            float maxSq = 0f;
            for (int i = 0; i < nSurface; i++)
            {
                int o = 3 * i;
                float x = disp[o];
                float y = disp[o + 1];
                float z = disp[o + 2];
                float sq = x * x + y * y + z * z;
                if (sq > maxSq)
                    maxSq = sq;
            }
            return MathF.Sqrt(maxSq);
        }
    }
}
