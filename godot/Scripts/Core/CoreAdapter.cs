using System;
using DeformGrip.Core;

namespace DeformGrip.App.Core
{
    /// <summary>
    /// Thin adapter boundary between the Godot scene and the plain-C# core library (../core/).
    /// When Role 2b delivers core/*.csproj, set UseCoreStub=false in DeformGrip.csproj and
    /// adjust RealDeformCore below if Role 2b's namespaces or method names differ.
    /// </summary>
    public interface IDeformCore
    {
        int NSurface { get; }
        bool Stage2Enabled { get; }
        bool IsStub { get; }

        /// <summary>
        /// Computes physical surface displacements u in SI metres (contract coordinates x,y,z)
        /// into outDisp (length 3*NSurface, interleaved xyz). Visual gain is NEVER applied here
        /// (CONTRACT §3 & §4.4: all arrays are SI metres; visual exaggeration is Godot-only).
        /// </summary>
        void ApplyPhysical(ReadOnlySpan<NodalLoad> loads, float[] outDisp);

        /// <summary>
        /// Stage 2 only: advances viscoelastic state by dt for piecewise-linear forces fPrev -> fNext
        /// (dense length 3*NSurface), then applies G to the resulting effective force.
        /// </summary>
        void StepViscoelastic(ReadOnlySpan<double> fPrev, ReadOnlySpan<double> fNext, double dt, float[] outDisp);

        void ResetState();
    }

    public static class DeformCoreFactory
    {
        public static IDeformCore Load(
            string gBinPath,
            int nSurface,
            bool stage2Enabled,
            string stage2Model,
            double eInfPa,
            double e1Pa,
            double tau1S)
        {
#if USE_CORE_STUB
            return new StubDeformCore(gBinPath, nSurface, stage2Enabled, stage2Model, eInfPa, e1Pa, tau1S);
#else
            return new RealDeformCore(gBinPath, nSurface, stage2Enabled, stage2Model, eInfPa, e1Pa, tau1S);
#endif
        }

        public static bool IsUsingStub
        {
            get
            {
#if USE_CORE_STUB
                return true;
#else
                return false;
#endif
            }
        }
    }

#if USE_CORE_STUB
    internal sealed class StubDeformCore : IDeformCore
    {
        private readonly InfluenceMatrix _matrix;
        private readonly ViscoelasticState _visco;
        private readonly NodalLoad[] _effLoadBuffer;

        public int NSurface => _matrix.NSurface;
        public bool Stage2Enabled { get; }
        public bool IsStub => true;

        public StubDeformCore(
            string gBinPath,
            int nSurface,
            bool stage2Enabled,
            string stage2Model,
            double eInfPa,
            double e1Pa,
            double tau1S)
        {
            _matrix = InfluenceMatrix.Load(gBinPath, nSurface);
            Stage2Enabled = stage2Enabled;
            _visco = new ViscoelasticState(nSurface, eInfPa, e1Pa, tau1S, stage2Model, elasticMode: !stage2Enabled);
            _effLoadBuffer = new NodalLoad[nSurface];
        }

        public void ApplyPhysical(ReadOnlySpan<NodalLoad> loads, float[] outDisp)
        {
            // Pass gain = 1.0f so outDisp stays strictly in SI metres (CONTRACT §3).
            _matrix.Apply(loads, 1.0f, outDisp);
        }

        public void StepViscoelastic(ReadOnlySpan<double> fPrev, ReadOnlySpan<double> fNext, double dt, float[] outDisp)
        {
            ReadOnlySpan<double> fEff = _visco.Step(fPrev, fNext, dt);
            int count = 0;
            int nSurface = NSurface;
            for (int row = 0; row < nSurface; row++)
            {
                double fx = fEff[3 * row];
                double fy = fEff[3 * row + 1];
                double fz = fEff[3 * row + 2];
                if (fx != 0.0 || fy != 0.0 || fz != 0.0)
                {
                    _effLoadBuffer[count++] = new NodalLoad(row, fx, fy, fz);
                }
            }
            _matrix.Apply(_effLoadBuffer.AsSpan(0, count), 1.0f, outDisp);
        }

        public void ResetState()
        {
            _visco.Reset();
        }
    }
#else
    /// <summary>
    /// Adapter for Role 2b's core library (core/DeformGrip.Core.csproj).
    /// </summary>
    internal sealed class RealDeformCore : IDeformCore
    {
        private readonly InfluenceMatrix _matrix;
        private readonly ViscoelasticState _visco;
        private readonly NodalLoad[] _effLoadBuffer;

        public int NSurface { get; }
        public bool Stage2Enabled { get; }
        public bool IsStub => false;

        public RealDeformCore(
            string gBinPath,
            int nSurface,
            bool stage2Enabled,
            string stage2Model,
            double eInfPa,
            double e1Pa,
            double tau1S)
        {
            NSurface = nSurface;
            Stage2Enabled = stage2Enabled;
            _matrix = InfluenceMatrix.Load(gBinPath, nSurface);
            _visco = new ViscoelasticState(nSurface, eInfPa, e1Pa, tau1S, stage2Model, elasticMode: !stage2Enabled);
            _effLoadBuffer = new NodalLoad[nSurface];
        }

        public void ApplyPhysical(ReadOnlySpan<NodalLoad> loads, float[] outDisp)
        {
            // Pass gain = 1.0f so outDisp stays strictly in SI metres (CONTRACT section 3).
            _matrix.Apply(loads, 1.0f, outDisp);
        }

        public void StepViscoelastic(ReadOnlySpan<double> fPrev, ReadOnlySpan<double> fNext, double dt, float[] outDisp)
        {
            ReadOnlySpan<double> fEff = _visco.Step(fPrev, fNext, dt);
            int count = 0;
            for (int row = 0; row < NSurface; row++)
            {
                double fx = fEff[3 * row];
                double fy = fEff[3 * row + 1];
                double fz = fEff[3 * row + 2];
                if (fx != 0.0 || fy != 0.0 || fz != 0.0)
                {
                    _effLoadBuffer[count++] = new NodalLoad(row, fx, fy, fz);
                }
            }
            _matrix.Apply(_effLoadBuffer.AsSpan(0, count), 1.0f, outDisp);
        }

        public void ResetState()
        {
            _visco.Reset();
        }
    }
#endif
}
