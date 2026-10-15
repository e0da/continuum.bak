using System;
using KspContinuum;

class Program
{
    const string Epoch="original-providers/1";
    static readonly string Digest=new string('a',64);
    static void Require(bool value,string message) {if(!value)throw new Exception(message);}
    static void Fill(OrderedBodyAeroRow row)
    {
        row.ordinal=0;row.partId=7;row.recipientBodyId=-10;row.providerEpoch=Epoch;row.ownBody=true;row.applyDrag=true;row.angularDragByIntegrator=true;
        row.servoBody=false;row.shielded=false;row.submergedPortion=0;row.applyParentDrag=false;row.accelerationMode=false;
        row.atmosphericDensity=2;row.velocity=new BodyAeroVector(10,0,0);row.angularDrag=1;row.angularDragMultiplier=1;row.dragMultiplier=1;
        row.originalDragValue=3;row.pseudoReMultiplier=1;row.bodyLiftEligible=true;row.liftForceEligible=true;row.liftGeometryCaptured=true;
        row.bodyLiftMultiplier=2;row.globalBodyLiftMultiplier=1;row.liftMachSample=1;row.rotatedScaledLift=new BodyAeroVector(2,4,0);
        row.dragApplicationPoint=new BodyAeroVector(1,2,3);row.liftApplicationPoint=new BodyAeroVector(4,5,6);
    }
    static RetainedOrderedBodyAeroResult Original()
    {
        // Independent axis case: rho*v^2/2000=.1, drag=.3; projection removes X only.
        return new RetainedOrderedBodyAeroResult {ordinal=0,partId=7,speedSquared=100,speed=10,direction=new BodyAeroVector(1,0,0),dynamicPressure=.1,dragScalar=.3f,bodyLiftScalar=.2f,bodyLiftScalarWritten=true};
    }
    static BodyAeroCommand[] Tape()
    {
        return new[] {
            new BodyAeroCommand {ordinal=0,partId=7,recipientBodyId=-10,kind=BodyAeroCommandKind.AngularDrag},
            new BodyAeroCommand {ordinal=0,partId=7,recipientBodyId=-10,kind=BodyAeroCommandKind.AngularDrag,angularDrag=(float)(.1*.009869232667160128)},
            new BodyAeroCommand {ordinal=0,partId=7,recipientBodyId=-10,kind=BodyAeroCommandKind.DragAtPosition,force=new BodyAeroVector(-.3f,0,0),point=new BodyAeroVector(1,2,3)},
            new BodyAeroCommand {ordinal=0,partId=7,recipientBodyId=-10,kind=BodyAeroCommandKind.LiftAtPosition,force=new BodyAeroVector(0,4,0),point=new BodyAeroVector(4,5,6)} };
    }
    static bool ResultUnavailable(RetainedOrderedBodyAeroBuffer buffer)
    {try {buffer.ResultAt(0);return false;}catch(InvalidOperationException) {return true;}}
    static void Main()
    {
        var buffer=new RetainedOrderedBodyAeroBuffer(1);buffer.Begin("analytic","vessel",Digest,Epoch,0);Fill(buffer.InputAt(0));string reason;var actual=Tape();var original=Original();
        Require(buffer.TryEvaluate(0,out reason)&&buffer.Matches(0,original,actual,0,actual.Length,out reason),"Independent retained axis oracle failed: "+reason);
        Require(buffer.CommandAt(0,2).point.x==1&&buffer.ResultAt(0).commandCount==4,"Retained command span differs.");

        // Same provider identity is insufficient for a new invocation: old row data must be refreshed.
        buffer.Begin("analytic","vessel",Digest,Epoch,1);
        Require(!buffer.TryEvaluate(0,out reason)&&reason=="retained-row-epoch-owner"&&ResultUnavailable(buffer),"Unrefreshed row leaked across invocation with unchanged provider epoch.");
        Fill(buffer.InputAt(0));Require(buffer.TryEvaluate(0,out reason),"Refreshed row rejected.");buffer.InputAt(0).velocity=new BodyAeroVector(0,0,0);
        Require(!buffer.TryEvaluate(0,out reason)&&ResultUnavailable(buffer),"Failed compute retained a stale eligible result.");
        bool staleRejected=false;try {buffer.Begin("analytic","vessel",Digest,Epoch,1);}catch(ArgumentException) {staleRejected=true;}
        Require(staleRejected,"Repeated invocation admitted.");

        Fill(buffer.InputAt(0));Require(buffer.TryEvaluate(0,out reason),"Ownership case unavailable.");
        actual[2].recipientBodyId=-11;Require(!buffer.Matches(0,original,actual,0,4,out reason),"Wrong native body admitted.");actual=Tape();actual[2].forceMode=5;
        Require(!buffer.Matches(0,original,actual,0,4,out reason),"Acceleration/force mode confusion admitted.");actual=Tape();actual[2].point.x+=.00001f;
        Require(!buffer.Matches(0,original,actual,0,4,out reason),"Approximate application point admitted.");actual=Tape();var swap=actual[2];actual[2]=actual[3];actual[3]=swap;
        Require(!buffer.Matches(0,original,actual,0,4,out reason),"Ordered force publication interchanged.");

        Fill(buffer.InputAt(0));buffer.InputAt(0).originalDragValue=double.NaN;buffer.InputAt(0).bodyLiftEligible=false;
        Require(buffer.TryEvaluate(0,out reason)&&buffer.ResultAt(0).dragScalar==0&&buffer.ResultAt(0).commandCount==2&&!buffer.ResultAt(0).bodyLiftScalarWritten,"Original NaN drag suppression/unwritten lift lost.");
        buffer.InputAt(0).submergedPortion=.1;Require(!buffer.TryEvaluate(0,out reason)&&ResultUnavailable(buffer),"Water path produced retained result.");

        // Charge Begin, input refresh, computation and numeric/order comparison, after one cold admission.
        actual=Tape();Fill(buffer.InputAt(0));Require(buffer.TryEvaluate(0,out reason)&&buffer.Matches(0,original,actual,0,4,out reason),"Allocation witness cold admission failed.");
        long before=GC.GetAllocatedBytesForCurrentThread();bool passed=true;
        for(int i=2;i<258;i++) {buffer.Begin("analytic","vessel",Digest,Epoch,i);Fill(buffer.InputAt(0));passed&=buffer.TryEvaluate(0,out reason)&&buffer.Matches(0,original,actual,0,4,out reason);}
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Require(passed&&allocated==0,"Supported retained invocation/row path allocated "+allocated+" bytes or failed.");
        Console.WriteLine("Retained ordered dry-tail independent oracle, epoch, failed-result, native operand and allocation checks passed.");
    }
}
