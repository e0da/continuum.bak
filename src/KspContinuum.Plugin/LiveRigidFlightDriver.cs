using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    public sealed class LiveRigidFlightDriver : IDisposable
    {
        readonly Vessel vessel;
        readonly float enteredTime;
        readonly int thread=Thread.CurrentThread.ManagedThreadId;
        readonly FlightInputCallback callback,originalFly,expectedFly,originalPre,originalAuto,originalPost;
        readonly TimeWarp warp;
        readonly TimeWarp.Modes originalMode;
        readonly MethodInfo setMode;
        readonly Part[] parts;
        readonly ModuleReactionWheel[] wheels;
        readonly ModuleResourceHandler[] wheelHandlers;
        readonly ModuleResource[][] electricInputs;
        readonly MethodInfo electricMethod;
        readonly Harmony electricHarmony;
        readonly string electricOwner="continuum.live-rigid-electric."+Guid.NewGuid().ToString("N");
        readonly List<ElectricDraw> electricDraws=new List<ElectricDraw>(4096);
        static LiveRigidFlightDriver electricActive;
        bool electricAttempted;
        readonly float[] last=new float[6];
        bool attached,groupsTouched,warpTouched,disposed,applied;
        string phase="quiet";
        public readonly Receipt Report=new Receipt();
        public string Phase {get{return phase;}}
        [Serializable] public sealed class Receipt
        {
            public bool passed,subscribersRestored,groupsRestored,warpRestored,controlsRestored,stockIdleAtAdmission;
            public int callbacks,attitudeCallbacks,translationCallbacks,warpCallbacks,reactionWheelModules,rcsModules,warpEntries,normalReturns;
            public int deliveredInputReactionWheels;
            public int[] reactionWheelModes,reactionWheelStates;
            public bool[] reactionWheelEnabled;
            public int electricResourceCalls,electricPositiveCalls,electricMethodToken;
            public double grossElectricChargeRequested,grossElectricChargeWithdrawn;
            public bool electricObserverReleased;
            public string electricMethodIlSha256,electricMethodMvid,electricAssemblySha256;
            public ElectricDraw[] electricDraws;
            public double initialElectricCharge,finalElectricCharge,initialMonoPropellant,finalMonoPropellant;
            public string failure,originalWarpMode;
            public float duration,originalWarpRate;
            public int originalWarpIndex;
            public bool originalSas,originalRcs;
            public string[] originalSubscribers,finalSubscribers;
        }
        [Serializable] public struct ElectricDraw
        {
            public float fixedTime;
            public string phase;
            public int partId;
            public double requested,withdrawn;
        }
        static string Hash(byte[] bytes) {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        static string FileHash(string path) {using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        static void Need(bool ok,string reason) {if(!ok)throw new InvalidOperationException(reason);}
        static bool Finite(float v) {return !float.IsNaN(v)&&!float.IsInfinity(v);}
        static bool Finite(double v) {return !double.IsNaN(v)&&!double.IsInfinity(v);}
        static bool IdleOther(FlightCtrlState s)
        {
            if(s==null||s.mainThrottle!=0||s.rollTrim!=0||s.yawTrim!=0||s.pitchTrim!=0||s.wheelSteer!=0||s.wheelSteerTrim!=0||s.wheelThrottle!=0||s.wheelThrottleTrim!=0||s.killRot||s.gearUp||s.gearDown||s.headlight)return false;
            if(s.custom_axes!=null){if(s.custom_axes.Length>256)return false;foreach(float a in s.custom_axes)if(a!=0)return false;}return true;
        }
        static bool Idle(FlightCtrlState s) {return IdleOther(s)&&s.pitch==0&&s.yaw==0&&s.roll==0&&s.X==0&&s.Y==0&&s.Z==0;}
        bool OurInput(FlightCtrlState s) {return applied&&IdleOther(s)&&s.pitch==last[0]&&s.yaw==last[1]&&s.roll==last[2]&&s.X==last[3]&&s.Y==last[4]&&s.Z==last[5];}
        static void Clear(FlightCtrlState s)
        {
            s.NeutralizeAll();if(s.custom_axes!=null){Need(s.custom_axes.Length<=256,"Custom axis bound changed.");Array.Clear(s.custom_axes,0,s.custom_axes.Length);}
        }
        static string[] Names(FlightInputCallback chain)
        {
            return chain==null?new string[0]:chain.GetInvocationList().Select(d=>d.Method.DeclaringType.FullName+"."+d.Method.Name+":"+d.Method.Module.ModuleVersionId.ToString("D")+":"+d.Method.MetadataToken+":target="+(d.Target==null?"static":d.Target.GetType().FullName)).ToArray();
        }
        static void AdmitSubscribers(FlightInputCallback chain)
        {
            if(chain==null)return;
            foreach(Delegate d in chain.GetInvocationList())
            {
                bool stock=d.Method.Module.ModuleVersionId==new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf");
                bool mech=d.Method.Module.ModuleVersionId==new Guid("30fbe3c4-e77e-4abc-895b-a635d5924a08")&&d.Method.DeclaringType.FullName=="MuMech.MechJebCore"&&d.Method.Name=="OnFlyByWire";
                Need(stock||mech,"Unsupported pilot input subscriber: "+d.Method);
            }
        }
        double Resource(string name)
        {
            double amount=0;foreach(Part p in parts){Need(p!=null,"Resource source part disappeared.");foreach(PartResource r in p.Resources)if(r.resourceName==name){Need(Finite(r.amount)&&r.amount>=0,"Invalid resource readback: "+name);amount+=r.amount;}}Need(Finite(amount),"Resource total overflow.");return amount;
        }
        public LiveRigidFlightDriver(Vessel vessel,float enteredTime)
        {
            Need(vessel!=null&&vessel.loaded&&!vessel.packed&&vessel.rootPart!=null&&vessel.ctrlState!=null&&Finite(enteredTime),"Controlled rigid driver requires a live vessel/time.");
            this.vessel=vessel;this.enteredTime=enteredTime;warp=TimeWarp.fetch;Need(warp!=null,"Stock TimeWarp owner missing.");
            Need(TimeWarp.CurrentRate==1&&Time.timeScale==1&&Time.fixedDeltaTime==.02f&&TimeWarp.CurrentRateIndex==0,"Controlled rigid entry requires stock normal time.");
            Report.originalSas=vessel.ActionGroups[KSPActionGroup.SAS];Report.originalRcs=vessel.ActionGroups[KSPActionGroup.RCS];originalMode=TimeWarp.WarpMode;
            Report.originalWarpMode=originalMode.ToString();Report.originalWarpIndex=TimeWarp.CurrentRateIndex;Report.originalWarpRate=TimeWarp.CurrentRate;
            Need(vessel.GroupOverride==0&&!vessel.Autopilot.Enabled&&!Report.originalSas,"Disable SAS/autopilot/group overrides before controlled admission.");
            Need(Idle(vessel.ctrlState),"Controlled rigid driver requires all analog inputs, trims, flags and custom axes idle.");Report.stockIdleAtAdmission=true;
            originalFly=vessel.OnFlyByWire;originalPre=vessel.OnPreAutopilotUpdate;originalAuto=vessel.OnAutopilotUpdate;originalPost=vessel.OnPostAutopilotUpdate;
            AdmitSubscribers(originalFly);AdmitSubscribers(originalPre);AdmitSubscribers(originalAuto);AdmitSubscribers(originalPost);Report.originalSubscribers=Names(originalFly);
            setMode=AccessTools.DeclaredMethod(typeof(TimeWarp),"setMode",new[]{typeof(TimeWarp.Modes)});
            Need(setMode!=null&&!setMode.IsStatic&&setMode.ReturnType==typeof(bool)&&setMode.GetMethodBody()!=null&&setMode.Module.ModuleVersionId==new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf")&&Harmony.GetPatchInfo(setMode)?.Owners.Any()!=true,"Stock warp mode method absent/patched.");
            Need(warp.physicsWarpRates!=null&&warp.physicsWarpRates.Length>3&&warp.physicsWarpRates[3]==4,"Stock LOW rate3 must be exactly4x.");
            parts=vessel.parts.ToArray();Need(parts.Length>0&&parts.Length<=50000,"Controlled rigid part bound differs.");
            foreach(Part p in parts)foreach(PartModule m in p.Modules){if(m is ModuleReactionWheel)Report.reactionWheelModules++;if(m is ModuleRCS)Report.rcsModules++;}
            Need(Report.reactionWheelModules>0&&Report.rcsModules>0,"Original reaction wheel and RCS modules required.");
            wheels=parts.SelectMany(p=>p.Modules.Cast<PartModule>()).OfType<ModuleReactionWheel>().ToArray();
            Report.reactionWheelModes=new int[wheels.Length];Report.reactionWheelStates=new int[wheels.Length];Report.reactionWheelEnabled=new bool[wheels.Length];
            for(int n=0;n<wheels.Length;n++)
            {
                ModuleReactionWheel wheel=wheels[n];Report.reactionWheelModes[n]=wheel.actuatorModeCycle;Report.reactionWheelStates[n]=(int)wheel.State;Report.reactionWheelEnabled[n]=wheel.moduleIsEnabled;
                if(wheel.moduleIsEnabled&&wheel.State==ModuleReactionWheel.WheelState.Active&&wheel.actuatorModeCycle==(int)VesselActuatorMode.Normal)Report.deliveredInputReactionWheels++;
            }
            Need(Report.deliveredInputReactionWheels>0,"An active enabled All-mode reaction wheel is required; Pilot-only wheels read global pilot input rather than the delivered controls.");
            Need(!CheatOptions.InfinitePropellant,"Gross wheel resource evidence requires infinite propellant disabled.");
            wheelHandlers=new ModuleResourceHandler[wheels.Length];electricInputs=new ModuleResource[wheels.Length][];
            int electricRows=0;
            for(int n=0;n<wheels.Length;n++)
            {
                ModuleResourceHandler handler=wheels[n].resHandler;
                Need(handler!=null&&handler.GetType()==typeof(ModuleResourceHandler)&&ReferenceEquals(handler.partModule,wheels[n])&&handler.inputResources!=null&&handler.inputResources.Count<=256,"Wheel resource handler identity/type/bound differs.");
                wheelHandlers[n]=handler;electricInputs[n]=handler.inputResources.Where(r=>r!=null&&r.name=="ElectricCharge").ToArray();electricRows+=electricInputs[n].Length;
                for(int earlier=0;earlier<n;earlier++)Need(!ReferenceEquals(handler,wheelHandlers[earlier]),"Wheel resource handler is shared.");
            }
            Need(electricRows>0&&electricRows<=4096,"Wheel ElectricCharge input inventory absent/excessive.");
            electricMethod=AccessTools.DeclaredMethod(typeof(ModuleResourceHandler),"UpdateModuleResourceInputs",new[]{typeof(string).MakeByRefType(),typeof(bool),typeof(double),typeof(double),typeof(bool),typeof(bool),typeof(bool)});
            Need(electricMethod!=null&&electricMethod.ReturnType==typeof(double)&&electricMethod.GetMethodBody()!=null&&electricMethod.Module.ModuleVersionId==new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf")&&Harmony.GetPatchInfo(electricMethod)?.Owners.Any()!=true,"Original wheel resource method absent/patched.");
            Report.electricMethodToken=electricMethod.MetadataToken;Report.electricMethodMvid=electricMethod.Module.ModuleVersionId.ToString("D");Report.electricMethodIlSha256=Hash(electricMethod.GetMethodBody().GetILAsByteArray());
            Need(Report.electricMethodToken==100673762&&Report.electricMethodIlSha256=="382f270bf72d7fa71254bb005ef6ea5c788ec51ddbc03a60cf2b335c5e0239c2","Original wheel resource IL shape differs.");
            Report.electricAssemblySha256=FileHash(electricMethod.Module.Assembly.Location);
            Need(Report.electricAssemblySha256=="8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27","Original wheel resource assembly differs.");
            electricHarmony=new Harmony(electricOwner);
            Report.initialElectricCharge=Resource("ElectricCharge");Report.initialMonoPropellant=Resource("MonoPropellant");
            Need(Report.initialElectricCharge>0&&Report.initialMonoPropellant>0,"Positive stock EC and MonoPropellant required.");
            callback=Control;expectedFly=(FlightInputCallback)Delegate.Combine(originalFly,callback);
            try
            {
                Need(electricActive==null,"Another controlled electric observer is active.");electricActive=this;electricAttempted=true;
                electricHarmony.Patch(electricMethod,postfix:new HarmonyMethod(typeof(LiveRigidFlightDriver),nameof(ElectricPostfix)));
                Need(ElectricHookInstalled(),"Owned electric observer installation differs.");
                groupsTouched=true;vessel.ActionGroups.SetGroup(KSPActionGroup.SAS,false);vessel.ActionGroups.SetGroup(KSPActionGroup.RCS,false);
                attached=true;vessel.OnFlyByWire+=callback;Audit();
            }
            catch {Dispose();throw;}
        }
        public static string PlannedPhase(float elapsed)
        {
            if(elapsed<1)return "quiet";if(elapsed<3)return "attitude";if(elapsed<5)return "translation";if(elapsed<6)return "pre-warp";if(elapsed<10)return "warp";return "normal-return";
        }
        float Elapsed() {float elapsed=Time.fixedTime-enteredTime;Need(Finite(elapsed)&&elapsed>=0,"Controlled rigid simulation clock regressed.");return elapsed;}
        void Fail(string why) {if(Report.failure==null)Report.failure=why;Report.passed=false;}
        bool ElectricHookInstalled()
        {
            Patches info=Harmony.GetPatchInfo(electricMethod);
            return info!=null&&info.Owners.Count()==1&&info.Owners.Contains(electricOwner)&&info.Prefixes.Count==0&&info.Transpilers.Count==0&&info.Finalizers.Count==0&&info.Postfixes.Count==1&&info.Postfixes[0].owner==electricOwner&&info.Postfixes[0].PatchMethod==AccessTools.DeclaredMethod(typeof(LiveRigidFlightDriver),nameof(ElectricPostfix));
        }
        static void ElectricPostfix(ModuleResourceHandler __instance,bool useFlowMode,double rateMultiplier,double threshold,bool returnOnFirstLack,bool average,bool stringOps)
        {
            LiveRigidFlightDriver owner=electricActive;if(owner==null)return;
            try {owner.ObserveElectric(__instance,useFlowMode,rateMultiplier,threshold,returnOnFirstLack,average,stringOps);}catch(Exception error){owner.Fail("Original electric withdrawal observer: "+error);}
        }
        void ObserveElectric(ModuleResourceHandler handler,bool useFlowMode,double rateMultiplier,double threshold,bool returnOnFirstLack,bool average,bool stringOps)
        {
            for(int n=0;n<wheelHandlers.Length;n++)if(ReferenceEquals(handler,wheelHandlers[n]))
            {
                Need(Thread.CurrentThread.ManagedThreadId==thread&&!CheatOptions.InfinitePropellant&&ReferenceEquals(handler.partModule,wheels[n]),"Electric withdrawal owner/thread/cheat changed.");
                Need(!useFlowMode&&!returnOnFirstLack&&!average&&stringOps&&threshold==.9&&rateMultiplier==(double)wheels[n].inputSum&&Finite(rateMultiplier)&&rateMultiplier>0,"Original wheel resource call recipe changed.");
                ModuleResource[] rows=electricInputs[n];
                for(int i=0;i<rows.Length;i++)
                {
                    ModuleResource row=rows[i];Need(handler.inputResources.Contains(row)&&row.name=="ElectricCharge","Electric input row identity changed.");
                    double requested=row.currentRequest,withdrawn=row.currentAmount;
                    Need(Finite(requested)&&Finite(withdrawn)&&requested>=0&&withdrawn>=0,"Original electric withdrawal invalid.");
                    Need(electricDraws.Count<4096,"Electric withdrawal ledger bound exceeded.");
                    double requestTotal=Report.grossElectricChargeRequested+requested,withdrawTotal=Report.grossElectricChargeWithdrawn+withdrawn;
                    Need(Finite(requestTotal)&&Finite(withdrawTotal),"Electric withdrawal total overflow.");
                    electricDraws.Add(new ElectricDraw{fixedTime=Time.fixedTime,phase=phase,partId=unchecked((int)wheels[n].part.flightID),requested=requested,withdrawn=withdrawn});
                    Report.electricResourceCalls++;if(withdrawn>0)Report.electricPositiveCalls++;
                    Report.grossElectricChargeRequested=requestTotal;Report.grossElectricChargeWithdrawn=withdrawTotal;
                }
                return;
            }
        }
        void Control(FlightCtrlState state)
        {
            if(disposed||Report.failure!=null)return;
            try
            {
                Audit();Need(ReferenceEquals(state,vessel.ctrlState),"Original pilot input object changed.");
                Need(Idle(state)||OurInput(state),"Pilot/autopilot input override changed the owned input recipe.");
                float elapsed=Elapsed();
                string next=Report.normalReturns==1?"normal-return":Report.warpEntries==1?"warp":PlannedPhase(Math.Min(elapsed,5.999f));
                bool rcs=next=="translation"||next=="warp";
                Clear(state);vessel.ActionGroups.SetGroup(KSPActionGroup.RCS,rcs);
                if(next=="attitude"){state.pitch=.35f;state.yaw=.15f;state.roll=-.2f;Report.attitudeCallbacks++;}
                else if(next=="translation"){state.X=.5f;state.Y=.2f;state.Z=-.3f;Report.translationCallbacks++;}
                else if(next=="warp"){state.pitch=-.2f;state.yaw=.1f;state.X=-.3f;state.Z=.4f;Report.warpCallbacks++;}
                last[0]=state.pitch;last[1]=state.yaw;last[2]=state.roll;last[3]=state.X;last[4]=state.Y;last[5]=state.Z;
                applied=true;phase=next;Report.callbacks++;
            }
            catch(Exception error){Fail(error.ToString());}
        }
        void Mode(TimeWarp.Modes mode,int index)
        {
            Need(ReferenceEquals(TimeWarp.fetch,warp),"Stock warp owner replaced.");
            TimeWarp.SetRate(0,true,false);
            Need((bool)setMode.Invoke(warp,new object[]{mode}),"Stock warp mode transition rejected.");
            TimeWarp.SetRate(index,true,false);
        }
        public void AfterPhysics()
        {
            Audit();float elapsed=Elapsed();Report.duration=elapsed;
            if(elapsed>=6&&elapsed<10&&Report.warpEntries==0){warpTouched=true;Mode(TimeWarp.Modes.LOW,3);Need(TimeWarp.WarpMode==TimeWarp.Modes.LOW&&TimeWarp.CurrentRate==4&&TimeWarp.CurrentRateIndex==3&&Time.timeScale==4&&Time.fixedDeltaTime==.08f,"Stock LOW4x transition readback failed.");Report.warpEntries++;}
            if(elapsed>=10&&Report.warpEntries==1&&Report.normalReturns==0){Mode(originalMode,Report.originalWarpIndex);Need(TimeWarp.WarpMode==originalMode&&TimeWarp.CurrentRate==1&&Time.timeScale==1&&Time.fixedDeltaTime==.02f,"Stock normal-time return readback failed.");Report.normalReturns++;}
        }
        public void Audit()
        {
            Need(!disposed&&Thread.CurrentThread.ManagedThreadId==thread,"Controlled rigid owner thread/lifetime changed.");
            Need(Report.failure==null,"Controlled pilot callback failure: "+Report.failure);
            Need(vessel!=null&&ReferenceEquals(FlightGlobals.ActiveVessel,vessel)&&vessel.loaded&&!vessel.packed&&!vessel.HoldPhysics&&!FlightDriver.Pause&&vessel.IsControllable,"Controlled pilot vessel readiness changed.");
            Need(vessel.parts.Count==parts.Length,"Controlled pilot topology changed.");for(int n=0;n<parts.Length;n++)Need(ReferenceEquals(vessel.parts[n],parts[n])&&parts[n]!=null&&parts[n].vessel==vessel,"Controlled pilot part identity changed.");
            Need(Equals(vessel.OnFlyByWire,expectedFly)&&Equals(vessel.OnPreAutopilotUpdate,originalPre)&&Equals(vessel.OnAutopilotUpdate,originalAuto)&&Equals(vessel.OnPostAutopilotUpdate,originalPost),"Original pilot subscriber chain changed or owned callback is no longer last.");
            Need(vessel.GroupOverride==0&&!vessel.ActionGroups[KSPActionGroup.SAS]&&!vessel.Autopilot.Enabled,"SAS/autopilot/group override became active.");
            Need(!CheatOptions.InfinitePropellant&&ReferenceEquals(electricActive,this),"Electric observer/cheat admission changed.");
            Need(vessel.ActionGroups[KSPActionGroup.RCS]==(phase=="translation"||phase=="warp"),"RCS action group changed outside the delivered control recipe.");
            for(int n=0;n<wheels.Length;n++)Need(wheels[n]!=null&&wheels[n].actuatorModeCycle==Report.reactionWheelModes[n]&&(int)wheels[n].State==Report.reactionWheelStates[n]&&wheels[n].moduleIsEnabled==Report.reactionWheelEnabled[n],"Captured reaction-wheel mode/state changed.");
            Need(ReferenceEquals(TimeWarp.fetch,warp),"Stock warp owner replaced.");
            bool low=Report.warpEntries==1&&Report.normalReturns==0;
            Need(low?TimeWarp.WarpMode==TimeWarp.Modes.LOW&&TimeWarp.CurrentRate==4&&TimeWarp.CurrentRateIndex==3&&Time.timeScale==4&&Time.fixedDeltaTime==.08f:TimeWarp.WarpMode==originalMode&&TimeWarp.CurrentRate==1&&TimeWarp.CurrentRateIndex==Report.originalWarpIndex&&Time.timeScale==1&&Time.fixedDeltaTime==.02f,"Unrequested stock warp transition.");
        }
        public void Dispose()
        {
            if(disposed)return;Need(Thread.CurrentThread.ManagedThreadId==thread,"Controlled rigid cleanup requires owned thread.");
            try {if(electricAttempted&&!ElectricHookInstalled())Fail("Original electric observer graph changed before cleanup.");}
            catch(Exception error){Fail("Electric observer graph audit: "+error);}
            try
            {
                if(electricAttempted)electricHarmony.Unpatch(electricMethod,HarmonyPatchType.Postfix,electricOwner);
                Report.electricObserverReleased=!electricAttempted||Harmony.GetPatchInfo(electricMethod)?.Owners.Contains(electricOwner)!=true;
                if(Report.electricObserverReleased&&ReferenceEquals(electricActive,this))electricActive=null;
                Need(Report.electricObserverReleased,"Owned electric observer remained installed.");
            }
            catch(Exception e){Fail("Electric observer cleanup: "+e);}
            try {if(attached&&vessel!=null){vessel.OnFlyByWire-=callback;Report.subscribersRestored=Equals(vessel.OnFlyByWire,originalFly)&&Equals(vessel.OnPreAutopilotUpdate,originalPre)&&Equals(vessel.OnAutopilotUpdate,originalAuto)&&Equals(vessel.OnPostAutopilotUpdate,originalPost);attached=!Report.subscribersRestored;}else Report.subscribersRestored=!attached;}
            catch(Exception e){Fail("Input detach: "+e);}
            try {if(vessel!=null){if(applied&&OurInput(vessel.ctrlState))Clear(vessel.ctrlState);Report.controlsRestored=Idle(vessel.ctrlState);Need(Report.controlsRestored,"Delivered controls no longer match our recipe; foreign input preserved.");}else Need(false,"Controlled vessel destroyed before input cleanup.");}
            catch(Exception e){Fail("Input restoration: "+e);}
            try {if(groupsTouched&&vessel!=null){vessel.ActionGroups.SetGroup(KSPActionGroup.SAS,Report.originalSas);vessel.ActionGroups.SetGroup(KSPActionGroup.RCS,Report.originalRcs);Report.groupsRestored=vessel.ActionGroups[KSPActionGroup.SAS]==Report.originalSas&&vessel.ActionGroups[KSPActionGroup.RCS]==Report.originalRcs;}else Report.groupsRestored=!groupsTouched;}
            catch(Exception e){Fail("Action group restoration: "+e);}
            try {if(warpTouched)Mode(originalMode,Report.originalWarpIndex);Report.warpRestored=ReferenceEquals(TimeWarp.fetch,warp)&&TimeWarp.WarpMode==originalMode&&TimeWarp.CurrentRateIndex==Report.originalWarpIndex&&TimeWarp.CurrentRate==Report.originalWarpRate&&Time.timeScale==1&&Time.fixedDeltaTime==.02f;}
            catch(Exception e){Fail("Warp restoration: "+e);}
            try {Report.duration=Time.fixedTime-enteredTime;Report.finalElectricCharge=Resource("ElectricCharge");Report.finalMonoPropellant=Resource("MonoPropellant");Report.finalSubscribers=vessel==null?new string[0]:Names(vessel.OnFlyByWire);if(Report.electricDraws==null)Report.electricDraws=electricDraws.ToArray();}
            catch(Exception e){Fail("Closing readback: "+e);}
            disposed=Report.subscribersRestored&&Report.controlsRestored&&Report.groupsRestored&&Report.warpRestored&&Report.electricObserverReleased;
            Report.passed=disposed&&Report.failure==null&&Report.duration>=12&&Report.attitudeCallbacks>0&&Report.translationCallbacks>0&&Report.warpCallbacks>0&&Report.warpEntries==1&&Report.normalReturns==1&&Report.electricPositiveCalls>0&&Report.grossElectricChargeWithdrawn>0&&Report.finalMonoPropellant<Report.initialMonoPropellant;
        }
    }
}
