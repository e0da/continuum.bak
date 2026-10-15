using System;
using System.Runtime.InteropServices;
using KspContinuum;

class Program
{
    [StructLayout(LayoutKind.Explicit)] struct Number {[FieldOffset(0)]public uint bits;[FieldOffset(0)]public float value;}
    static float F(uint bits) {return new Number {bits=bits}.value;}
    static void Require(bool value,string reason) {if(!value)throw new Exception(reason);}
    static void Main()
    {
        uint[] zeros={0,0x80000000};foreach(uint x in zeros)foreach(uint y in zeros)foreach(uint z in zeros)
            Require(NativeForceZeroPredicate.IsZeroBits(x,y,z)&&NativeForceZeroPredicate.IsZero(F(x),F(y),F(z)),"Exact signed-zero triple rejected.");
        uint[] nonzero={1,0x80000001,0x007fffff,0x00800000,0x3f800000,0xbf800000,0x7f800000,0xff800000,0x7fc00000,0x7f800001,0xff800001};
        foreach(uint n in nonzero) {Require(!NativeForceZeroPredicate.IsZeroBits(n,0,0)&&!NativeForceZeroPredicate.IsZeroBits(0,n,0)&&!NativeForceZeroPredicate.IsZeroBits(0,0,n),"Nonzero/poison component omitted.");Require(!NativeForceZeroPredicate.IsZero(F(n),0,0)&&!NativeForceZeroPredicate.IsZero(0,F(n),0)&&!NativeForceZeroPredicate.IsZero(0,0,F(n)),"Float path flushed/approximated a nonzero/poison component.");}
        Require(!NativeForceZeroPredicate.IsZero(float.Epsilon,0,0),"Underflowing squared magnitude treated as zero.");
        bool passed=true;long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<256;i++)passed&=NativeForceZeroPredicate.IsZero(0,F(0x80000000),0)&&!NativeForceZeroPredicate.IsZero(F(1),0,0);long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Require(passed&&bytes==0,"Exact predicate allocated or changed classification.");
        Console.WriteLine("Exact zero/signed-zero, subnormal, nonfinite and allocation falsifiers passed; native body semantics remain a separate gate.");
    }
}
