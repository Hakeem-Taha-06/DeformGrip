namespace DeformGrip.Core
{
    /// <summary>
    /// Force vector (Fx, Fy, Fz) in newtons applied to one surface row (CONTRACT §3, §4.2).
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
}
