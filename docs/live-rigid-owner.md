# Live rigid vessel ownership

The diagnostic rigid owner advances an assembled craft as one native Rigidbody in KSP's existing physics scene. Original Part and Rigidbody identities remain present as collision-disabled kinematic proxies. This is the first live integration of the rigid-primary model; stock callbacks, resource updates and rendering continue running.

Select `--flight-live-rigid-owner ABS_FRESH_RECEIPT.json` in a resolved research-lab launch. It requires a pinned part count, normal time, vacuum, quiet controls and more than two simulated seconds of capture. It rejects other physics owners, attribution/capture treatments, docking/reload probes and ordinary live-play mode. The output directory must exist and the receipt must be new. The plugin also admits actual lifecycle, topology and PlayerLoop ordering at runtime.

The owner takes over after a completed native step. Before subsequent native steps it refreshes mass, center of mass and the full inertia tensor, reduces original force queues and commits them to the aggregate. Afterward it publishes member native and Transform poses in hierarchy order. Uniform FlightIntegrator acceleration is applied once, with exactly one source call required for every owned native body. Other force modes preserve the recipient's mass or inertia conversion and each force's moment about the aggregate center.

A pinned managed IL bridge redirects stock and loaded MechJeb Rigidbody force, velocity, angular velocity, world-center and point-velocity operations. Unowned receivers retain native behavior. Vessel statistics use the aggregate authority. Vessel frame translations and velocity corrections apply once to the aggregate; translations also move staged force points. Unsupported direct velocity writers and lifecycle transactions restore stock ownership before falling through. Assembly/IL identities, selected/unrouted callsites, frame-hook cooperation and owned-hook removal are recorded.

Admission excludes servos, active wheels, contacts and changing topology. Exact stock landing gears may remain fully retracted and ungrounded with their wheel controller and collider disabled; this state is checked again before physics. The aggregate has no colliders and zero damping. All-body momentum projection, native mass weighting and rigid motion are declared model choices. This does not establish closure over arbitrary mods or every native writer.

The bounded diagnostic queues a perpendicular off-center force through an original physical Part after 0.2 simulated seconds. After 1.5 seconds it restores original body flags and constituent point velocities, retires the aggregate, and requires at least two further stock native steps. A private raw receipt includes each pre/post step, source commands, reductions, reader coverage, proxy errors, restoration readback and hook ledger. A frame is captured at end of frame while ownership is active.

Verify the private receipt independently:

```text
dotnet run --project tests/KspContinuum.LiveRigid.Tests -c Release -- ABS_RECEIPT.json
```

The reader uses raw native floats and independent tensor/wrench math. It checks force-mode conversions, lever moments, linear velocity changes, angular momentum residuals and torque response, as well as source identities, acceleration coverage, proxy errors and restoration. Its real-craft proof requires the frozen 512-part station. Frozen gates are relative `1e-4` plus absolute `1e-4` for linear velocity delta; 2% of the largest before/after/applied angular momentum plus `1e-4` for angular residual; relative `1e-4` plus absolute `1e-5 rad/s` for angular velocity response; `1e-4 m` and `0.002 degrees` for proxy pose discrepancy.

This proves a short fixed-topology, no-contact vacuum transaction when both native receipt and independent reader pass. Contacts, staging/docking, fuel-flow momentum, long-flight conservation, human control feel, physics warp and complete-game performance remain separate capabilities. Receipt instrumentation and broad managed bridging are diagnostic costs; an FPS claim needs a combined candidate with those costs measured or removed.
