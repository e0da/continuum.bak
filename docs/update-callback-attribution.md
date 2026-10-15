# Declared Update callback ownership

`CONTINUUM_UPDATE_CALLBACK_ATTRIBUTION=1` enables one intrusive diagnostic owner. It discovers the currently loaded Unity-referencing assemblies, excludes the entire Continuum helper, and patches declared managed instance `void Update()` bodies on `MonoBehaviour` types. Inherited bodies are admitted once; overrides and explicit base calls remain distinct. It preserves original calls, state and exceptions.

The frozen identity contains assembly bytes, MVID, method token and IL digest. Admission rejects foreign hooks, unsupported bodies and scan failures. Installation and cleanup audit the complete eligible assembly/target set and exact owned patch graph. Transient foreign mutations between those audits are outside the proof.

Only main-thread callbacks inside `ScriptRunBehaviourUpdate` windows whose opening observed active capture contribute rows. Flat primitive storage is allocated before capture: at most 512 bodies, 1,024 windows and 128 nested entries. Sparse JSON export occurs after patch removal. No per-callback serialization or file access is used.

Inclusive elapsed wall time contains descendants and observer tax. Self time subtracts directly observed child envelopes; unobserved descendants and hook tax remain. Inclusive rows overlap. Self sums must fit within each window, but are not exclusive CPU costs. Zero calls remains explicit `not-reached` coverage.

The independent Rust validator binds the owned PID and run, clocks, identities, lifecycle/fault counters, raw calls and reached targets. Only fully enclosed windows enter costs; straddlers remain in raw coverage. At least one enclosed callback must be reached. A diagnostic receipt is not eligible for a clean performance comparison.

Retained raw evidence can be reassessed without launching KSP:

```
continuum-research-lab update-callback-summary --report ABSOLUTE_RAW_REPORT --pid OWNED_PID --output NEW_ABSOLUTE_RECEIPT
```

The focused validator checks reject false self budgets, duplicated or unordered sparse slots, bad identity/clock/lifecycle coverage, hidden faults, wrong PID and empty enclosed coverage. They also preserve valid nested inclusive accounting and correctly exclude straddler costs.

In the private 512-part atmospheric diagnostic, all 367 discovered targets were patched and removed; 108 were reached across 106 enclosed windows. `FlightIntegrator.Update` was the largest admitted row at 3.12309 ms per rendered frame. This is the whole callback with descendants and observer tax. It selects the next investigation; it establishes neither an inner kernel cost nor an FPS gain. The narrower existing integrator observer can distinguish flux preparation and scheduled convection/solar/body passes after exact hook coexistence is admitted.
