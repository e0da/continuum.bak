using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace KspContinuum
{
    internal sealed class StockReloadMassInitialization : IDisposable
    {
        const string Owner="continuum.stock-reload-mass-initialization";
        static StockReloadMassInitialization active;
        readonly Harmony harmony=new Harmony(Owner);
        readonly QuickSaveLoad producer;
        readonly Action<Game> admit;
        readonly CheckpointReloadQualification.ReportData report;
        readonly MethodInfo quick,finished,scenario,start;
        bool hooked,subscribed,inFinished;
        Game scenarioGame;
        internal Exception Error;
        internal StockReloadMassInitialization(Action<Game> admitGame,CheckpointReloadQualification.ReportData receipt)
        {
            admit=admitGame;report=receipt;producer=QuickSaveLoad.fetch;
            StartupPinnedGameIdentity.Require(typeof(QuickSaveLoad).Assembly);
            quick=AccessTools.DeclaredMethod(typeof(QuickSaveLoad),"quickLoad",new[]{typeof(string),typeof(string)});
            finished=AccessTools.DeclaredMethod(typeof(QuickSaveLoad),"onQuickloadPipelineFinished",new[]{typeof(ConfigNode),typeof(string),typeof(Version)});
            scenario=AccessTools.DeclaredMethod(typeof(GamePersistence),"UpdateScenarioModules",new[]{typeof(Game)});
            start=AccessTools.DeclaredMethod(typeof(FlightDriver),"StartAndFocusVessel",new[]{typeof(Game),typeof(int)});
            if(active!=null||producer==null||producer.GetType()!=typeof(QuickSaveLoad)||new[]{quick,finished,scenario,start}.Any(m=>m==null||m.GetMethodBody()==null||m.Module.ModuleVersionId!=new Guid("10657063-2fc3-43a7-84fa-d39e75e877bf")||Harmony.GetPatchInfo(m)?.Owners.Any()==true)||quick.ReturnType!=typeof(void)||finished.ReturnType!=typeof(void)||scenario.ReturnType!=typeof(bool)||start.ReturnType!=typeof(void)||quick.IsStatic||finished.IsStatic||!scenario.IsStatic||!start.IsStatic)
                throw new InvalidOperationException("Original stock quickload producer/dispatch ownership differs.");
            active=this;hooked=true;
            try
            {
                harmony.Patch(finished,prefix:new HarmonyMethod(AccessTools.DeclaredMethod(typeof(StockReloadMassInitialization),"FinishedPrefix")),finalizer:new HarmonyMethod(AccessTools.DeclaredMethod(typeof(StockReloadMassInitialization),"FinishedFinalizer")));
                harmony.Patch(scenario,finalizer:new HarmonyMethod(AccessTools.DeclaredMethod(typeof(StockReloadMassInitialization),"ScenarioFinalizer")));
                harmony.Patch(start,prefix:new HarmonyMethod(AccessTools.DeclaredMethod(typeof(StockReloadMassInitialization),"StartPrefix")));
                GameEvents.onGameStatePostLoad.Add(PostLoad);subscribed=true;
                report.reloadProducer="QuickSaveLoad.quickLoad→upgrade-pipeline→onQuickloadPipelineFinished→FlightDriver.StartAndFocusVessel";
                report.stockReloadHooksRemoved=false;
            }
            catch{Dispose();throw;}
        }
        internal void Run(string filename,string folder)
        {
            try{quick.Invoke(producer,new object[]{filename,folder});}
            catch(Exception error){Fault(error);throw;}
        }
        void Fault(Exception error){Error=error;report.stockReloadError=ReloadMassInitialization.ExceptionText(error);}
        void PostLoad(ConfigNode node)
        {
            if(!inFinished)return;
            report.stockPostLoadCalls++;report.postLoadNotified=true;
        }
        static void FinishedPrefix(QuickSaveLoad __instance)
        {
            var owner=active;if(owner==null||__instance!=owner.producer)return;
            if(owner.inFinished||owner.report.stockFinishedCalls!=0)throw new InvalidOperationException("Stock quickload completion reentered.");
            owner.inFinished=true;owner.report.stockFinishedCalls++;
        }
        static Exception FinishedFinalizer(QuickSaveLoad __instance,Exception __exception)
        {
            var owner=active;if(owner==null||__instance!=owner.producer)return __exception;
            owner.inFinished=false;
            if(__exception!=null)owner.Fault(__exception);
            else if(owner.report.stockDispatchCalls!=1)owner.Fault(new InvalidOperationException("Stock quickload did not dispatch one compatible FLIGHT game."));
            return __exception;
        }
        static Exception ScenarioFinalizer(Game __0,Exception __exception)
        {
            var owner=active;if(owner==null||!owner.inFinished)return __exception;
            if(__exception==null){owner.scenarioGame=__0;owner.report.stockScenarioCalls++;owner.report.scenarioModulesUpdated=true;}
            return __exception;
        }
        static void StartPrefix(Game __0,int __1)
        {
            var owner=active;if(owner==null||!owner.inFinished)return;
            try
            {
                if(owner.report.stockDispatchCalls!=0||owner.report.stockScenarioCalls!=1||owner.report.stockPostLoadCalls!=1||!ReferenceEquals(__0,owner.scenarioGame)||__0==null||__0.flightState==null||__1!=__0.flightState.activeVesselIdx)throw new InvalidOperationException("Stock reload handler/dispatch sequence differs.");
                owner.admit(__0);owner.report.stockDispatchCalls++;
            }
            catch(Exception error){owner.Fault(error);throw;}
        }
        public void Dispose()
        {
            if(subscribed){GameEvents.onGameStatePostLoad.Remove(PostLoad);subscribed=false;}
            if(hooked){harmony.UnpatchAll(Owner);hooked=false;}
            report.stockReloadHooksRemoved=new[]{finished,scenario,start}.All(m=>m!=null&&Harmony.GetPatchInfo(m)?.Owners.Contains(Owner)!=true)&&!subscribed;
            if(ReferenceEquals(active,this))active=null;
        }
    }
}
