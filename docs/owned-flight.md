# Owned central-body flight

The captured-craft runtime now drives orbital and atmospheric flight from one
retained authority. The portable mission laboratory and matching Unity/Metal
workbench use the same world, dynamics, controls, topology and checkpoint code.
The workbench displays actual captured craft geometry/textures and a captured
planet surface. Original game assets and acquisition packets remain private.

## State and execution

`OwnedFlightWorld` admits immutable, source-bound central-body facts: SI gravity,
radius, spin, atmosphere samples, captured inertial placement and Sun direction.
`OwnedFlightSession` owns the body-relative origin and the mapping between the
retained runtime clock and universal time. Hosts receive per-region gravity and
rotating atmospheric flow as typed samples; the hot field does not serialize,
copy an entire vessel or make socket requests.

Local stepping handles controls, resource grants, wet mass/inertia, acquired aero
surfaces, thermal state and rigid motion. Its finite-size spherical surface guard
checks the actual post-mass-refresh integration inputs before publication. Failed
steps preserve the committed bank. Terrain, contacts and flexible joints need
their own models; the spherical guard does not implement those systems.

Passive Kepler coast is a different declared execution regime. It requires zero
controls, chassis spin and retained wheel momentum, an explicit decision to
freeze passive resources/thermal state, and an executable schedule implementing
`IOwnedRuntimePassiveKeplerSchedule`. It rejects atmosphere/surface crossings,
pending-event overruns and unsupported event brackets. Replacing local physics
does not silently authorize bypassing it during coast.

## Predictions, controls and events

Predictions fork a complete cold checkpoint and run the admitted local or coast
model. They have bounded work/time, cancellation, explicit failure/incomplete
status, and source/state/code/configuration stamps. Reading a prediction never
moves the authoritative craft.

Maneuver impulses are declared engineering events, expressed as inertial delta-v.
A future passive maneuver prepares the entire coast and publishes coast plus
impulse once. It does not consume propellant or claim to model a finite burn.
Any intervening state/code/configuration change requires replanning.

Guidance derives nominal authority from the actual captured actuator axes,
available resource pools, current mass and inertia. Reaction-wheel authority is
directional at saturation, so a wheel can unwind. Actual grants and contention
remain owned by the systems pass. These controllers are research policies;
stock control feel and broad module compatibility remain unqualified.

## Checkpoints and reload

`OwnedFlightCheckpoint` includes physical banks, definitions, resource policy,
controller targets, world facts/origin/clock/mode, pending maneuver, source
bindings and executable schedule generation. `OFCB/v1` preserves IEEE64 payloads,
integers and nulls with fixed typed-schema and payload digests. `ORCB/v1` retains
its existing schema and framing.

Restoration prepares a separate owner before the consumer switches references.
Pending nodes bind to the restored exact source. Predictions are disposable.
The builtin schedule is pinned to its actual assembly digest; custom schedule
images are hashed by the loader. A checkpoint from a different executable is
rejected. Deliberate migration between implementations requires an explicit
validated migration path rather than an implicit restore.

HMR replaces an executable schedule while retaining the live banks and visual
assets. Admission, execution, publication and coast eligibility are separate.
The example forward schedule supports both field execution and passive Kepler;
the gravity probe changes local physics and deliberately lacks coast eligibility.

## Visible consumer

`OwnedFlightPresentationBindings` converts same-epoch actual renderer transforms
into retained part-local affines. Committed region poses update cached matrices;
camera motion changes a presentation anchor. Meshes, textures and draw packets
stay resident across flight, origin rebases and checkpoint restoration.

The workbench composes independent positive-eye-depth craft and planet domains.
It supports perspective/orthographic cameras, root/COM/body focus, Sun/quarter/
ship/radial/orbit-plane views, world-resolution scale, scientific root/COM grids,
metre rings, prediction paths and geometry/depth debug views. UI overlays remain
outside the scaled world render. Overlays are unoccluded scientific annotations.

Appearance currently uses captured MainTex/ST/tint/vertex colour and approximate
opaque recipes. Original shader, alpha, shadow, specular and property-block
fidelity are not established. The acquired planet texture has one mip level;
seeing a globe or an ocean patch does not qualify filtering or distant detail.

## Running the loop

The [owned flight laboratory](../tools/KspContinuum.OwnedFlightLab/README.md) accepts
matching native/parts/aero/world packets and runs without KSP. It records original
placement separately from engineered circular/atmospheric scenarios, predicts,
applies a node, operates actual wheel/RCS actuators, enters atmosphere, rebases,
restores, replaces code and unlocks/re-locks an authored connection.

The existing Unity builder retains the multiscale shaders when
`CONTINUUM_MULTISCALE_WORKBENCH_BUILD=1`. Set the existing workbench output/import
variables plus `CONTINUUM_OWNED_FLIGHT_WORLD` and
`CONTINUUM_OWNED_FLIGHT_VISUALS` to matching acquired inputs. The
[control client](../tools/KspContinuum.OwnedRuntimeControl/README.md) sends live
commands to the loopback port recorded by the player.

Useful commands include `orbit 200000`, `atmo-flight 52500 0 1200`,
`coast 60 freeze-passive`, `forecast 1200 coast`, `forecast 2 local`,
`node 15 0 1 0`, `execute-node freeze-passive`, `clear-node`, `pilot attitude`,
`pilot velocity`, `pilot manual`, `view plane`, `focus body`, `ortho on`,
`camera 3000000`, `grid root`, `grid com`, `grid off`, `scale 0.5`,
`debug craft-world-normals`, and `debug body-linear-depth`.
Orbital preparation explicitly zeroes chassis spin and requires empty provider
state; it retains acquired resources. It is a treatment, not the captured orbit.

The current proof covers real acquired 512-part input, analytic world/orbit/flow
and saturation checks, source rejection, exact checkpoint/node continuation,
local atmosphere and inspected Metal frames, executable field replacement and
failure recovery. It does not establish full KSP FPS, stock mission equivalence,
terrain/contact, solar-system transitions, articulation, broad mods or an
independent complete game. Central gravity is applied at each region's COM;
tidal gravity, solar absorption and time-varying solar ephemerides are not modeled.
