# Capacity lab

This standalone consumer executes generated definitions through the actual Core
topology, retained world banks, systems, builtin host and binary checkpoint code.
It does not load KSP, render assets or solve contacts. Start with 4,096 authored
parts; independently vary physical bodies, rigid regions and provider activity.
An authored alias is an identity row, not another mass contributor. All dimensions
must be positive powers of two with `regions <= bodies <= parts`.

Build with `dotnet build tools/KspContinuum.CapacityLab/KspContinuum.CapacityLab.csproj -c Release`.
Run one observation per fresh output directory, without concurrent timed work:

```text
dotnet tools/KspContinuum.CapacityLab/bin/Release/net8.0/KspContinuum.CapacityLab.dll --mode runtime --family station --parts 4096 --bodies 256 --regions 1 --provider wheel --active-fraction 0.125 --throttle 0.25 --steps 16 --delta 0.015625 --strategy reference --output /ABSOLUTE/NEW/wheel-4096
```

Change `--provider` to `engine` for common Fuel-kind mixture routing, or `none`
for the passive thermal/resource contrast. Engine mode keeps one finite point
Fuel reservoir and one EC reservoir per part; wheel mode keeps EC and a finite
momentum reservoir per actuator. `--throttle` is also the wheel attitude demand.
All provider rows remain present; `--active-fraction` changes their enabled state.
The requested active count per region must be integral and paired (except a
single-part region); unrepresentable requests reject rather than round down.
No enabled resource/provider state is silently removed to accelerate a case.

`--state-format legacy|columnar` selects the persistence contract; legacy is the
default for existing recipes. Columnar mode admits explicit research capacity
through 1,048,576 parts and uses generated provenance without native body/process
bindings. It writes bounded segments from the pinned retained bank, restores a
new owner, and compares complete files after rewrite, discarded mutation and
future continuation. Checkpoint files use `.ocss`; no whole-file byte arrays or
legacy per-part checkpoint DTOs are constructed on that path. Limits cover
dimensions, encoded bytes, logical elements and conservative restore allocation.
The receipt separates file writing, disk hashing and read/restore costs.
Canonical restore and rollback checks compare streaming writes to the saved
checkpoint instead of saving duplicate images. The checkpoint and both final
continuation images remain available as raw evidence.

```text
dotnet tools/KspContinuum.CapacityLab/bin/Release/net8.0/KspContinuum.CapacityLab.dll --mode runtime --state-format columnar --family grid --parts 131072 --bodies 8192 --regions 16 --provider engine --active-fraction 0.125 --throttle 0.25 --steps 16 --delta 0.015625 --strategy compiled --deadline-probe true --output /ABSOLUTE/NEW/columnar-engine-131072
```

The new host store supports the actual builtin executable image. Custom schedule
state, scalar supply configuration/accounting, flight-session debt/events and
renderer state are outside this first contract and must be retained separately;
unrepresented custom schedules and scalar supply reject explicitly. Native KSP
sidecars retain their existing strict validation and bounds. Larger numeric
admission does not qualify larger KSP flights. See
[segmented state](../../docs/segmented-state.md) for the wire and ownership contract.

`station` uses a centered line of unit dry bodies; `grid` uses a centered lattice.
Authored parts and point resources alias their body's origin. Parts exchange heat
along a declared path and radiate against a 300 K environment. Region roots are
separated. Engine application points are the region origin with opposing paired
axes, isolating inertial translation while retaining actual thrust, withdrawal,
changing mass/inertia and thermal work. A single-part engine region instead has
actual net thrust and excludes the zero-net-force displacement oracle. This is
an authored workload, not a captured stock engine or spacecraft layout.

`--strategy reference|compiled` selects the existing Core execution strategy;
mechanics uses the original fixed local interval, not adaptive trials. `--steps`
defaults to 16, `--delta` to the binary-exact 0.015625 seconds, and
`--continuation-steps` to 3. These are configurable numerical/workload inputs.
Intervals that exhaust resources or saturate wheels are outside the analytic
rate fixture and fail its unchanged checks rather than quietly losing activity.

The receipt separates fixture/definition/topology/world/system/host construction,
first and subsequent complete committed steps, invariant observation, checkpoint
capture/encode/write/read/decode/restore, discarded-bank checking and both cold
continuation branches. Every stage records ticks, current-thread managed
allocations and process RSS. Core work counters/clocks are nested and nonadditive;
their snapshot ends at the original measured stepping frontier. Complete cost
includes cold observation/persistence and is not a warm throughput measurement.

