# Sleeping-island fixed-step experiment

`--continuum-sleeping-island` adds one opt-in strategy to the existing settled station profiler. It captures the active
vessel's current rigidbody, joint, and collider graph without changing that topology. Immediately before each native
`PhysicsFixedUpdate`, it calls `Sleep` on every captured dynamic body. This asks whether a quiet vacuum vessel can be
represented as one inactive physics island while KSP continues running its ordinary scripts, modules, rendering, and
global clock.

The strategy admits only an unpacked active vessel in vacuum orbit at normal time with zero throttle, inactive engines,
SAS off, and RCS off. It rejects missing or changed bodies, joints, parts, vessel identity, or flight regime. It samples
all body centers in the root body's rotating frame on admission and periodically after native physics; more than 1 mm
of internal drift invalidates the run. Cleanup restores the captured awake/asleep state and verifies that all captured
joints remain present. The receipt is embedded as `sleepingIsland` in the ordinary `markers/v2` report.

The installed diagnostic used separate fresh stock and candidate processes from the same immutable 196-part station
checkpoint. The stock run measured a 1.5803 ms mean native `PhysicsFixedUpdate` and a 4.5224 ms mean complete
`FixedUpdate`. The candidate measured 2.2046 ms and 5.4187 ms respectively: about 39.5% slower in the native scope and
19.8% slower in the complete parent. It completed 230 steps with the 110-body, 144-joint, 328-collider graph retained,
and observed at most 7.8e-6 m of internal drift. The source save remained unchanged.

This is a negative result. Reasserting sleep on every body immediately before every native physics step costs more than
it saves in this station workload, so the candidate will not be merged or hardened. Review also found that the probe's
qualification needed exact repeated collider-identity checks, sleep-state restoration readback, and explicit attitude,
translation, and wheel-control admission checks. Those gaps do not explain away the measured slowdown and are left
unimplemented because the experiment already rejects its performance hypothesis.

This experiment does not create a compound body, delete joints, integrate forces, support contacts, or preserve thrust,
staging, docking, robotics, wheel, damage, or arbitrary module behavior. It deliberately suppresses native motion for a
settled quiet island and restores the graph after the bounded capture. The result rules out per-step sleep assertion as
the next station optimization. It does not test a semantic compound cluster, which would remove native bodies and joints
and therefore changes the scaling term this experiment preserved.
