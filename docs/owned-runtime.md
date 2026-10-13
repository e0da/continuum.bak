# Owned runtime and live workbench

Continuum now has an executable simulation independent of Unity objects. The
same C#7.3 implementation runs in the portable laboratory and matching Unity
2019 workbench. Unity presents committed poses; it does not integrate this
runtime's bodies. Rigid assembled craft is the primary model.

Authored parts retain their identities, including physicsless aliases. Unique
physical contributors compile into rigid regions. Retained numeric banks hold
motion, resources, temperature, wheel momentum and input history. A host owns
publication; failed scheduled work leaves the previous committed bank available.
Cold topology changes compile new regions and conserve declared mass and linear
and angular momentum. Joining is inelastic and records removed kinetic energy.

Coarse passes evaluate controls, shared resource pools, changing mass properties,
gravity, aerodynamic/thermal work, wrenches and double-precision rigid motion.
JSON belongs to cold acquisition, debugging and checkpoint export. It is not the
simulation's state exchange format. Read leases preserve committed banks;
bounded reader exhaustion fails explicitly instead of overwriting a reader.

## Real craft acquisition and model boundaries

Set `CONTINUUM_OWNED_RUNTIME_CAPTURE=1` while using the existing native-assembly
capture. The disposable KSP instance exports `manifest.json`, collider geometry,
`owned-parts.json` and `owned-aero.json` at one unchanged native epoch. Resources,
selected stock engine/RCS/wheel parameters, thermal capacity, control frames and
six weighted drag-face areas are acquired facts. Quantities use SI after the
explicit KSP tonnes/kN conversion. Source digests and omissions travel with the
imported runtime.

The implemented actuator schedule uses constant captured vacuum Isp and common
region resource routing. Aerodynamics currently uses an isotropic mean of the
acquired face areas, declared Cd=1, no lift or stock occlusion/Mach curves, and
an explicit heating/radiation model. Fuel is represented as mass at the authored
part origin; wet/dry reconstruction must pass its full tensor checks. Captured
collision geometry is visible, but this consumer does not solve contacts.
Unsupported modules, articulated/servo owners and incompatible native states
remain explicit omissions or admission failures.

Strict import rejects quantities above capacity. An explicit
`acquired-surplus` policy preserves such source quantities and their original
capacity, allows consuming the surplus, and forbids refilling above that
capacity. It does not clamp fuel mass or fabricate a new tank capacity.

## Portable integrated mission

```sh
dotnet run --project tools/KspContinuum.OwnedRuntimeLab -c Release -- \
  --native /private/captured/manifest.json \
  --parts /private/captured/owned-parts.json \
  --aero /private/captured/owned-aero.json \
  --resource-policy acquired-surplus --output /private/new-run
```

The mission exercises real captured resources and controls, declared atmosphere
and physics warp, staging/joining, serialized checkpoint continuation and
executable schedule replacement/rejection/recovery. It retains producer hashes,
raw per-advance costs, complete cold preparation/orchestration costs and the
executed binaries. These costs are not whole-game FPS or a stock parity claim.

## Matching Unity consumer and HMR

The existing private matching-2019 project/build route can include Core sources,
`OwnedRuntimeWorkbench.cs`, `NativeAssemblySceneImport.cs`, current rigid-owner
donors and `BuildOwnedRuntimeWorkbench.cs`. The builder pins the editor revision
and records all C# sources, the fixture shader and actual player assembly hash.
Set `CONTINUUM_AERO_WORKBENCH_PLAYER` to a new private `.app` path and use
`BuildOwnedRuntimeWorkbench.Build`. Set `CONTINUUM_SHADER_GRAPHICS_API=Metal`.

Launch with `CONTINUUM_OWNED_RUNTIME_WORKBENCH_OUTPUT` pointing to a new directory
and `CONTINUUM_OWNED_RUNTIME_IMPORT` pointing to the captured manifest. The
workbench starts paused and publishes a loopback port in `status.json`.
Its visible representation uses the captured collision shapes and a fixture
material; stock textures, a planet backdrop and full KSP UI are separate work.

Space toggles pause; W/S, A/D and Q/E control attitude; I/J/K/L/H/N control
translation; O toggles orthographic projection. The debug socket supports
`status`, `step N`, `input throttle pitch yaw roll x y z`, `pause on|off`,
`vacuum`, `atmosphere density temperature`, `wind x y z`, `warp factor`,
`split connection`, `join connection partA partB`, `save path`, `restore path`,
`reload assemblyPath typeName`, `camera metres`, `ortho on|off`, `poses`,
`capture absolutePath` and `quit`. Commands and checkpoints use fresh private
paths. The control client and schedule examples have their own READMEs.

Native checkpoints use a bounded, schema-bound binary envelope with explicit
nulls, exact IEEE scalar bits and a payload digest. Diagnostic exports remain
readable JSON; Unity's JSON parser does not participate in state restoration.
Use `.checkpoint` for the workbench's binary save paths.

Code reload loads a bounded original schedule DLL against the actual player
assembly, validates its ABI and switches executable code at a committed boundary.
World-bank state stays resident. Checkpoints bind the loaded code identity and
generation; restore rejects mismatched code instead of silently falling back to
builtin behavior. A new process must load the matching schedule first. This is
Mono schedule HMR, not arbitrary mod, native library or AOT code unloading.

Admission callbacks cannot mutate the world or cold system configuration, and
only the host can publish a hosted step. A long socket command reports whether
it started; queued cancellation is distinct from completed execution, so a
client must inspect status before retrying a started operation. Teardown waits
for the owned scene to unload before recording its release.

## Qualification and next integration

The dedicated portable checks are part of the existing xtask check inventory.
The net472 plugin build checks the shared source's compatibility with the actual
KSP references. Native rendering, executable DLL reload, checkpoint continuation
and teardown require actual player evidence in addition to successful builds.

This runtime is a shared implementation and an iteration environment for the
replacement program. Full gameplay still requires contacts/articulation,
staging/docking interactions, broader providers and mod facades, stock visual
content, world/scene transitions and player UI. They should join this authority
and its consumers rather than create another competing pose-only runtime.
