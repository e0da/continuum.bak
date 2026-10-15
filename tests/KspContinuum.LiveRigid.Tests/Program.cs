using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

static class Program
{
    const string StockSha="8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27",StockMvid="10657063-2fc3-43a7-84fa-d39e75e877bf";
    const string MechSha="6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929",MechMvid="30fbe3c4-e77e-4abc-895b-a635d5924a08";
    static void Need(bool ok,string why){if(!ok)throw new InvalidDataException("Live rigid receipt: "+why);}
    static double F(JsonElement e,string name){double x=e.GetProperty(name).GetSingle();Need(double.IsFinite(x),"nativefloat "+name);return x;}
    static double D(JsonElement e,string name){double x=e.GetProperty(name).GetDouble();Need(double.IsFinite(x),"double "+name);return x;}
    static int I(JsonElement e,string name)=>e.GetProperty(name).GetInt32();
    static bool B(JsonElement e,string name)=>e.GetProperty(name).GetBoolean();
    static double[] A(JsonElement e,string name,int count=3,bool native=true){var a=e.GetProperty(name).EnumerateArray().Select(x=>native?(double)x.GetSingle():x.GetDouble()).ToArray();Need(a.Length==count&&a.All(double.IsFinite),"array "+name);return a;}
    static JsonElement[] Rows(JsonElement e,string name,int bound){var a=e.GetProperty(name).EnumerateArray().ToArray();Need(a.Length<=bound,"bounded "+name);return a;}
    static double[] Add(double[] a,double[] b)=>a.Zip(b,(x,y)=>x+y).ToArray();
    static double[] Sub(double[] a,double[] b)=>a.Zip(b,(x,y)=>x-y).ToArray();
    static double[] Scale(double[] a,double n)=>a.Select(x=>x*n).ToArray();
    static double Dot(double[] a,double[] b)=>a.Zip(b,(x,y)=>x*y).Sum();
    static double Norm(double[] a)=>Math.Sqrt(Dot(a,a));
    static double[] Cross(double[] a,double[] b)=>new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
    static double[,] R(double[] q){double length=Norm(q);Need(Math.Abs(length*length-1)<=1e-4,"native unit quaternion");double x=q[0]/length,y=q[1]/length,z=q[2]/length,w=q[3]/length;return new[,]{{1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)},{2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)},{2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)}};}
    static double[,] Mul(double[,] a,double[,] b){var r=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)r[i,j]+=a[i,k]*b[k,j];return r;}
    static double[] Mul(double[,] a,double[] b)=>Enumerable.Range(0,3).Select(i=>Enumerable.Range(0,3).Sum(j=>a[i,j]*b[j])).ToArray();
    static double[,] Tensor(JsonElement state){var r=Mul(R(A(state,"rotation",4)),R(A(state,"inertiaRotation",4)));var p=A(state,"inertia");Need(p.All(x=>x>0),"positive native principal inertia");var t=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)t[i,j]+=r[i,k]*p[k]*r[j,k];return t;}
    static double[,] InverseTensor(JsonElement state){var r=Mul(R(A(state,"rotation",4)),R(A(state,"inertiaRotation",4)));var p=A(state,"inertia");Need(p.All(x=>x>0),"positive native principal inertia");var t=new double[3,3];for(int i=0;i<3;i++)for(int j=0;j<3;j++)for(int k=0;k<3;k++)t[i,j]+=r[i,k]/p[k]*r[j,k];return t;}
    static void State(JsonElement s,int id){Need(I(s,"id")==id&&id!=0&&F(s,"mass")>0,"native body identity/mass");A(s,"position");A(s,"center");A(s,"velocity");A(s,"angularVelocity");Tensor(s);}
    static double Error(double[] actual,double[] expected,double relative,double absolute,string why){double error=Norm(Sub(actual,expected));Need(double.IsFinite(error)&&error<=absolute+relative*Norm(expected),why+" error="+error.ToString("R",System.Globalization.CultureInfo.InvariantCulture));return error;}
    static void Wrench(JsonElement step,out double[] force,out double[] torque,out double[] impulse,out double[] angularImpulse,out int offcenter,HashSet<int> sourceIds)
    {
        force=new double[3];torque=new double[3];impulse=new double[3];angularImpulse=new double[3];offcenter=0;
        var commands=Rows(step,"nonzeroCommands",16384);Need(I(step,"commands")==commands.Length,"raw command cardinality");var center=A(step.GetProperty("before"),"center");
        foreach(var c in commands)
        {
            int id=I(c,"sourceId");Need(id!=0,"raw command source ID");sourceIds.Add(id);var source=c.GetProperty("source");State(source,id);var value=A(c,"value");var point=A(c,"point");Need(value.Any(x=>x!=0),"nonzero tape contains zero command");bool angular=B(c,"torque"),atPoint=B(c,"atPoint");Need(!angular||!atPoint,"torque at-point contradiction");int mode=I(c,"mode");Need(mode==0||mode==1||mode==2||mode==5,"force mode");
            if(mode==2||mode==5)value=angular?Mul(Tensor(source),value):Scale(value,F(source,"mass"));
            var moment=angular?value:Cross(Sub(point,center),value);
            if(!angular&&atPoint&&Norm(moment)>0)offcenter++;
            if(!angular&&!atPoint)Error(point,A(source,"center"),0,0,"centre-applied command point");
            bool instantaneous=mode==1||mode==2;
            if(instantaneous){if(!angular)impulse=Add(impulse,value);angularImpulse=Add(angularImpulse,moment);}
            else{if(!angular)force=Add(force,value);torque=Add(torque,moment);}
        }
    }
    static void Bridge(JsonElement receipt)
    {
        var rows=Rows(receipt,"bridgeSites",8192);Need(rows.Length>0,"source bridge ledger");var identities=new HashSet<string>();int replaced=0;bool integrate=false,stats=false,track=false;
        foreach(var row in rows)
        {
            string assembly=row.GetProperty("assembly").GetString();Need(assembly=="Assembly-CSharp"||assembly=="MechJeb2","bridge assembly scope");Need(row.GetProperty("assemblySha256").GetString()==(assembly=="Assembly-CSharp"?StockSha:MechSha)&&row.GetProperty("assemblyMvid").GetString()==(assembly=="Assembly-CSharp"?StockMvid:MechMvid),"source assembly pin");
            string digest=row.GetProperty("ilSha256").GetString();Need(digest!=null&&digest.Length==64&&digest.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f'),"source IL digest");int token=I(row,"metadataToken");Need(token>0&&I(row,"instructionCount")>0&&identities.Add(assembly+"/"+token),"unique source method");Need(!string.IsNullOrEmpty(row.GetProperty("declaringType").GetString())&&!string.IsNullOrEmpty(row.GetProperty("method").GetString()),"method identity");
            int expected=I(row,"expectedReplacements");Need(expected>=0,"replacement count");var calls=Rows(row,"replacedCalls",50000);Need(calls.Length==expected&&calls.All(c=>!string.IsNullOrEmpty(c.GetString())&&c.GetString().Contains(" -> ")),"replacement call ledger");Rows(row,"unroutedCalls",50000);
            if(expected==0){Need(!B(row,"patched"),"unselected method patched");continue;}
            // Removing this transpiler does not invoke it; cleanup recomposition is optional.
            Need(B(row,"patched")&&B(row,"removed")&&I(row,"observedReplacements")==expected&&I(row,"shapeAdmissions")==1&&I(row,"cleanupShapeChecks")>=0&&I(row,"frameRecompositions")>=0,"exact installed/restored replacement shape");replaced+=expected;
            if(assembly=="Assembly-CSharp"&&token==100677461){Need(row.GetProperty("declaringType").GetString()=="FlightIntegrator"&&row.GetProperty("method").GetString()=="Integrate"&&expected==5&&calls.Count(c=>c.GetString().EndsWith(" -> IntegrationAcceleration",StringComparison.Ordinal))==2,"pinned uniform acceleration seam");integrate=true;}
            if(assembly=="Assembly-CSharp"&&token==100678128)stats=true;
            if(assembly=="Assembly-CSharp"&&token==100670206)track=true;
        }
        Need(replaced>0&&integrate&&stats&&track,"required consumer/force seams reached in bridge admission");
    }
    static void Verify(JsonElement receipt)
    {
        string schema=receipt.GetProperty("schema").GetString();bool controlled=schema=="continuum-live-rigid-owner/v2";
        Need((controlled||schema=="continuum-live-rigid-owner/v1")&&receipt.GetProperty("status").GetString()=="passed"&&B(receipt,"passed"),"completed live receipt");Need(!string.IsNullOrEmpty(receipt.GetProperty("runId").GetString()),"run identity");
        if(controlled)Need(receipt.GetProperty("scenario").GetString()=="control-warp","controlled scenario identity");
        Need(I(receipt,"sourceParts")==512&&I(receipt,"sourceBodies")>0&&I(receipt,"sourceBodies")<=2048,"frozen real craft domain");int bodies=I(receipt,"sourceBodies"),owner=I(receipt,"aggregateId");Need(owner!=0,"aggregate identity");
        if(controlled && receipt.TryGetProperty("poseMaterializations",out var materializations))
        {
            int writes=materializations.GetInt32(), reuses=I(receipt,"poseReuses"), requests=I(receipt,"publishCalls");
            bool presentation=receipt.TryGetProperty("forestMode",out var mode)&&mode.GetString()=="owned-presentation-root";
            Need(writes>0&&reuses>0&&writes+reuses==requests&&receipt.GetProperty("poseGeneration").GetInt64()==writes,"retained pose request/materialization coverage");
            Need(I(receipt,"publishSyncCalls")==writes+(presentation?1:0)&&I(receipt,"publishedMembers")==checked((requests+(presentation?1:0))*bodies),"every retained request preserves full proxy readback; presentation setup sync/readbacks charged");
            Need(I(receipt,"poseRefreshRequests")+I(receipt,"posePostPhysicsRequests")+I(receipt,"poseFrameRequests")+I(receipt,"poseAdmissionRequests")==requests,"publication cause coverage");
            Console.WriteLine("Retained publication: "+writes+" materializations, "+reuses+" target reuses; "+I(receipt,"publishedMembers")+" native/Transform member validations retained.");
        }
        bool principalFrame=controlled && receipt.TryGetProperty("aggregatePoseFrame",out var frameKind) && frameKind.GetString()=="native-com-principal";
        if(controlled && receipt.TryGetProperty("forestStatus",out var forest))
        {
            if(forest.GetString()=="admitted")VerifyForest(receipt,bodies);
            else Need(forest.GetString()=="eager-fallback"&&!string.IsNullOrEmpty(receipt.GetProperty("forestFallbackReason").GetString()),"forest rejection must remain explicit");
        }
        if(principalFrame)Need(D(receipt,"maxNativeFramePositionError")>=0&&D(receipt,"maxNativeFramePositionError")<=1e-4&&D(receipt,"maxNativeFrameRotationError")>=0&&D(receipt,"maxNativeFrameRotationError")<=.002,"native aggregate writer/frame guards");
        foreach(string flag in new[]{"originalBodiesRestored","aggregateRetired","bridgeReleased"})Need(B(receipt,flag),"closure "+flag);
        if(!controlled)Need(B(receipt,"pulseQueued"),"closure pulseQueued");
        Need(receipt.GetProperty("cleanupStatus").GetString()=="restored-original-ownership","ownership cleanup");foreach(string field in new[]{"velocityReads","angularReads","centerReads","pointReads"})Need(I(receipt,field)>0,"read coverage "+field);
        if(receipt.TryGetProperty("statsReads",out var statsReads))Need(statsReads.GetInt32()>0,"coherent aggregate statistics read coverage");
        Need(I(receipt,"duplicateAccelerationReceivers")==0&&D(receipt,"maxProxyPositionError")>=0&&D(receipt,"maxProxyPositionError")<=1e-4&&D(receipt,"maxProxyRotationError")>=0&&D(receipt,"maxProxyRotationError")<=.002,"proxy/common ownership errors");
        Bridge(receipt);State(receipt.GetProperty("initial"),owner);State(receipt.GetProperty("restoredAggregate"),owner);
        var steps=Rows(receipt,"steps",1024);Need(steps.Length>10&&I(receipt,"physicsSteps")==steps.Length,"meaningful completed native step coverage");var sourceIds=new HashSet<int>();int offcenter=0,commands=0,common=0;bool pulseSeen=false;double priorTime=-1;int priorFrame=-1;double maxLinear=0,maxAngular=0,maxAngularVelocity=0,maxReduction=0;
        for(int index=0;index<steps.Length;index++)
        {
            var step=steps[index];double time=F(step,"fixedTime"),dt=F(step,"dt");Need(I(step,"index")==index&&I(step,"frame")>=priorFrame&&time>priorTime&&dt>0&&dt<=1&&I(step,"dynamicProxies")==0,"matched ordered native steps");priorTime=time;priorFrame=I(step,"frame");
            Need(I(step,"commonReceivers")==bodies,"uniform acceleration reaches distinct source bodies once");common+=I(step,"commonReceivers");var before=step.GetProperty("before");var after=step.GetProperty("after");State(before,owner);State(after,owner);Need(F(before,"mass")==F(after,"mass"),"within-step mass stable");
            if(principalFrame)
            {
                var center=A(step,"centerInCraft");var principal=R(A(step,"principalRotation",4));
                foreach(var boundary in new[]{("Before",before),("After",after)})
                {
                    var root=A(step,"canonicalRootPosition"+boundary.Item1);var rotation=R(A(step,"canonicalRootRotation"+boundary.Item1,4));
                    Error(A(boundary.Item2,"position"),Add(root,Mul(rotation,center)),0,1e-4,"independent canonical-root/native-COM correspondence");
                    Error(A(boundary.Item2,"center"),A(boundary.Item2,"position"),0,1e-4,"native COM frame has zero local COM");
                    Error(A(boundary.Item2,"inertiaRotation",4),new[]{0d,0d,0d,1d},0,0,"native principal frame inertia rotation identity");
                    var expected=Mul(rotation,principal);var actual=R(A(boundary.Item2,"rotation",4));
                    double angleBudget=2*Math.Sin(.002*Math.PI/360);
                    for(int axis=0;axis<3;axis++)Error(Enumerable.Range(0,3).Select(row=>actual[row,axis]).ToArray(),Enumerable.Range(0,3).Select(row=>expected[row,axis]).ToArray(),0,angleBudget,"independent canonical-root/native-principal axes correspondence");
                }
            }
            Wrench(step,out var force,out var torque,out var impulse,out var angularImpulse,out int reached,sourceIds);offcenter+=reached;commands+=I(step,"commands");
            // The fixed diagnostic queues .25 m/s worth of force perpendicular to its lever.
            double pulseMagnitude=F(before,"mass")*.25/dt;
            foreach(var command in step.GetProperty("nonzeroCommands").EnumerateArray())if(I(command,"mode")==0&&!B(command,"torque")&&B(command,"atPoint"))
            {
                var value=A(command,"value");var lever=Sub(A(command,"point"),A(before,"center"));double length=Norm(value),arm=Norm(lever);
                if(Math.Abs(length-pulseMagnitude)<=1e-4+1e-5*pulseMagnitude&&arm>0&&length>0&&Math.Abs(Dot(lever,value))/(arm*length)<=1e-4&&Norm(Cross(lever,value))>0)pulseSeen=true;
            }
            foreach(var item in new[]{(Key:"force",Value:force),(Key:"torque",Value:torque),(Key:"impulse",Value:impulse),(Key:"angularImpulse",Value:angularImpulse)})maxReduction=Math.Max(maxReduction,Error(A(step,item.Key,3,false),item.Value,1e-10,1e-8,"independent raw reduction "+item.Key));
            double mass=F(before,"mass");var acceleration=A(step,"commonAcceleration");var expectedVelocityDelta=Add(Scale(Add(Scale(force,1/mass),acceleration),dt),Scale(impulse,1/mass));
            maxLinear=Math.Max(maxLinear,Error(Sub(A(after,"velocity"),A(before,"velocity")),expectedVelocityDelta,1e-4,1e-4,"native linear velocity delta"));
            var oldL=Mul(Tensor(before),A(before,"angularVelocity"));var newL=Mul(Tensor(after),A(after,"angularVelocity"));var expectedDelta=Add(Scale(torque,dt),angularImpulse);
            double angularResidual=Norm(Sub(Sub(newL,oldL),expectedDelta));double angularScale=Math.Max(Math.Max(Norm(oldL),Norm(newL)),Norm(expectedDelta));
            Need(double.IsFinite(angularResidual)&&angularResidual<=1e-4+.02*angularScale,"native angular momentum residual error="+angularResidual.ToString("R",System.Globalization.CultureInfo.InvariantCulture));maxAngular=Math.Max(maxAngular,angularResidual);
            var expectedAngularVelocityDelta=Mul(InverseTensor(before),expectedDelta);
            maxAngularVelocity=Math.Max(maxAngularVelocity,Error(Sub(A(after,"angularVelocity"),A(before,"angularVelocity")),expectedAngularVelocityDelta,1e-4,1e-5,"native angular velocity torque response"));
            Need(D(step,"proxyPositionError")>=0&&D(step,"proxyPositionError")<=1e-4&&D(step,"proxyRotationError")>=0&&D(step,"proxyRotationError")<=.002,"per-step proxy pose coverage");
        }
        Need(sourceIds.Count>0&&sourceIds.Count<=bodies&&offcenter>0&&(controlled||pulseSeen),"actual queued off-center command coverage");Need(I(receipt,"routedCommands")>=commands&&I(receipt,"accelerationCalls")==common&&I(receipt,"omittedZeroCommands")>=0,"raw/global command coverage");
        if(controlled)Controlled(receipt,steps);
        Need(I(receipt,"postRestorationNativeSteps")>=2,"original ownership resumes native steps");
        RestoreRows(receipt.GetProperty("restoredRows"),bodies,owner);
        var restoredIds=new HashSet<int>(receipt.GetProperty("restoredRows").EnumerateArray().Select(r=>I(r,"originalId")));Need(sourceIds.IsSubsetOf(restoredIds),"raw commands bound to restored original recipients");
        Console.WriteLine("Independent live rigid receipt passed: "+steps.Length+" native steps, "+bodies+" source bodies, "+commands+" nonzero commands, "+offcenter+" off-center moments. Max raw reduction / linear / angular momentum residual / angular velocity response errors: "+maxReduction.ToString("R")+" / "+maxLinear.ToString("R")+" / "+maxAngular.ToString("R")+" / "+maxAngularVelocity.ToString("R")+". Angular momentum residual uses the maximum before/after/applied momentum scale; torque response uses the angular velocity delta scale. Fixed-topology no-contact vacuum scope; no stock trajectory, exact free-rotation conservation, complete gameplay, contact or FPS qualification.");
    }
    static void RestoreRows(JsonElement element,int count,int aggregate)
    {
        var rows=element.EnumerateArray().ToArray();Need(rows.Length==count&&rows.Select(r=>I(r,"originalId")).All(id=>id!=0&&id!=aggregate)&&rows.Select(r=>I(r,"originalId")).Distinct().Count()==count,"restored constituent identity coverage");
        foreach(var row in rows)
        {
            Need(I(row,"originalId")==I(row,"readbackId")&&!B(row,"originalKinematic")&&B(row,"originalKinematic")==B(row,"readbackKinematic")&&B(row,"originalDetectCollisions")==B(row,"readbackDetectCollisions"),"original constituent flags restored");
            Error(A(row,"readbackVelocity"),A(row,"expectedVelocity"),0,1e-4,"restored native velocity assignment");Error(A(row,"readbackAngularVelocity"),A(row,"expectedAngularVelocity"),0,1e-4,"restored native angular assignment");
        }
    }
    static void Controlled(JsonElement receipt,JsonElement[] steps)
    {
        var driver=receipt.GetProperty("controlDriver");
        foreach(string flag in new[]{"passed","subscribersRestored","groupsRestored","warpRestored","controlsRestored","stockIdleAtAdmission"})Need(B(driver,flag),"control driver closure "+flag);
        Need(I(receipt,"propertyRebuilds")>0&&F(receipt.GetProperty("restoredAggregate"),"mass")<F(receipt.GetProperty("initial"),"mass"),"original propellant mass reaches rebuilt aggregate");
        Need(B(receipt,"stockContinuationVerified")&&Rows(receipt,"screenshots",4).Length==4,"stock continuation and four image artifacts");
        Need(D(receipt,"postRestorationSimulatedSeconds")>=1.9999,"two actual simulated seconds under restored stock ownership");
        double ownerStartUt=D(receipt,"ownerStartUt"),ownerEndUt=D(receipt,"ownerEndUt"),priorUt=ownerStartUt;
        Need(ownerEndUt>ownerStartUt,"ordered owner UT interval");
        foreach(var step in steps)
        {
            double ut=D(step,"universalTime");Need(ut>=priorUt&&ut<=ownerEndUt,"finite monotonic native step UT within owner interval");priorUt=ut;
        }
        var originalSubscribers=Rows(driver,"originalSubscribers",4096);var finalSubscribers=Rows(driver,"finalSubscribers",4096);
        Need(originalSubscribers.Select(s=>s.GetString()).SequenceEqual(finalSubscribers.Select(s=>s.GetString()))&&originalSubscribers.All(s=>!string.IsNullOrEmpty(s.GetString())),"exact input subscriber restoration");
        Need(string.IsNullOrEmpty(driver.GetProperty("failure").GetString()),"control driver failure");
        foreach(string field in new[]{"callbacks","attitudeCallbacks","translationCallbacks","warpCallbacks","reactionWheelModules","rcsModules","warpEntries","normalReturns"})Need(I(driver,field)>0,"control driver coverage "+field);
        var wheelModes=Rows(driver,"reactionWheelModes",4096);var wheelStates=Rows(driver,"reactionWheelStates",4096);var wheelEnabled=Rows(driver,"reactionWheelEnabled",4096);
        Need(wheelModes.Length==I(driver,"reactionWheelModules")&&wheelStates.Length==wheelModes.Length&&wheelEnabled.Length==wheelModes.Length,"reaction wheel inventory cardinality");
        int deliveredWheels=0;for(int i=0;i<wheelModes.Length;i++){int mode=wheelModes[i].GetInt32(),state=wheelStates[i].GetInt32();bool enabled=wheelEnabled[i].GetBoolean();Need(mode>=0&&mode<=2&&state>=0&&state<=2,"bounded wheel mode/state");if(mode==0&&state==0&&enabled)deliveredWheels++;}
        Need(deliveredWheels>0&&I(driver,"deliveredInputReactionWheels")==deliveredWheels,"active Normal reaction wheels receive delivered controls");
        Need(I(driver,"warpEntries")==1&&I(driver,"normalReturns")==1,"one declared physics warp roundtrip");
        Need(I(driver,"callbacks")>=I(driver,"attitudeCallbacks")&&I(driver,"callbacks")>=I(driver,"translationCallbacks")&&I(driver,"callbacks")>=I(driver,"warpCallbacks"),"control callback totals");
        B(driver,"originalSas");B(driver,"originalRcs");
        Need(F(driver,"originalWarpRate")==1&&I(driver,"originalWarpIndex")==0&&(driver.GetProperty("originalWarpMode").GetString()=="LOW"||driver.GetProperty("originalWarpMode").GetString()=="HIGH"),"original normal-time warp selection");
        Need(F(driver,"duration")>=12,"control driver simulated duration");
        double initialEc=D(driver,"initialElectricCharge"),finalEc=D(driver,"finalElectricCharge");
        Need(initialEc>=0&&finalEc>=0,"finite nonnegative net electric charge balances");
        double initialPropellant=D(driver,"initialMonoPropellant"),finalPropellant=D(driver,"finalMonoPropellant");
        Need(initialPropellant>0&&finalPropellant>=0&&finalPropellant<initialPropellant,"actual stock propellant consumption");
        ElectricDraws(driver);
        string[] phases={"quiet","attitude","translation","pre-warp","warp","normal-return"};
        double[][] controls={new double[6],new[]{.35,.15,-.2,0,0,0},new[]{0,0,0,.5,.2,-.3},new double[6],new[]{-.2,.1,0,-.3,0,.4},new double[6]};
        int[] counts=new int[phases.Length];int lastPhase=-1;double duration=0;bool attitudeTorque=false,translationForce=false,warpTorque=false,warpForce=false;int warpIndex=-1;
        foreach(var step in steps)
        {
            string phase=step.GetProperty("controlPhase").GetString();int slot=Array.IndexOf(phases,phase);
            Need(slot>=0&&slot>=lastPhase,"ordered control phase");lastPhase=slot;counts[slot]++;duration+=F(step,"dt");
            var actual=A(step,"controls",6);for(int i=0;i<actual.Length;i++)Need(Math.Abs(actual[i]-controls[slot][i])<=1e-7,"declared native controls "+phase+" component "+i);
            double rate=F(step,"warpRate");int index=I(step,"warpRateIndex");string mode=step.GetProperty("warpMode").GetString();
            Need(index>=0&&(mode=="LOW"||mode=="HIGH"),"actual warp selection");
            if(slot==4)
            {
                Need(rate==4&&mode=="LOW"&&index==3&&Math.Abs(F(step,"dt")-.08)<=1e-6,"actual LOW 4x native step");
                if(warpIndex<0)warpIndex=index;else Need(index==warpIndex,"stable 4x warp index");
            }
            else Need(rate==1&&index==0&&Math.Abs(F(step,"dt")-.02)<=1e-6,"normal-time native step outside warp phase");
            foreach(var command in step.GetProperty("nonzeroCommands").EnumerateArray())
            {
                bool torque=B(command,"torque");bool atPoint=B(command,"atPoint");var value=A(command,"value");
                if(slot==1&&torque&&I(command,"mode")==0&&Norm(value)>0)attitudeTorque=true;
                if(slot==2&&!torque&&atPoint&&I(command,"mode")==0&&Norm(Cross(Sub(A(command,"point"),A(step.GetProperty("before"),"center")),value))>0)translationForce=true;
                if(slot==4&&torque&&I(command,"mode")==0&&Norm(value)>0)warpTorque=true;
                if(slot==4&&!torque&&atPoint&&I(command,"mode")==0&&Norm(value)>0)warpForce=true;
            }
        }
        Need(counts.All(c=>c>0)&&duration>=11.99&&attitudeTorque&&translationForce&&warpTorque&&warpForce,"complete controlled native phases/stock commands/duration");
        Console.WriteLine("Controlled-flight evidence: "+duration.ToString("R")+" simulated seconds; phase native steps "+string.Join(",",phases.Zip(counts,(p,c)=>p+"="+c))+". Stock torque/RCS command and resource consumption witnessed; no human control-feel or fuel-flow fidelity claim.");
    }
    static void ElectricDraws(JsonElement driver)
    {
        Need(B(driver,"electricObserverReleased"),"electric withdrawal observer released");
        Need(I(driver,"electricMethodToken")==100673762&&driver.GetProperty("electricMethodMvid").GetString()==StockMvid&&driver.GetProperty("electricAssemblySha256").GetString()==StockSha,"pinned original electric resource method");
        string digest=driver.GetProperty("electricMethodIlSha256").GetString();
        Need(digest=="382f270bf72d7fa71254bb005ef6ea5c788ec51ddbc03a60cf2b335c5e0239c2","pinned original electric resource method IL identity");
        var rows=Rows(driver,"electricDraws",4096);Need(rows.Length>0&&rows.Length==I(driver,"electricResourceCalls"),"bounded gross electric draw tape");
        double requested=0,withdrawn=0,priorTime=-1;int positive=0,roundedOverdraw=0;bool attitude=false,warp=false;
        foreach(var row in rows)
        {
            double time=F(row,"fixedTime");Need(time>=priorTime&&I(row,"partId")!=0,"ordered original wheel resource call identity");priorTime=time;
            string phase=row.GetProperty("phase").GetString();Need(Array.IndexOf(new[]{"quiet","attitude","translation","pre-warp","warp","normal-return"},phase)>=0,"electric draw control phase");
            double request=D(row,"requested"),draw=D(row,"withdrawn");Need(request>=0&&draw>=0&&(request==0?draw==0:draw<=Math.BitIncrement(request)),"original electric withdrawal bounds (one binary64 successor)");
            if(draw>request)roundedOverdraw++;requested+=request;withdrawn+=draw;
            if(draw>0){positive++;if(phase=="attitude")attitude=true;if(phase=="warp")warp=true;}
        }
        Need(positive==I(driver,"electricPositiveCalls")&&withdrawn>0&&attitude&&warp,"actual gross wheel withdrawal in delivered attitude and warp phases");
        Need(Math.Abs(D(driver,"grossElectricChargeRequested")-requested)<=1e-8+1e-10*Math.Abs(requested)&&Math.Abs(D(driver,"grossElectricChargeWithdrawn")-withdrawn)<=1e-8+1e-10*Math.Abs(withdrawn),"independent gross electric withdrawal sum");
        Console.WriteLine("Original wheel electric withdrawals: "+rows.Length+" calls; "+roundedOverdraw+" one-ULP over-request readbacks; raw values retained.");
    }
    static void VerifyForest(JsonElement receipt,int bodyCount)
    {
        int[] Integers(string name,int bound){var rows=Rows(receipt,name,bound);return rows.Select(row=>row.GetInt32()).ToArray();}
        var bodies=Integers("forestBodyIds",2048);var transforms=Integers("forestBodyTransformIds",2048);var roots=Integers("forestRootBodyIds",2048);
        var nodes=Integers("forestTransformIds",32768);var parents=Integers("forestParentIds",32768);
        Need(bodies.Length==bodyCount&&transforms.Length==bodyCount&&roots.Length==bodyCount&&bodies.Distinct().Count()==bodyCount&&transforms.Distinct().Count()==bodyCount,"forest body/transform bijection");
        Need(nodes.Length>0&&nodes.Length==parents.Length&&nodes.Distinct().Count()==nodes.Length&&nodes.All(id=>id!=0)&&I(receipt,"forestEdges")==nodes.Length,"bounded closed forest node ledger");
        var restored=Rows(receipt,"restoredRows",2048).Select(row=>I(row,"originalId")).ToHashSet();Need(restored.SetEquals(bodies),"forest covers original restored bodies");
        var parentMap=nodes.Zip(parents,(id,parent)=>(id,parent)).ToDictionary(pair=>pair.id,pair=>pair.parent);
        Need(parents.All(id=>id==0||parentMap.ContainsKey(id)),"forest parent closure");
        var transformBodies=transforms.Zip(bodies,(transform,body)=>(transform,body)).ToDictionary(pair=>pair.transform,pair=>pair.body);
        var fixedPoses=Rows(receipt,"forestFixedLocalPose",32768).Select(row=>row.GetBoolean()).ToArray();Need(fixedPoses.Length==nodes.Length,"forest local lease cardinality");
        A(receipt,"forestLocalPositions",nodes.Length*3);var rotations=A(receipt,"forestLocalRotations",nodes.Length*4);var scales=A(receipt,"forestLocalScales",nodes.Length*3);
        if(receipt.GetProperty("forestMode").GetString()=="owned-presentation-root")
        {
            int pivot=I(receipt,"presentationPivotTransformId");Need(pivot!=0&&I(receipt,"presentationPivotId")!=0&&parentMap.ContainsKey(pivot)&&parentMap[pivot]==0&&!transformBodies.ContainsKey(pivot)&&nodes.Length==bodyCount+1,"separate unparented presentation root");
            var rootTransforms=Integers("forestRootTransformIds",2048);var originalParents=Integers("presentationOriginalParentIds",2048);var restoredParents=Integers("presentationRestoredParentIds",2048);
            var originalSiblings=Integers("presentationOriginalSiblingIndices",2048);var restoredSiblings=Integers("presentationRestoredSiblingIndices",2048);var restoredBodies=Integers("presentationRestoredBodyIds",2048);
            var originalScales=A(receipt,"presentationOriginalScales",bodyCount*3);var restoredScales=A(receipt,"presentationRestoredScales",bodyCount*3);
            Need(rootTransforms.Length==bodyCount&&rootTransforms.All(id=>id==pivot)&&roots.All(id=>id==0)&&originalParents.Length==bodyCount&&originalParents.All(id=>id==0)&&restoredParents.SequenceEqual(originalParents)&&restoredSiblings.SequenceEqual(originalSiblings)&&restoredBodies.SequenceEqual(bodies)&&restoredScales.SequenceEqual(originalScales),"presentation transaction restores original identity/parent/sibling/scale");
            Need(originalSiblings.Length==bodyCount&&originalSiblings.All(index=>index>=0)&&B(receipt,"presentationParentsRestored")&&B(receipt,"presentationPivotRetired"),"presentation captured sibling indices and retirement");
            for(int i=0;i<nodes.Length;i++){R(rotations.Skip(i*4).Take(4).ToArray());Need(fixedPoses[i]==(nodes[i]!=pivot),"presentation pivot/leaf pose ownership");if(nodes[i]==pivot)Need(scales.Skip(i*3).Take(3).All(scale=>scale==1),"unit presentation root");else Need(parentMap[nodes[i]]==pivot&&transformBodies.ContainsKey(nodes[i]),"direct presentation leaves");}
            for(int i=0;i<bodyCount;i++){int node=Array.IndexOf(nodes,transforms[i]);Need(node>=0&&scales.Skip(node*3).Take(3).SequenceEqual(originalScales.Skip(i*3).Take(3))&&originalScales.Skip(i*3).Take(3).All(scale=>scale>0),"fixed original leaf scale");}
            long pivotMaterializations=receipt.GetProperty("forestMaterializations").GetInt64();
            Need(I(receipt,"forestRoots")==1&&I(receipt,"forestDescendants")==bodyCount&&I(receipt,"forestInitialFullWrites")==bodyCount&&I(receipt,"poseMaterializations")==pivotMaterializations+1,"presentation materialization coverage");
            Need(receipt.GetProperty("presentationPivotWriteCalls").GetInt64()==pivotMaterializations+1&&receipt.GetProperty("forestWriteCalls").GetInt64()==bodyCount+pivotMaterializations+1&&receipt.GetProperty("forestReadbackChecks").GetInt64()==(long)I(receipt,"publishCalls")*bodyCount&&I(receipt,"presentationSetupWrites")==bodyCount,"presentation cold setup and steady writes/readbacks charged");
            Need(D(receipt,"maxPresentationWorldScaleRelativeError")>=0&&D(receipt,"maxPresentationWorldScaleRelativeError")<=1e-5&&receipt.GetProperty("presentationScaleReadbacks").GetInt64()==((long)I(receipt,"publishCalls")+1)*bodyCount,"world scale preservation and restoration coverage");
            Console.WriteLine("Owned presentation: one root for "+bodyCount+" original bodies; "+receipt.GetProperty("forestWriteCalls").GetInt64()+" publication writes; all pose/scale readbacks retained.");return;
        }
        Need(scales.All(scale=>scale==1),"rigid forest unit scale");
        for(int i=0;i<nodes.Length;i++)
        {
            R(rotations.Skip(i*4).Take(4).ToArray());bool ownedAncestor=false;var seen=new HashSet<int>{nodes[i]};
            for(int cursor=parents[i];cursor!=0;cursor=parentMap[cursor]){Need(seen.Add(cursor),"acyclic transform forest");ownedAncestor|=transformBodies.ContainsKey(cursor);}
            Need(fixedPoses[i]==ownedAncestor,"descendant local pose lease follows actual ancestry");
        }
        for(int i=0;i<bodyCount;i++)
        {
            Need(parentMap.ContainsKey(transforms[i]),"body transform in ledger");int highest=bodies[i];
            for(int cursor=parentMap[transforms[i]];cursor!=0;cursor=parentMap[cursor])if(transformBodies.TryGetValue(cursor,out int ancestor))highest=ancestor;
            Need(roots[i]==highest,"publication root is highest owned transform ancestor");
        }
        int rootCount=roots.Distinct().Count();long materializations=receipt.GetProperty("forestMaterializations").GetInt64();
        Need(rootCount>0&&rootCount<bodyCount&&I(receipt,"forestRoots")==rootCount&&I(receipt,"forestDescendants")==bodyCount-rootCount,"positive forest compression coverage");
        Need(I(receipt,"forestInitialFullWrites")==bodyCount&&I(receipt,"poseMaterializations")==materializations+1,"first materialization eager; subsequent forest coverage");
        Need(receipt.GetProperty("forestWriteCalls").GetInt64()==bodyCount+materializations*rootCount&&receipt.GetProperty("forestReadbackChecks").GetInt64()==((long)I(receipt,"publishCalls")-1)*bodyCount&&receipt.GetProperty("forestInvariantChecks").GetInt64()>0,"forest work and full readback accounting");
        Console.WriteLine("Transform forest: "+rootCount+" publication roots for "+bodyCount+" bodies; "+receipt.GetProperty("forestWriteCalls").GetInt64()+" writes, all original pose readbacks retained.");
    }
    public static int Main(string[] args)
    {
        try{Need(args.Length==1,"usage: receipt.json");byte[] bytes=File.ReadAllBytes(args[0]);using var doc=JsonDocument.Parse(bytes);Verify(doc.RootElement);Need(bytes.SequenceEqual(File.ReadAllBytes(args[0])),"receipt changed during verification");Console.WriteLine("receiptSha256="+Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());return 0;}
        catch(Exception e){Console.Error.WriteLine(e.ToString());return 1;}
    }
}
