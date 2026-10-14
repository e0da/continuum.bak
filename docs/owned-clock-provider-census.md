# Provider census for owned flight time

This census reads the existing acquired 512-part packet; it does not reacquire
game state. Part-spec schema is `continuum-owned-runtime-part-specs/v1`, run ID
`1791925988-35022`, content SHA-256
`cf5fe397edb186a5c05b12e14abe72d26c92a58dddba36e366319898dbdaa051`.
The native ledger has 426 distinct bodies. The acquisition names stock assembly
SHA-256 `8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27`
and MVID `10657063-2fc3-43a7-84fa-d39e75e877bf`.

| Acquired bank | Rows | Initial amount | Original capacity |
|---|---:|---:|---:|
| ElectricCharge | 33 | 3465.36 | 3465.36 |
| LiquidFuel | 10 | 2865.4100904464717 | 3370 |
| Oxidizer | 9 | 3168.274749755859 | 3630 |
| MonoPropellant | 10 | 1134.1233331617225 | 0 |
| IntakeAir | 2 | 0 | 4 |

Amounts above are summed acquisition facts, not a flow-equivalence claim. The
positive zero-capacity MonoPropellant rows require the existing explicit
acquired-surplus policy; their original capacities must not be corrected.

The packet contains 48 RCS actuator rows and 11 wheel rows, all initially enabled,
plus six engine-transform rows, two enabled. Those two belong to one stock engine
module, not two independent engines. Acquired engine parameters include nominal
thrust, current authority, vacuum Isp, mixture ratios and thrust transforms.
They do not acquire the complete engine response state or environmental curves.

There are 42 distinct omission kinds. Particularly consequential omitted writers
are six `ModuleDeployableSolarPanel`, six `ModuleCommand`, four
`ModuleAlternator`, five `ModuleLight`, and six external MechJeb core modules.
Control surfaces, docking, intake and other stateful modules also remain in the
inventory. Crew/roster state is absent from both part-spec and native-body DTOs;
crew count or faithful crew continuation cannot be inferred from this packet.
The capture producer is [OwnedRuntimeKspCapture](../src/KspContinuum.Plugin/OwnedRuntimeKspCapture.cs).

## Missing provider state and authority

Pinned stock `ModuleDeployableSolarPanel.PostCalculateTracking` derives its output
from line of sight, panel orientation/shape, solar distance or vessel flux,
skin-temperature efficiency, age since `launchUT`, efficiency adjustments and
submersion effects. `FixedUpdate` establishes launch time and advances the
deployable/tracking owner. `ModuleResourceHandler.UpdateModuleResourceOutputs`
uses the stock fixed delta and a negative resource request to fill storage.
Capturing a nominal charge rate alone does not preserve this provider.

`ModuleCommand.FixedUpdate` updates control readiness; its resource check applies
normal or hibernation rates and changes control capability when supply is absent.
`ModuleAlternator.FixedUpdate` is another generation owner. Lights and transmitter
activity can add demand. Engine fixed updates retain requested/current throttle,
response and ignition/flameout state, apply environment/adjusters and withdraw
propellant. Wheel response and finite momentum, RCS activation and mixture state,
and solar deployment/tracking require stored histories with explicit revisions.
The present acquired static actuator recipe is therefore not a complete provider
handoff, even where its instantaneous force happens to match.

An owned-time transfer must select one writer per resource and provider domain.
Numeric supply, loads and actuators operate in the same provisional bank and
publish with accepted mechanics, thermal history and clock. Original module
resource callbacks must then stop mutating those bank-owned rows; merely copying
owned amounts back while leaving callbacks active double-counts work. Main-thread
UI and admitted controller callbacks can remain consumers of a committed view,
but a module's visual callback must be separated from its resource/state writer.
Unknown writers require explicit unsupported admission or handback, not frozen
zero demand/generation.

## Small stock family

The installed stock **ComSat Lx** orbital payload is a useful first family:
Octo probe core, battery, antenna, small LiquidFuel/Oxidizer tank, Ant engine and
four fixed OX-STAT panels, nine payload parts. The full supplied craft has 18 parts
including its launcher. The fixed-panel config specifies 0.35 EC units/second,
nontracking and nonbreakable; this removes deployable-tracker dynamics from the
first solar treatment without inventing generation. Preserve a source digest and
explicit payload selection rather than calling a stripped authored seed the
unchanged stock craft. Probe command drain/control readiness and finite wheel
state still require real models. This family does not provide RCS or crew.

**Orbiter 1A** is a larger stock follow-up with 34 parts, crew-capable pod,
reaction wheels, RCS, chemical engines, fixed solar panels and three fuel cells.
The fuel-cell converters must be modeled or explicitly excluded through a
declared craft treatment. Disabling them silently is not a complete ownership
transfer. Ion-Powered Space Probe instead adds RTG generation and EC-consuming
propulsion; it is not the simplest resolving solar case. No DLC requirement is
needed for the first fixed-panel chemical-engine family.

## Proposed portable supply contract

An immutable supply definition should bind stable part/module IDs, resource kind,
nominal rate, physical panel point/normal, efficiency inputs and source definition
hash. A retained provider bank should contain launch epoch, deployment/tracking,
engine response/ignition, wheel and command-readiness state. Cold acquisition must
record these facts; configuration remains separate from current mutable history.

An original scalar supply operator can calculate a declared physical panel cosine
and spherical-body eclipse from committed body-centred geometry, then distribute
generated EC through explicit ordered eligible storage routes. Capacity overflow,
empty storage, eclipse entry/exit, control changes and provider transitions are
causal boundaries. Initial support may use fixed panels, acquired distance and
efficiency inputs, spherical eclipse and explicit supply-before-consumer order.
Self-shadowing, tracking deployment, unknown efficiency adjusters and arbitrary
stock crossfeed remain unqualified until represented; this is a replacement
model, not a claim of exact stock equations or callback ordering.

The first end-to-end gate should exercise real original UI input, a burn and
cutoff, attitude rotation through panel incidence, light/shadow power balance,
command readiness, pause/warp, save/load continuation and safe handback. Readbacks
must distinguish gross generation/consumption from net battery change. Actual
craft, planet, scientific camera controls and original UI must remain visible.
Use the existing source-bound scene capture, sunward preparation/inspection
shots and lifecycle receipts; these reusable paths do not qualify global time
ownership by themselves. No game launch, build or new numerical test accompanies
this census.