Checks independently sum unit dry mass plus actual point resource mass, weighted
COM and the full six-component parallel-axis tensor. Balanced inertial motion,
EC withdrawal and wheel momentum use analytic rate/translation checks. A changed
uncommitted bank must leave the complete OFCB packet identical. Restore and the
three-step continuation compare complete binary packets, including resources,
provider state, thermal/mechanical state, controls/history and source bindings.

`fixture.json` binds the authored numeric recipe. Its source-binding hash slots
refer to generated recipe bytes and a declared synthetic body identity ledger;
they are explicitly not KSP/native capture provenance. The receipt independently
hashes the actual loaded tool and Core images. Keep before/after recipes and all
CLI inputs unchanged when comparing substituted Core binaries. Persistence uses
the public imported-runtime checkpoint facade with that generated source ledger.
Current Core definition and codec bounds remain real admission limits; source
body mappings currently cap at 50,000 and payloads at 64 MiB. Refusals preserve
the active stage and completed work instead of truncating rows.

Those source-ledger and payload limits describe legacy mode. Columnar mode has
an independent versioned store and explicit admission budgets. The existing
`--mode compare` command compares legacy packets across builds; it does not
normalize columnar and legacy formats into a shared semantic image.

Spatial mode is a separate consumer of `CapacityBroadphase`:

```text
dotnet tools/KspContinuum.CapacityLab/bin/Release/net8.0/KspContinuum.CapacityLab.dll --mode spatial --parts 1048576 --case separated --output /ABSOLUTE/NEW/spatial-separated
```

`--parts` counts each of two craft, not both together. `--case sparse|dense`
selects sparse candidate overlap or dense adversarial overlap. Explicit
`--maximum-candidates`, `--maximum-pair-tests`, `--maximum-bucket-visits` and
`--maximum-memory-bytes` can bound work. A budget-exhausted result retains its
partial counts/digest and returns exit 2; it is not a successful complete query.
Candidates do not establish narrowphase, contact response or fragmentation.

Runtime receipts use `passed`, `failed`, `unsupported`, `invalid` or `interrupted`;
exit codes are 0, 2 or 130. No output directory is reused or overwritten. Ctrl+C
is observed between complete runtime transactions. Abrupt process termination
during a Core call cannot produce a final receipt; retain the input/runner failure
as incomplete evidence. Larger standalone cases never imply matching KSP load,
stock-provider, rendering or collision capacity.

Add `--deadline-probe true` to measure first/repeated retained resource-boundary
queries at the unchanged initial stamp. Receipts retain certification, fallback,
source stamp, identity and finite prediction bounds; this is not an adaptive
flight or reached-event mission.

```text
dotnet tools/KspContinuum.CapacityLab/bin/Release/net8.0/KspContinuum.CapacityLab.dll --mode compare --baseline /ABSOLUTE/BASELINE --candidate /ABSOLUTE/CANDIDATE --output /ABSOLUTE/NEW/comparison
```

Comparison requires passed receipts, matching actual recipes/controls and
packet provenance. Every complete field is checked after excluding only code
source hash and process identity; a physical temperature change must fail.
Peak memory reported as zero by the process API is recorded as unavailable.
See [measured capacity and replacements](../../docs/capacity-runtime.md).

Use `--mode compare-columnar` for two passed columnar runtime directories:

```text
dotnet tools/KspContinuum.CapacityLab/bin/Release/net8.0/KspContinuum.CapacityLab.dll --mode compare-columnar --baseline /ABSOLUTE/BASELINE --candidate /ABSOLUTE/CANDIDATE --output /ABSOLUTE/NEW/comparison
```

The comparator verifies actual fixture hashes and controls, complete success
checks, each receipt-bound file hash, each envelope digest and the actual builtin
image identity against its producer's Core image hash. It compares the checkpoint
and both continuation images byte for byte with bounded reusable buffers. Only
the admitted executable-image hash and independently verified derived envelope
digest may differ. Every physical, control, resource and history byte remains
covered. It does not restore a foreign executable image or normalize away
physical differences. The executable falsifier changes a massless resource
quantity, reseals all integrity hashes and requires comparison to fail.

See [compiled thermal and mass validity](../../docs/compiled-thermal-mass.md) for
the matched million-part observations, complete-state proof and consumer limits.
