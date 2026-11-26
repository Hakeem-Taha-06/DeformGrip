using System;

namespace DeformGrip.Core
{
    /// <summary>
    /// Stage 2 per-DOF viscoelastic effective-force state (CONTRACT §4.5 and O1).
    /// Supports "sls" (one-term Prony / standard linear solid), "kv" (Kelvin-Voigt lag),
    /// and an elastic bypass mode where f_eff == f.
    /// </summary>
    public sealed class ViscoelasticState
    {
        private readonly int _nDof;
        private readonly double _eInf;
        private readonly double _e1;
        private readonly double _e0;
        private readonly double _tau1;
        private readonly string _model;
        private readonly double[] _p;
        private readonly double[] _fEff;

        public int NSurface { get; }
        public int NDof => _nDof;
        public bool ElasticMode { get; set; }

        public ViscoelasticState(
            int nSurface,
            double eInfPa,
            double e1Pa,
            double tau1S,
            string model = "sls",
            bool elasticMode = false)
        {
            if (nSurface <= 0)
                throw new ArgumentOutOfRangeException(nameof(nSurface), "nSurface must be positive.");
            if (eInfPa <= 0.0 || e1Pa < 0.0 || tau1S <= 0.0)
                throw new ArgumentOutOfRangeException("Material parameters must satisfy E_inf > 0, E1 >= 0, tau1 > 0.");

            NSurface = nSurface;
            _nDof = 3 * nSurface;
            _eInf = eInfPa;
            _e1 = e1Pa;
            _e0 = eInfPa + e1Pa;
            _tau1 = tau1S;
            _model = string.IsNullOrWhiteSpace(model) ? "sls" : model.Trim().ToLowerInvariant();
            if (_model != "sls" && _model != "kv")
                throw new ArgumentException($"Unsupported viscoelastic model '{model}' (expected 'sls' or 'kv').", nameof(model));

            ElasticMode = elasticMode;
            _p = new double[_nDof];
            _fEff = new double[_nDof];
        }

        public void Reset()
        {
            Array.Clear(_p, 0, _nDof);
            Array.Clear(_fEff, 0, _nDof);
        }

        /// <summary>
        /// Advances the viscoelastic state across [0, dt] for a piecewise-linear force fPrev -> fNext.
        /// Returns the dense effective-force vector of length 3*NSurface.
        /// </summary>
        public ReadOnlySpan<double> Step(ReadOnlySpan<double> fPrev, ReadOnlySpan<double> fNext, double dt)
        {
            if (fPrev.Length < _nDof || fNext.Length < _nDof)
                throw new ArgumentException($"Force spans must have length >= {_nDof}.");
            if (dt < 0.0)
                throw new ArgumentOutOfRangeException(nameof(dt), "dt must be non-negative.");

            if (ElasticMode || dt == 0.0)
            {
                fNext.Slice(0, _nDof).CopyTo(_fEff);
                return _fEff;
            }

            if (_model == "kv")
            {
                double a = dt / _tau1;
                double ea = Math.Exp(-a);
                double oneMinusE = 1.0 - ea;
                double rampCoeff = a > 1e-12 ? (1.0 - oneMinusE / a) : 0.5 * a;
                for (int d = 0; d < _nDof; d++)
                {
                    double fp = fPrev[d];
                    double fn = fNext[d];
                    _fEff[d] = ea * _fEff[d] + fp * oneMinusE + (fn - fp) * rampCoeff;
                }
                return _fEff;
            }

            // Standard Linear Solid (CONTRACT §4.5):
            // a = (E_inf / (E0 * tau1)) * dt, ea = exp(-a)
            // p_next = ea*p + (E0/E_inf) * (f_prev*(1 - ea) + df*(1 - (1 - ea)/a))
            // f_eff_next = f_next + (E1/E0) * p_next
            double alpha = (_eInf / (_e0 * _tau1)) * dt;
            double eAlpha = Math.Exp(-alpha);
            double oneMinusEAlpha = 1.0 - eAlpha;
            double slsRampCoeff = alpha > 1e-12 ? (1.0 - oneMinusEAlpha / alpha) : 0.5 * alpha;
            double e0OverEInf = _e0 / _eInf;
            double e1OverE0 = _e1 / _e0;

            for (int d = 0; d < _nDof; d++)
            {
                double fp = fPrev[d];
                double fn = fNext[d];
                double df = fn - fp;
                double pNext = eAlpha * _p[d] + e0OverEInf * (fp * oneMinusEAlpha + df * slsRampCoeff);
                _p[d] = pNext;
                _fEff[d] = fn + e1OverE0 * pNext;
            }

            return _fEff;
        }
    }
}
