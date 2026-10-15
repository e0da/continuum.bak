using System;
using System.Runtime.InteropServices;

namespace KspContinuum
{
    [Serializable] public struct RetainedOrderedBodyAeroResult
    {
        public int ordinal,commandOffset,commandCount;public uint partId;
        public float speedSquared,speed,dragScalar,bodyLiftScalar;
        public BodyAeroVector direction;public double dynamicPressure;public bool bodyLiftScalarWritten;
    }
    public sealed class RetainedOrderedBodyAeroBuffer
    {
        public const string Scope="Mutable invocation-owned input storage and dry-tail reference only; InputAt exposes writable rows, not an immutable publication token. Begin invalidates each row's provider epoch; the author must refresh inputs and explicitly rebind that epoch before evaluation.";
        readonly OrderedBodyAeroRow[] rows;readonly RetainedOrderedBodyAeroResult[] results;readonly BodyAeroCommand[] commands;readonly bool[] evaluated;
        public readonly int Capacity;
        public long Invocation {get;private set;}
        public string RunId {get;private set;}public string VesselId {get;private set;}public string TopologySha256 {get;private set;}public string ProviderEpoch {get;private set;}
        public RetainedOrderedBodyAeroBuffer(int capacity)
        {
            if(capacity<1||capacity>OrderedBodyAeroPacket.MaximumRows)throw new ArgumentOutOfRangeException("capacity");
            Capacity=capacity;rows=new OrderedBodyAeroRow[capacity];results=new RetainedOrderedBodyAeroResult[capacity];commands=new BodyAeroCommand[checked(capacity*4)];evaluated=new bool[capacity];
            for(int i=0;i<capacity;i++)rows[i]=new OrderedBodyAeroRow();Invocation=-1;
        }
        public void Begin(string runId,string vesselId,string topologySha256,string providerEpoch,long invocation)
        {
            if(string.IsNullOrEmpty(runId)||string.IsNullOrEmpty(vesselId)||string.IsNullOrEmpty(providerEpoch)||!Digest(topologySha256)||invocation<=Invocation)throw new ArgumentException("Retained invocation identity absent/stale.");
            RunId=runId;VesselId=vesselId;TopologySha256=topologySha256;ProviderEpoch=providerEpoch;Invocation=invocation;Array.Clear(evaluated,0,evaluated.Length);
            for(int i=0;i<rows.Length;i++)rows[i].providerEpoch=null;
        }
        public OrderedBodyAeroRow InputAt(int ordinal) {Index(ordinal);return rows[ordinal];}
        public bool TryEvaluate(int ordinal,out string reason)
        {
            Index(ordinal);evaluated[ordinal]=false;reason=null;var row=rows[ordinal];
            if(Invocation<0||row.ordinal!=ordinal||row.partId==0||row.recipientBodyId==0||row.providerEpoch!=ProviderEpoch) {reason="retained-row-epoch-owner";return false;}
            if(!OrderedBodyAeroKernel.Supported(row,out reason))return false;
            try {OrderedBodyAeroKernel.EvaluateInto(row,ref results[ordinal],commands,ordinal*4);evaluated[ordinal]=true;return true;}
            catch(ArgumentException e) {reason=e.Message;return false;}
        }
        public RetainedOrderedBodyAeroResult ResultAt(int ordinal) {Index(ordinal);if(!evaluated[ordinal])throw new InvalidOperationException("Retained row not evaluated in this epoch.");return results[ordinal];}
        public BodyAeroCommand CommandAt(int ordinal,int command)
        {var r=ResultAt(ordinal);if(command<0||command>=r.commandCount)throw new ArgumentOutOfRangeException("command");return commands[r.commandOffset+command];}
        public bool Matches(int ordinal,RetainedOrderedBodyAeroResult original,BodyAeroCommand[] actual,int offset,int count,out string reason)
        {
            reason=null;var r=ResultAt(ordinal);
            if(actual==null||offset<0||count<0||count>4||offset>actual.Length-count||r.ordinal!=original.ordinal||r.partId!=original.partId||r.bodyLiftScalarWritten!=original.bodyLiftScalarWritten||r.commandCount!=count) {reason="actual-row-mask-command-count";return false;}
            if(!Near(r.speedSquared,original.speedSquared)||!Near(r.speed,original.speed)||!Vector(r.direction,original.direction,false)||!Near(r.dynamicPressure,original.dynamicPressure)||!Near(r.dragScalar,original.dragScalar)||(r.bodyLiftScalarWritten&&!Near(r.bodyLiftScalar,original.bodyLiftScalar))) {reason="actual-scalar-result";return false;}
            for(int i=0;i<count;i++)
            {
                var a=commands[r.commandOffset+i];var b=actual[offset+i];
                if(a.ordinal!=b.ordinal||a.partId!=b.partId||a.recipientBodyId!=b.recipientBodyId||a.kind!=b.kind||a.forceMode!=b.forceMode) {reason="actual-command-order-recipient-mode";return false;}
                if(a.kind==BodyAeroCommandKind.AngularDrag?!Near(a.angularDrag,b.angularDrag):!Vector(a.force,b.force,false)||!Vector(a.point,b.point,true)) {reason="actual-command-operand";return false;}
            }
            return true;
        }
        public OrderedBodyAeroPacket FreezeInputs(int count)
        {
            if(count<1||count>Capacity)throw new ArgumentOutOfRangeException("count");var copy=new OrderedBodyAeroRow[count];for(int i=0;i<count;i++)copy[i]=rows[i].Copy();
            return new OrderedBodyAeroPacket {runId=RunId,vesselId=VesselId,topologySha256=TopologySha256,providerEpoch=ProviderEpoch,invocation=Invocation,rows=copy};
        }
        public OrderedBodyAeroResult FreezeResult(int ordinal)
        {
            var r=ResultAt(ordinal);var c=new BodyAeroCommand[r.commandCount];Array.Copy(commands,r.commandOffset,c,0,c.Length);
            return new OrderedBodyAeroResult {ordinal=r.ordinal,partId=r.partId,speedSquared=r.speedSquared,speed=r.speed,direction=r.direction,dynamicPressure=r.dynamicPressure,dragScalar=r.dragScalar,bodyLiftScalar=r.bodyLiftScalar,bodyLiftScalarWritten=r.bodyLiftScalarWritten,commands=c};
        }
        [StructLayout(LayoutKind.Explicit)]struct FloatBits {[FieldOffset(0)]public float value;[FieldOffset(0)]public int bits;}
        static bool Vector(BodyAeroVector a,BodyAeroVector b,bool exact)
        {if(!a.Finite||!b.Finite)return false;return exact?Bits(a.x)==Bits(b.x)&&Bits(a.y)==Bits(b.y)&&Bits(a.z)==Bits(b.z):Near(a.x,b.x)&&Near(a.y,b.y)&&Near(a.z,b.z);}
        static int Bits(float n) {return new FloatBits {value=n}.bits;}
        static bool Near(double a,double b) {return BodyAeroVector.Number(a)&&BodyAeroVector.Number(b)&&Math.Abs(a-b)<=Math.Min(OrderedBodyAeroGate.AbsoluteTolerance+OrderedBodyAeroGate.RelativeTolerance*Math.Abs(b),OrderedBodyAeroGate.RelativeTolerance*Math.Max(1,Math.Abs(b)));}
        static bool Digest(string s) {if(s==null||s.Length!=64)return false;foreach(char c in s)if(!(c>='0'&&c<='9'||c>='a'&&c<='f'))return false;return true;}
        void Index(int ordinal) {if(ordinal<0||ordinal>=Capacity)throw new ArgumentOutOfRangeException("ordinal");}
    }
}
