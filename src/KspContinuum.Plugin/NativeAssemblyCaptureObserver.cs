using System;
using System.IO;
using UnityEngine;

namespace KspContinuum
{
    internal sealed class NativeAssemblyCaptureObserver : IPlayerLoopBracketObserver
    {
        readonly string output, runId;
        readonly Func<bool> active;
        bool attempted;
        public string ManifestPath { get; private set; }
        public string Error { get; private set; }
        public NativeAssemblyCaptureObserver(string output,string runId,Func<bool> active)
        { this.output=output;this.runId=runId;this.active=active; }
        public void Before(string phase,int frame,double time) {}
        public void After(string phase,int frame,double time)
        {
            if(attempted||!active()||phase!=typeof(UnityEngine.PlayerLoop.FixedUpdate.PhysicsFixedUpdate).FullName)return;
            attempted=true;
            try
            {
                ManifestPath=NativeAssemblySceneCapture.Capture(FlightGlobals.ActiveVessel,output,runId);
                File.WriteAllText(Path.Combine(output,"boundary.txt"),"after "+phase+"\nframe="+frame+"\nfixedTime="+time.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+"\nmanifestSha256="+ScaleCheckpointLoadState.HashFile(ManifestPath)+"\n");
            }
            catch(Exception error) {Error=error.ToString();Debug.LogException(error);}
        }
        public void Fault(string phase,Exception error) {if(Error==null)Error=phase+": "+error;}
    }
}
