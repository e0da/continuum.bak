# Owned runtime thermal scheduling

The thermal scheduler advances the owned model's temperatures independently of its mechanical cadence. Controls, actuators, resource withdrawal, mass properties, environmental observations, aerodynamic forces and rigid motion still execute together at every mechanical step. The default reference policy retains the previous complete thermal fold. `OwnedRuntimeSystems.SetThermalPolicy(expected, policy)` selects an independent policy through a cold, stamp-checked world publication.

This is an authored SI thermal model, not a replacement of all stock KSP thermal behavior. Part temperatures currently do not feed mechanical properties, resource demand or actuator/aerodynamic coefficients. Temperature-dependent providers require a new coupling contract before independent scheduling can support them.

## Ordered energy and forcing

Each mechanical interval records actuator/aerodynamic heat in joules and convection/radiation exposure and source terms for every part. The bank retains up to eight chronological segments, including each interval's duration. Heat delivered early and heat delivered late therefore remain different inputs. Ambient changes remain separate segments; the implementation does not replace them with one averaged environment.

Within each mechanical interval, the observed environment and generated heat rate are treated as constant. This is the declared mechanical sampling model; the thermal solver cannot certify unobserved changes inside that interval. The source energy ledger is exact for the supplied interval inputs and ordering, subject to floating-point arithmetic.

Pending energy, duration, forcing segments, integrated physical time, causal revision, topology revision and policy are bank-owned state. Provisional steps copy them and cancellation restores the previous publication. Read leases expose both the mechanical frontier and the older integrated thermal frontier.

## Triggers and numerical scope

An independent thermal publication occurs when any of these limits is reached:

- the policy's maximum physical lag;
- its conservative deferred-temperature excursion bound;
- eight retained forcing segments;
- an explicit same-time `Systems.FlushThermal(expected)` request.

The excursion bound uses the largest equilibrium temperature in the individual forcing segments, total positive source heat, and a conservative conduction contribution. It bounds the held-temperature discrepancy and may force frequent updates in strong exchange conditions. It is intentionally conservative; independent scheduling is not a promise of fewer thermal solves on every craft.

The solver replays segments in chronological order. It solves the nonlinear per-part convection/radiation equation implicitly and uses deterministic serial implicit pair exchanges for conduction. Each exchange subtracts and adds the same energy, conserving the isolated pair's total thermal energy. Finer subdivisions reduce the serial splitting and integration error.

Acceptance compares two subdivision levels, using twice their endpoint temperature discrepancy against the frozen absolute-plus-relative Kelvin budget. This is an error estimator for the declared model, not a universal mathematical error certificate for arbitrary future providers. Failed convergence rejects the provisional step. The reported source/exchange energy totals distinguish injected heat from external exchange.

The reference fold applies source heat, then its existing surface exchange approximation, then one serial conduction sweep per mechanical interval. The independent solver can produce a different, more finely integrated temperature trajectory. Reference equivalence is not claimed. Independent analytic tests cover piecewise early/late heat pulses with changing ambient temperature, two-capacity conduction energy and decay, and radiation against a finely stepped ODE oracle. These tests passed in the integrated runtime suite; native acquired-craft behavior and performance remain separate qualification gates.

## Checkpoints, events and topology

New runtime checkpoints use JSON schema `continuum-owned-runtime-checkpoint/v2` and binary version 2. They persist the complete pending ledger and thermal policy. Binary version 1 retains its original field layout and schema digest; legacy checkpoints with no thermal state restore the reference policy at their saved physical frontier. Existing evidence files are unchanged.

Save/restore does not flush or discard pending heat. A same-time flush increments state revision without advancing mechanical time. Rebase preserves pending state. Topology changes, temperature overwrite and passive time jumps reject pending intervals; callers must flush under the existing model before those operations. A changed thermal plan cannot consume another plan's pending interval. Policy replacement likewise requires a clean frontier.

Passive Kepler coast explicitly freezes resources and thermal state. It is eligible only with no pending thermal interval; local stepping can continue until publication when that condition is unmet. Permitted passive clock jumps record their frozen duration rather than pretending to integrate heat. Cold worker commands and checkpoint restoration must preserve the world stamp and synchronize their timeline after any same-time publication.

## Ownership and cost

Hot input history and solver scratch are preallocated. No per-step JSON serialization or per-part callback dispatch is introduced. Bank copying, conservative-bound evaluation, refinement work and thermal-state validation remain real costs. A bound writer thread owns mechanical and cold thermal mutations; read leases and release may cross threads. Reader pressure is reported separately from model faults and cannot justify dropping pending energy or simulation demand.
