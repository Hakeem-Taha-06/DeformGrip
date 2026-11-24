using Godot;

namespace DeformGrip.App.Data
{
    /// <summary>
    /// CONTRACT §3 coordinate transform and triangle winding rules:
    /// - Contract is right-handed (x, y, z) with +z as polar axis up and -z fixed cap at bottom.
    /// - Godot is right-handed Y-up: Godot = (x, z, -y) (pure +90 deg rotation around X; det = +1).
    /// - Contract triangles are CCW when viewed from outside; Godot front faces are CW,
    ///   so triangle indices are reversed (i0, i1, i2) -> (i0, i2, i1).
    /// </summary>
    public static class CoordMap
    {
        public static Vector3 ContractToGodot(double x, double y, double z)
        {
            return new Vector3((float)x, (float)z, (float)(-y));
        }

        public static Vector3 ContractToGodot(float x, float y, float z)
        {
            return new Vector3(x, z, -y);
        }

        /// <summary>
        /// Converts contract CCW triangle list [nTris][3] into Godot CW index array (length 3*nTris).
        /// </summary>
        public static int[] BuildGodotCwIndices(int[][] contractCcwTriangles)
        {
            int nTris = contractCcwTriangles.Length;
            int[] indices = new int[nTris * 3];
            for (int t = 0; t < nTris; t++)
            {
                int[] tri = contractCcwTriangles[t];
                indices[3 * t] = tri[0];
                indices[3 * t + 1] = tri[2];
                indices[3 * t + 2] = tri[1];
            }
            return indices;
        }
    }
}
