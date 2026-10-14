using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class InputCadenceCensus : MonoBehaviour
    {
        const string Flag = "--continuum-input-cadence-census";
        const string QuitFlag = "--continuum-input-cadence-quit";
        const string Owner = "continuum.input-cadence-census";
        const int RequiredFixedCallbacks = 512;
        static InputCadenceCensus instance;
        readonly HashSet<BindingKey> frameAxis = new HashSet<BindingKey>();
        readonly HashSet<BindingKey> callbackAxis = new HashSet<BindingKey>();
        readonly HashSet<BindingKey> frameKeys = new HashSet<BindingKey>();
        readonly HashSet<BindingKey> callbackKeys = new HashSet<BindingKey>();
        Harmony harmony;
        bool requested, capturing, done, quit;
        int frame = -1, callbacksInFrame, fixedDepth;
        long fixedCallbacks, renderedFrames, multiStepFrames, callbacksInMultiStepFrames;
        long axisCalls, axisSameCallbackRepeats, axisCrossCallbackRepeats;
        long keyCalls, keySameCallbackRepeats, keyCrossCallbackRepeats;

        readonly struct BindingKey : IEquatable<BindingKey>
        {
            readonly object binding;
            readonly bool argument;
            public BindingKey(object binding, bool argument) { this.binding = binding; this.argument = argument; }
            public bool Equals(BindingKey other) => ReferenceEquals(binding, other.binding) && argument == other.argument;
            public override bool Equals(object value) => value is BindingKey && Equals((BindingKey)value);
            public override int GetHashCode() => RuntimeHelpers.GetHashCode(binding) * 397 ^ (argument ? 1 : 0);
        }

        public void Start()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (Array.IndexOf(arguments, Flag) < 0) return;
            requested = true; quit = Array.IndexOf(arguments, QuitFlag) >= 0;
            try
            {
                MethodInfo fixedUpdate = AccessTools.DeclaredMethod(typeof(FlightInputHandler), "FixedUpdate");
                MethodInfo axis = AccessTools.DeclaredMethod(typeof(AxisBinding), "GetAxis", Type.EmptyTypes);
                MethodInfo key = AccessTools.DeclaredMethod(typeof(KeyBinding), "GetKey", new[] { typeof(bool) });
                if (fixedUpdate == null || axis == null || key == null) throw new MissingMethodException("Pinned input seams are unavailable.");
                harmony = new Harmony(Owner); instance = this;
                harmony.Patch(fixedUpdate,
                    prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(InputCadenceCensus), "FixedPrefix"), Priority.First),
                    finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(InputCadenceCensus), "FixedFinalizer"), Priority.Last));
                harmony.Patch(axis, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(InputCadenceCensus), "AxisPrefix"), Priority.First));
                harmony.Patch(key, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(InputCadenceCensus), "KeyPrefix"), Priority.First));
            }
            catch (Exception error) { Finish("installation-failed:" + error.GetType().Name, 2); }
        }

        public void Update()
        {
            if (!requested || done) return;
            if (!capturing)
            {
                if (ScaleCheckpointLoadState.Requested && !ScaleCheckpointLoadState.Ready) return;
                Vessel vessel = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                if (vessel == null || !vessel.loaded || vessel.packed || FlightDriver.Pause || TimeWarp.CurrentRate != 1) return;
                capturing = true;
            }
            if (fixedCallbacks >= RequiredFixedCallbacks) Finish("complete", 0);
        }

        static void FixedPrefix(out InputCadenceCensus __state)
        {
            __state = null;
            InputCadenceCensus owner = instance;
            if (owner == null || !owner.capturing || owner.done) return;
            int currentFrame = Time.frameCount;
            if (currentFrame != owner.frame)
            {
                owner.CompleteFrame(); owner.frame = currentFrame; owner.callbacksInFrame = 0;
                owner.frameAxis.Clear(); owner.frameKeys.Clear();
            }
            owner.callbacksInFrame++; owner.fixedCallbacks++;
            owner.callbackAxis.Clear(); owner.callbackKeys.Clear();
            owner.fixedDepth++;
            __state = owner;
        }

        static void FixedFinalizer(InputCadenceCensus __state)
        {
            if (__state != null && __state.fixedDepth > 0) __state.fixedDepth--;
        }

        static void AxisPrefix(AxisBinding __instance)
        {
            InputCadenceCensus owner = instance;
            if (owner == null || !owner.capturing || owner.done || owner.fixedDepth == 0) return;
            owner.axisCalls++;
            var binding = new BindingKey(__instance, false);
            if (!owner.callbackAxis.Add(binding)) owner.axisSameCallbackRepeats++;
            else if (!owner.frameAxis.Add(binding)) owner.axisCrossCallbackRepeats++;
        }

        static void KeyPrefix(KeyBinding __instance, bool __0)
        {
            InputCadenceCensus owner = instance;
            if (owner == null || !owner.capturing || owner.done || owner.fixedDepth == 0) return;
            owner.keyCalls++;
            var binding = new BindingKey(__instance, __0);
            if (!owner.callbackKeys.Add(binding)) owner.keySameCallbackRepeats++;
            else if (!owner.frameKeys.Add(binding)) owner.keyCrossCallbackRepeats++;
        }

        void CompleteFrame()
        {
            if (callbacksInFrame == 0) return;
            renderedFrames++;
            if (callbacksInFrame > 1) { multiStepFrames++; callbacksInMultiStepFrames += callbacksInFrame; }
        }

        void Finish(string status, int exitCode)
        {
            if (done) return;
            done = true; capturing = false; CompleteFrame();
            try { if (harmony != null) harmony.UnpatchAll(Owner); }
            catch (Exception error) { Debug.LogException(error); status = "cleanup-failed:" + error.GetType().Name; exitCode = 2; }
            harmony = null; if (ReferenceEquals(instance, this)) instance = null;
            Debug.Log("[Continuum.InputCadenceCensus] status=" + status + " fixedCallbacks=" + fixedCallbacks +
                " renderedFrames=" + renderedFrames + " multiStepFrames=" + multiStepFrames +
                " callbacksInMultiStepFrames=" + callbacksInMultiStepFrames + " axisCalls=" + axisCalls +
                " axisSameCallbackRepeats=" + axisSameCallbackRepeats + " axisCrossCallbackRepeats=" + axisCrossCallbackRepeats +
                " keyCalls=" + keyCalls + " keySameCallbackRepeats=" + keySameCallbackRepeats +
                " keyCrossCallbackRepeats=" + keyCrossCallbackRepeats);
            if (quit) Application.Quit(exitCode);
        }

        public void OnDestroy() { if (requested && !done) Finish("destroyed", 2); }
    }
}
