using System;
using System.Diagnostics;
using System.Security.Cryptography;

namespace KspContinuum.CapacityLab
{
    public static class CapacityBroadphase
    {
        public readonly struct Aabb
        {
            public readonly double MinX,MinY,MinZ,MaxX,MaxY,MaxZ;
            public Aabb(double minX,double minY,double minZ,double maxX,double maxY,double maxZ)
            {MinX=minX;MinY=minY;MinZ=minZ;MaxX=maxX;MaxY=maxY;MaxZ=maxZ;}
        }
        public readonly struct Pair : IComparable<Pair>
        {
            public readonly int A,B;
            public Pair(int a,int b){A=a;B=b;}
            public int CompareTo(Pair other){int x=A.CompareTo(other.A);return x!=0?x:B.CompareTo(other.B);}
        }
        public sealed class Limits
        {
            public readonly double CellSize;
            public readonly int MaximumPartsPerCraft,MaximumCellsPerBox,MaximumEntries;
            public readonly long MaximumBucketVisits,MaximumPairTests,MaximumCandidates,MaximumMemoryBytes,MaximumQueriedCells;
            public Limits(double cellSize=1.5,int maximumPartsPerCraft=1048576,int maximumCellsPerBox=4096,int maximumEntries=16777216,long maximumBucketVisits=100000000,long maximumPairTests=10000000,long maximumCandidates=2000000,long maximumMemoryBytes=536870912,long maximumQueriedCells=100000000)
            {
                if(!Finite(cellSize)||cellSize<=0||maximumPartsPerCraft<1||maximumPartsPerCraft>1048576||maximumCellsPerBox<1||maximumEntries<1||maximumBucketVisits<1||maximumPairTests<1||maximumCandidates<1||maximumCandidates>int.MaxValue||maximumMemoryBytes<1||maximumQueriedCells<1)throw new ArgumentException("Spatial limits are invalid.");
                CellSize=cellSize;MaximumPartsPerCraft=maximumPartsPerCraft;MaximumCellsPerBox=maximumCellsPerBox;MaximumEntries=maximumEntries;MaximumBucketVisits=maximumBucketVisits;MaximumPairTests=maximumPairTests;MaximumCandidates=maximumCandidates;MaximumMemoryBytes=maximumMemoryBytes;MaximumQueriedCells=maximumQueriedCells;
            }
        }
        public sealed class Result
        {
            public string Schema {get;}="continuum-capacity-aabb-candidates/v1";
            public string Status {get;} public string FailureStage {get;} public string Reason {get;}
            public string Scope {get;}="Inclusive primitive cross-vessel AABB candidate intersections; no contact, narrowphase, impulse or collision response.";
            public string Ordering {get;}="A ordinal, queried cell lexicographic XYZ, B ordinal; canonical minimum shared cell emits each pair once. Digest is this deterministic discovery stream, not a sorted-set hash.";
            public string PairStreamSha256 {get;} public string CaseKind {get;}
            public int PartsA {get;} public int PartsB {get;} public int Entries {get;}
            public long QueriedCells {get;} public long BucketVisits {get;} public long DuplicateCellVisits {get;} public long PairTests {get;} public long CandidateCount {get;}
            public long GenerationTicks {get;} public long BuildTicks {get;} public long QueryTicks {get;} public long OutputTicks {get;} public long ClockFrequency {get;}=Stopwatch.Frequency;
            public long PlannedMemoryBytes {get;} public long AllocatedBytes {get;}
            public bool Complete {get;} public bool RetainedPairs {get;}
            readonly Pair[] pairs;
            public Pair GetPair(int index){if(pairs==null||index<0||index>=pairs.Length)throw new ArgumentOutOfRangeException(nameof(index));return pairs[index];}
            internal Result(Observation o,Pair[] retained,long allocated)
            {Status=o.Status;FailureStage=o.Stage;Reason=o.Reason;PairStreamSha256=o.Digest;CaseKind=o.Case;PartsA=o.A;PartsB=o.B;Entries=o.Entries;QueriedCells=o.Cells;BucketVisits=o.Visits;DuplicateCellVisits=o.Duplicates;PairTests=o.Tests;CandidateCount=o.Candidates;GenerationTicks=o.Generation;BuildTicks=o.Build;QueryTicks=o.Query;OutputTicks=o.Output;PlannedMemoryBytes=o.Memory;AllocatedBytes=allocated;Complete=o.Status=="passed";RetainedPairs=retained!=null;pairs=retained;}
        }
        internal sealed class Observation
        {internal string Status="failed",Stage="admission",Reason="",Digest="",Case="external";internal int A,B,Entries;internal long Cells,Visits,Duplicates,Tests,Candidates,Generation,Build,Query,Output,Memory;}
        sealed class Refusal : Exception {internal readonly string Status;internal Refusal(string reason,string status="budget-exhausted"):base(reason){Status=status;}}
        readonly struct Cell : IComparable<Cell>
        {
            internal readonly long X,Y,Z;
            internal Cell(long x,long y,long z){X=x;Y=y;Z=z;}
            public int CompareTo(Cell c){int n=X.CompareTo(c.X);if(n==0)n=Y.CompareTo(c.Y);return n==0?Z.CompareTo(c.Z):n;}
        }
        readonly struct Entry : IComparable<Entry>
        {
            internal readonly Cell Cell;internal readonly int Part;
            internal Entry(Cell cell,int part){Cell=cell;Part=part;}
            public int CompareTo(Entry e){int n=Cell.CompareTo(e.Cell);return n==0?Part.CompareTo(e.Part):n;}
        }
        readonly struct Range
        {
            internal readonly Cell Min,Max;internal readonly int Count;
            internal Range(Cell min,Cell max,int count){Min=min;Max=max;Count=count;}
        }
        static bool Finite(double x){return !double.IsNaN(x)&&!double.IsInfinity(x);}
        static long Coordinate(double x,double size)
        {double n=Math.Floor(x/size);if(!Finite(n)||n< -4503599627370496d||n>4503599627370496d)throw new Refusal("Cell coordinate exceeds exact integer domain.","unsupported");return (long)n;}
        static Range Cells(Aabb b,Limits l)
        {
            if(!Finite(b.MinX)||!Finite(b.MinY)||!Finite(b.MinZ)||!Finite(b.MaxX)||!Finite(b.MaxY)||!Finite(b.MaxZ)||b.MinX>b.MaxX||b.MinY>b.MaxY||b.MinZ>b.MaxZ)throw new Refusal("Nonfinite/inverted AABB.","invalid");
            var min=new Cell(Coordinate(b.MinX,l.CellSize),Coordinate(b.MinY,l.CellSize),Coordinate(b.MinZ,l.CellSize));var max=new Cell(Coordinate(b.MaxX,l.CellSize),Coordinate(b.MaxY,l.CellSize),Coordinate(b.MaxZ,l.CellSize));
            long x=max.X-min.X+1,y=max.Y-min.Y+1,z=max.Z-min.Z+1;
            if(x>l.MaximumCellsPerBox||y>l.MaximumCellsPerBox/x||z>l.MaximumCellsPerBox/(x*y))throw new Refusal("AABB cell coverage exceeds per-box budget.");return new Range(min,max,(int)(x*y*z));
        }
        static bool Intersects(Aabb a,Aabb b){return a.MinX<=b.MaxX&&a.MaxX>=b.MinX&&a.MinY<=b.MaxY&&a.MaxY>=b.MinY&&a.MinZ<=b.MaxZ&&a.MaxZ>=b.MinZ;}
        static int Lower(Entry[] entries,Cell cell)
        {int lo=0,hi=entries.Length;while(lo<hi){int m=lo+(hi-lo)/2;if(entries[m].Cell.CompareTo(cell)<0)lo=m+1;else hi=m;}return lo;}
        static void BudgetMemory(Observation o,Limits l,long bytes){o.Memory=bytes;if(bytes>l.MaximumMemoryBytes)throw new Refusal("Planned numeric storage exceeds memory budget.");}
        public static Aabb[] GenerateGrid(int count,double offsetX=0,bool collapsed=false)
        {
            if(count<1||count>1048576||!Finite(offsetX))throw new ArgumentOutOfRangeException(nameof(count));int width=1;while((long)width*width*width<count)width++;
            var boxes=new Aabb[count];for(int i=0;i<count;i++){double x=.75+offsetX+(collapsed?0:(i%width)*1.5),y=.75+(collapsed?0:((i/width)%width)*1.5),z=.75+(collapsed?0:(i/(width*width))*1.5);boxes[i]=new Aabb(x-.5,y-.5,z-.5,x+.5,y+.5,z+.5);}return boxes;
        }
        public static Result Run(int partsPerCraft,string caseKind,Limits limits=null,bool retainPairs=false)
        {
            limits=limits??new Limits();long alloc=GC.GetAllocatedBytesForCurrentThread(),begin=Stopwatch.GetTimestamp();var o=new Observation{Case=caseKind??"",A=partsPerCraft,B=partsPerCraft,Stage="generation"};
            try
            {
                if(partsPerCraft<1||partsPerCraft>limits.MaximumPartsPerCraft)throw new Refusal("Generated rows exceed part budget.");
                if(caseKind!="separated"&&caseKind!="sparse-contact"&&caseKind!="dense-overlap")throw new Refusal("Unknown generated spatial case.","invalid");
                BudgetMemory(o,limits,checked((long)partsPerCraft*96));int width=1;while((long)width*width*width<partsPerCraft)width++;
                bool dense=caseKind=="dense-overlap";double shift=caseKind=="separated"?(width*1.5+2):caseKind=="sparse-contact"?((width-1)*1.5+1):0;
                var a=GenerateGrid(partsPerCraft,0,dense);var b=GenerateGrid(partsPerCraft,shift,dense);o.Generation=Stopwatch.GetTimestamp()-begin;
                return Execute(a,b,limits,retainPairs,o,alloc,false);
            }
            catch(Refusal e){o.Status=e.Status;o.Reason=e.Message;o.Generation=Stopwatch.GetTimestamp()-begin;return new Result(o,null,GC.GetAllocatedBytesForCurrentThread()-alloc);}
        }
        public static Result Find(Aabb[] craftA,Aabb[] craftB,Limits limits=null,bool retainPairs=false)
        {limits=limits??new Limits();var o=new Observation{A=craftA==null?0:craftA.Length,B=craftB==null?0:craftB.Length};return Execute(craftA,craftB,limits,retainPairs,o,GC.GetAllocatedBytesForCurrentThread(),true);}
        static Result Execute(Aabb[] inputA,Aabb[] inputB,Limits l,bool retain,Observation o,long alloc,bool copy)
        {
            Pair[] output=null;long stageBegin=Stopwatch.GetTimestamp();o.Stage="build";
            using(var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                try
                {
                    if(inputA==null||inputB==null||inputA.Length<1||inputB.Length<1||inputA.Length>l.MaximumPartsPerCraft||inputB.Length>l.MaximumPartsPerCraft)throw new Refusal("AABB row tables absent/exceed budget.","invalid");
                    long sourceBytes=checked(((long)inputA.Length+inputB.Length)*48),outputBytes=retain?checked(l.MaximumCandidates*16):0;
                    BudgetMemory(o,l,checked(sourceBytes*(copy?2:1)+(long)inputB.Length*24+outputBytes+65536));
                    var a=copy?(Aabb[])inputA.Clone():inputA;var b=copy?(Aabb[])inputB.Clone():inputB;var minima=new Cell[b.Length];long entries=0;
                    for(int i=0;i<a.Length;i++)Cells(a[i],l);
                    for(int i=0;i<b.Length;i++){var r=Cells(b[i],l);minima[i]=r.Min;entries+=r.Count;if(entries>l.MaximumEntries)throw new Refusal("Sparse cell entry budget exhausted.");}
                    BudgetMemory(o,l,checked(sourceBytes*(copy?2:1)+(long)b.Length*24+entries*32+outputBytes+65536));
                    var index=new Entry[(int)entries];int at=0;
                    for(int i=0;i<b.Length;i++){var r=Cells(b[i],l);for(long x=r.Min.X;x<=r.Max.X;x++)for(long y=r.Min.Y;y<=r.Max.Y;y++)for(long z=r.Min.Z;z<=r.Max.Z;z++)index[at++]=new Entry(new Cell(x,y,z),i);}
                    Array.Sort(index);o.Entries=index.Length;if(retain)output=new Pair[(int)l.MaximumCandidates];o.Build=Stopwatch.GetTimestamp()-stageBegin;o.Stage="query";stageBegin=Stopwatch.GetTimestamp();
                    Span<byte> pairBytes=stackalloc byte[8];
                    for(int i=0;i<a.Length;i++)
                    {
                        var range=Cells(a[i],l);
                        for(long x=range.Min.X;x<=range.Max.X;x++)for(long y=range.Min.Y;y<=range.Max.Y;y++)for(long z=range.Min.Z;z<=range.Max.Z;z++)
                        {
                            if(o.Cells>=l.MaximumQueriedCells)throw new Refusal("Queried cell work budget exhausted.");var cell=new Cell(x,y,z);o.Cells++;int first=Lower(index,cell);
                            for(int e=first;e<index.Length&&index[e].Cell.CompareTo(cell)==0;e++)
                            {
                                if(o.Visits>=l.MaximumBucketVisits)throw new Refusal("Bucket visit work budget exhausted.");o.Visits++;int j=index[e].Part;var min=minima[j];
                                if(x!=Math.Max(range.Min.X,min.X)||y!=Math.Max(range.Min.Y,min.Y)||z!=Math.Max(range.Min.Z,min.Z)){o.Duplicates++;continue;}
                                if(o.Tests>=l.MaximumPairTests)throw new Refusal("Exact pair test work budget exhausted.");o.Tests++;
                                if(!Intersects(a[i],b[j]))continue;
                                if(o.Candidates>=l.MaximumCandidates)throw new Refusal("Dense candidate output budget exhausted.");
                                long outputBegin=Stopwatch.GetTimestamp();if(retain)output[(int)o.Candidates]=new Pair(i,j);
                                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pairBytes,i);System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(pairBytes.Slice(4),j);digest.AppendData(pairBytes);o.Output+=Stopwatch.GetTimestamp()-outputBegin;o.Candidates++;
                            }
                        }
                    }
                    o.Query=Stopwatch.GetTimestamp()-stageBegin-o.Output;o.Status="passed";o.Stage="complete";
                }
                catch(Refusal e){o.Status=e.Status;o.Reason=e.Message;if(o.Stage=="query")o.Query=Stopwatch.GetTimestamp()-stageBegin-o.Output;else o.Build=Stopwatch.GetTimestamp()-stageBegin;}
                long finish=Stopwatch.GetTimestamp();o.Digest=Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
                if(output!=null){var exact=new Pair[(int)o.Candidates];Array.Copy(output,exact,exact.Length);output=exact;}o.Output+=Stopwatch.GetTimestamp()-finish;
                return new Result(o,output,GC.GetAllocatedBytesForCurrentThread()-alloc);
            }
        }
    }
}
