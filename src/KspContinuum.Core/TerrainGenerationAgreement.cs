using System;

namespace KspContinuum
{
    public static class TerrainGenerationAgreement
    {
        public static bool Matches(int[] owners, int[] currentOwners, string[] geometry, string[] currentGeometry, float[] matrices, float[] currentMatrices)
        {
            if (owners == null || currentOwners == null || geometry == null || currentGeometry == null || matrices == null || currentMatrices == null ||
                owners.Length == 0 || owners.Length != currentOwners.Length || geometry.Length != currentGeometry.Length || geometry.Length == 0 || matrices.Length != currentMatrices.Length || matrices.Length == 0) return false;
            for (int i = 0; i < owners.Length; i++) if (owners[i] == 0 || owners[i] != currentOwners[i]) return false;
            for (int i = 0; i < geometry.Length; i++)
            {
                string digest = geometry[i]; if (digest == null || digest.Length != 64 || digest != currentGeometry[i]) return false;
                foreach (char c in digest) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            }
            for (int i = 0; i < matrices.Length; i++) if (float.IsNaN(matrices[i]) || float.IsInfinity(matrices[i]) || BitConverter.ToInt32(BitConverter.GetBytes(matrices[i]), 0) != BitConverter.ToInt32(BitConverter.GetBytes(currentMatrices[i]), 0)) return false;
            return true;
        }
    }
}
