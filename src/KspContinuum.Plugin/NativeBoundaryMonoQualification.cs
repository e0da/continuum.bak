using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace KspContinuum
{
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    public sealed class NativeBoundaryMonoQualification : MonoBehaviour
    {
        const int Samples = 101;
        const int Warmups = 12;
        const double StepSeconds = 1.0 / 60.0;

        public void Start()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-startup-build-cache") >= 0) return;
            bool benchmark = Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-native-boundary-bench") >= 0;
            if (!benchmark && Array.IndexOf(Environment.GetCommandLineArgs(), "--continuum-scale-profile") < 0) return;
            try
            {
                StartupAssetGeneration.ObserveMenu();
                if (!benchmark) return;
                NativeBoundaryMonoReport report = Run();
                string directory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "KspContinuum", "PluginData");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "native-boundary-mono-" +
                    DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + ".json");
                File.WriteAllText(path, ReportJson.Encode(report));
                Application.Quit(0);
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogException(error);
                Application.Quit(2);
            }
        }

        static NativeBoundaryMonoReport Run()
        {
            if (Native.ContinuumBoundaryAbiVersion() != 1)
                throw new InvalidOperationException("native ABI version mismatch");
            if (Marshal.SizeOf(typeof(Body)) != 80)
                throw new InvalidOperationException("native ABI body layout mismatch");

            var rows = new List<NativeBoundaryMonoRow>();
            int[] counts = { 64, 256, 1024, 4096 };
            uint[] steps = { 1, 4 };
            foreach (int count in counts)
            foreach (uint stepCount in steps)
            {
                Body[] initial = Fixture(count);
                Body[] managed = (Body[])initial.Clone();
                Body[] native = (Body[])initial.Clone();
                NativeBoundaryMonoTiming managedTiming = Measure(() => Integrate(managed, stepCount), count);
                NativeBoundaryMonoTiming nativeTiming = Measure(() => Native.Integrate(native, StepSeconds, stepCount), count);
                Body[] expected = (Body[])initial.Clone();
                Body[] observed = (Body[])initial.Clone();
                Integrate(expected, stepCount);
                Native.Integrate(observed, StepSeconds, stepCount);
                if (!expected.SequenceEqual(observed))
                    throw new InvalidOperationException("native and managed results differ");
                rows.Add(new NativeBoundaryMonoRow
                {
                    bodies = count, steps = checked((int)stepCount), managed = managedTiming, native = nativeTiming,
                    managedToNativeRatio = managedTiming.medianNanoseconds / nativeTiming.medianNanoseconds
                });
            }
            NativeBoundaryMonoTiming noop = Measure(
                () => GC.KeepAlive(Native.ContinuumBoundaryNoop(0x123456789abcdef0)), 1, 1001);
            return new NativeBoundaryMonoReport
            {
                schema = "continuum-native-boundary-mono/v1",
                runtime = Environment.Version.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                operatingSystem = RuntimeInformation.OSDescription,
                samplesPerCase = Samples,
                warmupsPerCase = Warmups,
                stepSeconds = StepSeconds,
                scope = "Unity embedded Mono synchronous P/Invoke and Rust f64 kernel; excludes capture, packing, publication, physics, rendering and vessel work.",
                noop = noop,
                rows = rows.ToArray()
            };
        }

        static Body[] Fixture(int count)
        {
            var bodies = new Body[count];
            for (int i = 0; i < count; i++) bodies[i] = new Body
            {
                px = i * 0.25, py = i % 31, pz = i * -0.125,
                vx = 0.5 + i * 0.00001, vy = -0.25, vz = i % 7 * 0.01,
                fx = (i % 13 - 6) * 0.2, fy = -9.81, fz = i % 5 * 0.1,
                inverseMass = 1.0 / (1 + i % 97)
            };
            return bodies;
        }

        static void Integrate(Body[] bodies, uint steps)
        {
            for (uint step = 0; step < steps; step++)
            for (int i = 0; i < bodies.Length; i++)
            {
                double scale = StepSeconds * bodies[i].inverseMass;
                bodies[i].vx += bodies[i].fx * scale;
                bodies[i].vy += bodies[i].fy * scale;
                bodies[i].vz += bodies[i].fz * scale;
                bodies[i].px += bodies[i].vx * StepSeconds;
                bodies[i].py += bodies[i].vy * StepSeconds;
                bodies[i].pz += bodies[i].vz * StepSeconds;
            }
        }

        static NativeBoundaryMonoTiming Measure(Action operation, int bodies, int samples = Samples)
        {
            for (int i = 0; i < Warmups; i++) operation();
            var ticks = new long[samples];
            for (int i = 0; i < samples; i++)
            {
                long start = Stopwatch.GetTimestamp();
                operation();
                ticks[i] = Stopwatch.GetTimestamp() - start;
            }
            Array.Sort(ticks);
            double scale = 1000000000.0 / Stopwatch.Frequency;
            double median = ticks[ticks.Length / 2] * scale;
            double p95 = ticks[(int)Math.Ceiling(ticks.Length * 0.95) - 1] * scale;
            return new NativeBoundaryMonoTiming
            {
                medianNanoseconds = median, p95Nanoseconds = p95,
                nanosecondsPerBodyAtMedian = median / bodies
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Body : IEquatable<Body>
        {
            public double px, py, pz, vx, vy, vz, fx, fy, fz, inverseMass;
            public bool Equals(Body other)
            {
                return px == other.px && py == other.py && pz == other.pz &&
                    vx == other.vx && vy == other.vy && vz == other.vz &&
                    fx == other.fx && fy == other.fy && fz == other.fz && inverseMass == other.inverseMass;
            }
        }

        static class Native
        {
            const string Library = "continuum_native_boundary";
            [DllImport(Library, EntryPoint = "continuum_boundary_abi_version")]
            internal static extern uint ContinuumBoundaryAbiVersion();
            [DllImport(Library, EntryPoint = "continuum_boundary_noop")]
            internal static extern ulong ContinuumBoundaryNoop(ulong value);
            [DllImport(Library, EntryPoint = "continuum_integrate_f64")]
            static extern unsafe int IntegrateF64(Body* bodies, UIntPtr count, double dt, uint steps);
            internal static unsafe void Integrate(Body[] bodies, double dt, uint steps)
            {
                fixed (Body* pointer = bodies)
                    if (IntegrateF64(pointer, (UIntPtr)bodies.Length, dt, steps) != 0)
                        throw new InvalidOperationException("native f64 integration failed");
            }
        }
    }

}
