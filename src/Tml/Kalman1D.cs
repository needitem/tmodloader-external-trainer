namespace TerrariaTrainer.Tml;

/// <summary>
/// Minimal 1-D constant-velocity Kalman filter (state = position + velocity). Two of these (X and Y)
/// smooth a moving target's path and, crucially, estimate its velocity from noisy/erratic position
/// samples — which the aimbot then multiplies by the projectile travel time to lead the shot. x and y
/// are independent under a constant-velocity model, so a pair of 1-D filters is exact and far cheaper
/// than a 4-D matrix filter. Units are game pixels and pixels/frame; dt is in frames.
/// </summary>
public struct Kalman1D
{
    private double _p, _v;                       // state: position, velocity
    private double _p00, _p01, _p10, _p11;       // covariance
    private bool _init;

    public double Pos => _p;
    public double Vel => _v;
    public void Reset() => _init = false;

    /// <summary><paramref name="sa"/> = target accel std-dev (process noise; higher tracks sharper turns,
    /// lower is smoother). <paramref name="r"/> = position measurement noise (higher = more smoothing/lag).</summary>
    public void Update(double z, double dt, double sa, double r)
    {
        if (!_init) { _p = z; _v = 0; _p00 = 1; _p01 = 0; _p10 = 0; _p11 = 1; _init = true; return; }
        if (dt <= 0) dt = 0.0001;

        // --- predict: p += v*dt ; P = F P Fᵀ + Q ---
        double pp = _p + _v * dt, pv = _v;
        double dt2 = dt * dt, dt3 = dt2 * dt, dt4 = dt3 * dt, sa2 = sa * sa;
        double a00 = _p00 + dt * (_p10 + _p01) + dt2 * _p11 + sa2 * dt4 / 4.0;
        double a01 = _p01 + dt * _p11 + sa2 * dt3 / 2.0;
        double a10 = _p10 + dt * _p11 + sa2 * dt3 / 2.0;
        double a11 = _p11 + sa2 * dt2;

        // --- update with position measurement z (H = [1 0]) ---
        double s = a00 + r;
        double k0 = a00 / s, k1 = a10 / s;
        double y = z - pp;
        _p = pp + k0 * y;
        _v = pv + k1 * y;
        _p00 = (1 - k0) * a00; _p01 = (1 - k0) * a01;
        _p10 = a10 - k1 * a00; _p11 = a11 - k1 * a01;
    }
}
