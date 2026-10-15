using System;
using KspContinuum;

static class Program
{
    static int assertions;
    static void Check(bool value, string reason) { assertions++; if (!value) throw new Exception(reason); }
    static bool Scalar(byte[] a, byte[] b, out int compared)
    {
        compared = 0;
        if (a == null || b == null) return a == null && b == null;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        { compared++; if (a[i] != b[i]) return false; }
        return true;
    }
    static void Equivalent(byte[] a, byte[] b)
    {
        int expectedCount, actualCount;
        bool expected = Scalar(a, b, out expectedCount);
        bool actual = HarmonyPatchStateCertificate.SameBytes(a, b, out actualCount);
        Check(actual == expected && actualCount == expectedCount, "Exact result and earliest-difference count differ from independent scalar oracle");
        Check(HarmonyPatchStateCertificate.SameBytes(a, b) == expected, "Existing bool helper differs from shared implementation");
    }
    static void Main()
    {
        Equivalent(null, null); Equivalent(null, new byte[0]); Equivalent(new byte[1], null);
        Equivalent(new byte[7], new byte[8]); Equivalent(new byte[9], new byte[8]);
        foreach (int length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 65, 257, 4099 })
        {
            var seal = new byte[length]; for (int i = 0; i < length; i++) seal[i] = (byte)((i * 73 + length) & 255);
            var current = (byte[])seal.Clone(); Equivalent(current, seal); Equivalent(current, current);
            // Every byte is a possible first difference, including word/block crossings and the long odd tail.
            for (int i = 0; i < length; i++)
            {
                current[i] ^= 0x80; Equivalent(current, seal);
                if (i + 1 < length) { current[length - 1] ^= 0x01; Equivalent(current, seal); current[length - 1] ^= 0x01; }
                current[i] ^= 0x80; Equivalent(current, seal);
            }
        }
        var large = new byte[967680]; var snapshot = (byte[])large.Clone();
        Equivalent(large, snapshot);
        foreach (int index in new[] { 0, 7, 8, 31, 32, 967671, 967672, 967679 })
        { large[index] = 1; Equivalent(large, snapshot); large[index] = 0; Equivalent(large, snapshot); }
        Console.WriteLine("PASS: " + assertions + " exact patch-byte assertions; compare-only, no Harmony patches or native-runtime qualification.");
    }
}
