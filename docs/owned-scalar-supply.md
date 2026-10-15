# Fixed solar supply and probe command loads

`OwnedRuntimeScalarSupply` is a Unity-free numeric operator. It does not mutate
resource banks, call stock resource APIs or execute module callbacks. The consumer
must fold its rates into the same provisional transaction as loads, actuators,
thermal state and mechanics; a rejected interval must publish none of those rows.
Ordered storage routes, capacity clipping, gross supply/demand counters and
readiness provider slots belong to the bank owner.

Fixed flat-panel definitions carry stable module/part identities, a local point
and unit normal, nominal resource rate, acquired efficiency multiplier and launch
epoch, and immutable temperature/age Hermite curves. Curves retain actual acquired
key/tangent values with clamped endpoints. Weighted, looping, discontinuous or
nonfinite source curves require explicit unsupported acquisition. The numeric
operator uses the owned part temperature, not stock skin-temperature parity.

The initial solar source is explicitly a frozen body-relative Sun position with
a maximum 300-second universal-time domain. It is not a parent-chain celestial
ephemeris. The captured unattenuated reference flux is derived from actual solar
luminosity and home flux at the reference Sun distance; stock `vessel.solarFlux`
must not seed it because that value includes shadow and atmospheric attenuation.
Generation varies with panel incidence and inverse-square distance, is suppressed
by spherical main-body eclipse, and includes acquired temperature/age efficiency.
Self-shadow, other occulting bodies, atmospheric transmission, tracking and
deployable animation are outside this initial vacuum treatment. Source expiry
throws rather than silently freezing generation indefinitely.

Probe command definitions retain EC rate, hibernation multiplier/state, enabled
state and a stable readiness provider slot. The declared model reports full
readiness only while enabled, nonhibernating and supplied at least 90 percent of
the requested load (or a zero request). It does not reconstruct CommNet,
crew-dependent control, stock partial-control categories or arbitrary controller
behavior. Capture must admit uncrewed probe command modules and reject resource
types or stateful writers not modeled by the transfer.

The original callbacks must stop writing bank-owned resources before publication.
Original UI/controller code may consume published readiness, rates and controls
on the main thread. Deployment, tracking, transmission, converter/generator
activation or unknown resource writes require an owned state machine or safe
handback. Enabled-state or control changes invalidate deadline views. Capacity
and eclipse transitions are real scheduling boundaries; a full battery does not
mean gross solar generation or consumption was zero.

The focused pure-operator tests passed independent checks of incidence, inverse-square
flux, eclipsed and clear rays, curve interpolation/immutability, command demand
and readiness, invalid inputs and source expiry. Portable supply integration also
passed analytic supply/load balance, accepted versus discarded ledgers,
deadline-probe provider isolation and immutable/atomic configuration checks.
Those fixtures establish the declared fold, not exact stock solar, resource
routing, skin-temperature or gross-balance fidelity on an arbitrary craft.

The acquired ComSat native consumer demonstrates a narrower real integration:
nine parts, four bodies, four resource rows and 25 admitted stock modules, original
fixed callbacks suppressed while the worker owns resource/provider state, and
coherent main-thread probe readiness/solar-rate publication. Native14 exercised
keypad acquisition/pause/save/restore with all 25 module XML rows and retained
timeline debt, followed by short 32x/128x demand and clean autosave handback.
Its source-bound checkpoint includes scalar configuration and bank provider
state. No worker fault, partial publication or pose error was reported in that
session. These records do not independently establish exact stock solar output,
all eclipse transitions or native gross generation/consumption balance. See
[Owned clock flight](owned-clock-flight.md) for the observed scope and remaining
operator, provider and rendering limits.

The cold `OwnedClockProviderCapture.Capture` adapter returns
`OwnedRuntimeSupplyCapturePacket` under schema
`continuum-owned-clock-providers/v1`. It binds the original native part order,
module objects, frame/fixed/universal-time boundaries and source digests. Its
chemical-engine metadata uses the captured native-root thrust frame; solar
geometry is part-local. Positive module IDs are `(partUID << 16) | moduleIndex`;
command readiness keys use `long.MinValue + moduleID`, separated from acquired
wheel keys. Capture admits an actual uncrewed probe vessel. Unsupported providers
remain explicit rows, and idle transmitters require activation handback rather
than a claim that their future resource state has been captured. The adapter
returns detached primitive DTOs, performs no private file write and does not
replace the factory's complete callback inventory/admission check.

## Provisional supply pipeline

`OwnedRuntimeSystems.ConfigureScalarSupply(expected, packet, supplySha256,
originBodyMeters, runtimeEpoch)` binds a cold immutable packet and a fixed
body-coordinate origin to the current frame revision. Rebase requires explicit
reconfiguration. `CaptureScalarSupplyConfiguration` and
`RestoreScalarSupplyConfiguration` preserve that binding; the checkpoint owner
restores its saved configuration revision afterward.

Each physical interval evaluates panel geometry, temperature and age at the
accepted interval's starting state, holds that rate for the interval, credits
same-region ElectricCharge rows in canonical resource order up to their acquired
capacity, and then applies command loads in captured module order. Command
readiness uses the acquired 90% delivered threshold. If the packet has commands
and none is ready, engine, RCS and wheel actuation is disabled for this interval.
The original input record remains intact. Actuator folding follows the command
fold. This is the declared owned model, not stock resource-flow or skin thermal
parity.

While owned, the probe facade publishes command readiness to the original
ModuleCommand state/control-source fields. Real input is gated by that published
authority; unavailable command authority submits zero controls. `IsHibernating`
returns the acquired actual hibernation field, not a stock rails inference from
independent demandwarp. Hibernation toggles hand back before original mutation;
other retained field edits are checked before publication. This does not supply
CommNet or general crew/control parity. The first factory remains vacuum-only
and expires its frozen solar source after 300 simulation seconds.

A solar rate provider key and command readiness provider key are
`long.MinValue + ((long)partId << 16 | moduleIndex)`. Their accepted bank values
are respectively units/second and 0/1. Presentation reads these coherent bank
values rather than asking worker-owned Systems for mutable data. Generated,
credited, spilled and command requested/withdrawn totals are counted only after
the provisional bank publishes; rejected adaptive candidates are excluded.

Configured supply disables the retained constant-consumption deadline
certificate. The disposable source trial includes the same solar, command and
actuator fold, privately copies all provider rows, and uses the existing bounded
64-bisection upper bracket. It detects initially positive command EC depletion
and original actuator frontiers. Solar capacity saturation remains a scalar
clamp and spill; it is excluded from physical deadlines so a charging battery
with a continuous command load cannot create artificial shrinking intervals.
This certification is named `supply-augmented-64-bisection-upper`; it is not a
continuous solar-motion prediction. Source expiry still rejects an attempted
advance, rather than silently reusing the Sun indefinitely.

The portable integration suite passed analytic supply/load balance, accepted
versus discarded interval ledgers, provider isolation during deadline probes,
immutable configuration and atomic invalid configuration rejection. Its full
battery fixture completed twelve .1-second intervals with a continuous command
load, demonstrating that capacity clipping does not introduce a false shrinking
deadline. Native ownership/save/restore observations cover the acquired probe
consumer described above. Powered engine animation, arbitrary providers,
stock-flow fidelity, skin thermal parity and complete native gross-balance
qualification remain open.
