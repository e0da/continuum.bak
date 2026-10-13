# Owned runtime executable schedule examples

These are original C# implementations of the actual owned player's schedule
interface, not metadata-only reload examples. Build only after the owned Unity
player assembly exists:

```text
dotnet build tools/KspContinuum.OwnedRuntimeScheduleExample -c Release -p:OWNED_RUNTIME_ASSEMBLY=/ABSOLUTE/PLAYER/Data/Managed/Assembly-CSharp.dll
```

The reference must be that player's `Assembly-CSharp.dll`, containing the
compiled Core runtime. Referencing a separate net8 Core DLL or the original KSP
assembly gives a different or absent ABI. The assembly reference is not copied
to the example output. Building neither installs nor reloads anything.

Use the loopback control client with the player's reported port and a new JSON
path for each response. Replace `FULL_DLL` with the absolute built example DLL:

```text
--command "pause on"
--command "reload FULL_DLL KspContinuum.OwnedRuntimeScheduleExample.ForwardSchedule"
--command "step 1"
--command "reload FULL_DLL KspContinuum.OwnedRuntimeScheduleExample.GravityProbeSchedule"
--command "step 1"
--command "reload FULL_DLL KspContinuum.OwnedRuntimeScheduleExample.RejectedSchedule"
```

`ForwardSchedule` executes the existing bulk schedule. `GravityProbeSchedule`
adds −0.25 m/s² on world Y to the supplied gravity for that invocation while
preserving all environment metadata, other environment values and controls. It
declares changed physics through its implementation identity; the original
environment descriptor remains the external input. A zero-gravity, zero-input
step of duration dt should therefore add −0.25·dt m/s to velocity Y.

`RejectedSchedule` deliberately fails ABI admission before execution.
`FailedSchedule` has a valid ABI and executes provisional systems work, then
throws before host publication. Its command is:

```text
--command "reload FULL_DLL KspContinuum.OwnedRuntimeScheduleExample.FailedSchedule"
--command "step 1"
```

Retain the failed response and independently compare committed state/resources
and epoch before/after. These examples keep no hidden physical state and never
commit a step themselves. A successful assembly load or command response does
not alone prove numerical effects, rollback, checkpoint continuation or pixels.
The current server command parser requires path arguments without spaces.
