using System.Runtime.InteropServices;

namespace KspContinuum
{
    public static class NativeForceZeroPredicate
    {
        [StructLayout(LayoutKind.Explicit)] struct FloatBits
        { [FieldOffset(0)] public float value; [FieldOffset(0)] public uint bits; }
        public static bool IsZero(float x,float y,float z)
        { return IsZeroBits(new FloatBits {value=x}.bits,new FloatBits {value=y}.bits,new FloatBits {value=z}.bits); }
        public static bool IsZeroBits(uint x,uint y,uint z)
        { return ((x|y|z)&0x7fffffffu)==0; }
    }
}
