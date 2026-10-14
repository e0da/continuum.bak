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

For each changed executable image in an already running Mono player, use a fresh
assembly identity, for example `-p:AssemblyName=KspContinuum.Schedule.Generation2`.
Mono can retain a previously loaded assembly with the same identity even when
the file bytes change. Reusing that identity for changed code is unsupported;
a new path alone is insufficient. Reusing identical bytes to select another
schedule type in that image is supported. Worker and isolated-trial admissions
are separate contracts; the executable examples declare both where applicable.

The reload consumer now uses `OwnedRuntimeScheduleImages`: it clones and hashes
the exact image, reads its full identity through a temporary metadata snapshot,
then deletes that snapshot before executable admission. A process-wide cold
registry retains at most 32 images of at most 16 MiB each. Changed bytes under a
known identity and unknown already-loaded identities reject before schedule type
construction. Identical known bytes reuse the attested assembly; fresh identities
can load new generations. The host receives the admitted image digest rather
than a digest of bytes that Mono may have ignored. Rejected metadata cleanup
retains the primary exception plus its cleanup failure; cleanup-only failure
rejects admission explicitly.

This is a reserved single-writer cold loading contract, not a sandbox or assembly
unloader. Concurrent external assembly loading is outside that contract.
Successful load retains the image even if later type/host admission fails;
constructors of admitted images are not transactionally rolled back. Native Mono
qualification must exercise the changed-byte rejection and fresh-generation
numerical behavior independently of the portable registry fixtures.

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
