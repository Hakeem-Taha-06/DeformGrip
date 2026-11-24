using System;
using DeformGrip.App.Data;
using DeformGrip.Core;

namespace DeformGrip.App.Input
{
    public enum ForceInputMode
    {
        ManualSlider,
        ProfileReplay
    }

    /// <summary>
    /// Case-based IForceInput implementation (godot/README.md Task 3):
    /// scales the unit-normalized spatial load pattern p from config/cases/<level>/<case_id>.json
    /// by an amplitude (N) taken from either a manual slider (0–20 N) or a replayed CSV profile.
    /// Zero heap allocations in FillLoads.
    /// </summary>
    public sealed class CaseForceSource : IForceInput
    {
        private CaseData? _case;
        private ProfileData? _profile;

        public ForceInputMode Mode { get; set; } = ForceInputMode.ManualSlider;
        public double ManualAmplitudeN { get; set; } = 0.0;
        public double CurrentAmplitudeN { get; private set; }
        public CaseData? ActiveCase => _case;
        public ProfileData? ActiveProfile => _profile;

        public string SourceLabel => Mode == ForceInputMode.ManualSlider
            ? $"Manual ({ManualAmplitudeN:F2} N)"
            : $"Profile: {_profile?.ProfileId ?? "none"}";

        public void SetCase(CaseData caseData)
        {
            _case = caseData;
        }

        public void SetProfile(ProfileData? profile)
        {
            _profile = profile;
        }

        public double EvaluateAmplitude(double timeSec)
        {
            double amp = Mode == ForceInputMode.ManualSlider
                ? ManualAmplitudeN
                : (_profile != null ? _profile.SampleAmplitude(timeSec) : 0.0);
            CurrentAmplitudeN = amp;
            return amp;
        }

        public int FillLoads(double timeSec, Span<NodalLoad> buffer)
        {
            double amp = EvaluateAmplitude(timeSec);
            return FillLoadsForAmplitude(amp, buffer);
        }

        public int FillLoadsForAmplitude(double amplitudeN, Span<NodalLoad> buffer)
        {
            CurrentAmplitudeN = amplitudeN;
            if (_case == null || amplitudeN == 0.0)
                return 0;

            CaseLoadEntry[] pattern = _case.Loads;
            if (buffer.Length < pattern.Length)
                throw new ArgumentException($"Load buffer length ({buffer.Length}) < case loads ({pattern.Length}).", nameof(buffer));

            int written = 0;
            for (int i = 0; i < pattern.Length; i++)
            {
                CaseLoadEntry entry = pattern[i];
                double[] f = entry.F;
                double fx = amplitudeN * f[0];
                double fy = amplitudeN * f[1];
                double fz = amplitudeN * f[2];
                if (fx != 0.0 || fy != 0.0 || fz != 0.0)
                {
                    buffer[written++] = new NodalLoad(entry.Row, fx, fy, fz);
                }
            }
            return written;
        }

        /// <summary>
        /// Writes dense surface force vector (length 3*nSurface) for Stage 2 stepping without allocations.
        /// </summary>
        public void FillDenseForces(double timeSec, double[] denseBuffer)
        {
            Array.Clear(denseBuffer, 0, denseBuffer.Length);
            double amp = EvaluateAmplitude(timeSec);
            if (_case == null || amp == 0.0)
                return;

            CaseLoadEntry[] pattern = _case.Loads;
            for (int i = 0; i < pattern.Length; i++)
            {
                int row = pattern[i].Row;
                double[] f = pattern[i].F;
                denseBuffer[3 * row] = amp * f[0];
                denseBuffer[3 * row + 1] = amp * f[1];
                denseBuffer[3 * row + 2] = amp * f[2];
            }
        }
    }
}
