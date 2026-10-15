# Startup icon construction admission

Status: bounded offline audit; transparent lazy-icon replacement is not admitted. No changed startup, editor operation or speedup is claimed.

The measured owner is `PartLoader.CreatePartIcon`: 479 calls, 0.625729 seconds inclusive and 0.610314 seconds exclusive of instrumented children in the retained `multiscale-presentation-01/startup-part-timing-01.json` observation (run 1791690686-98533, process 98618, clock frequency 10,000,000). The enclosing `ParsePart` cost was 2.547944 seconds, not an additional independent cost. Native construction, callbacks, allocations and instrumentation are included. This historical scope bounds the opportunity; it is not a current clean startup comparison.

## Callback and consumer scope

The matching private managed index contains only the base `PartModule.OnIconCreate` definition and its call from `PartLoader.CreatePartIcon`. The base method is empty. No stock override was found, including the indexed Serenity robotic and ground-science module families. Installed MechJeb2 method metadata contains no `OnIconCreate` declaration. This establishes a useful narrow callback fact, not that every component on a cloned icon is inert or that arbitrary mods are supported.

The producer clones the full part object, activates the clone, invokes module icon callbacks, then strips gameplay components and prepares bounds/materials. Cloning/activation can execute Unity lifecycle callbacks before stripping. In particular, `PartModule.Awake` initializes its resource handler and invokes virtual `OnAwake`; the absence of an icon override does not prove these effects are empty. Deferring the full clone changes when those original effects occur. An eager clone followed by deferred stripping retains that lifecycle but may retain most of the measured cost.

Consumers expose a stronger compatibility boundary:

| Owner | Actual dependency | Admission implication |
| --- | --- | --- |
| `AvailablePart.iconPrefab` | Public `GameObject` field | There is no getter through which every existing consumer must request readiness. |
| `PartLoader.CompileParts` | Registers the completed icon in `APFinderByIcon` | A null icon cannot be substituted: dictionary key insertion requires a real object. |
| `PartLoader.getPartInfoByIconPrefab` | Reverse lookup by original object identity | A placeholder must not silently change the lookup contract. |
| `AvailablePart.iconRenderers` | Lazily enumerates the public icon object, then retains the array | Publishing a placeholder creates stale cached renderer identity. |
| `EditorPartIcon.InstantiatePartIcon` | Directly instantiates the public icon | A readiness interception must precede this operation. |
| `PartListTooltipController` | Instantiates the icon and uses it as a dictionary key | Tooltip identity and lifetime are part of publication. |
| `CraftThumbnail` | Directly instantiates the icon, including a flight branch | Editor entry alone is not a complete demand boundary. |
| Part-loader clone/disposal paths | Clone existing icons and destroy completed icon objects | Construction, aliasing and disposal must understand pending obligations. |

Private source anchors, relative to the matching managed index: `PartModule.cs:1880–1887,2126`; `PartLoader.cs:3025–3036,4246–4255,6270–6339`; `AvailablePart.cs:96,156`; `KSP.UI.Screens/EditorPartIcon.cs:2019–2025`; `KSP.UI.Screens.Editor/PartListTooltipController.cs:323–355`; `CraftThumbnail.cs:356`. These references describe observed contracts; recovered game source is not included here.

## Supported and unsupported split

**Supported offline conclusion:** inherited empty stock icon callbacks can be identified exactly, and stock consumer sites are discoverable. No useful family is yet proved safe for transparent whole-icon deferral. Neither MechJeb's lack of an override nor an empty `OnIconCreate` proves clone lifecycle equivalence.

**Unsupported:** null public icons, fabricated placeholder geometry, moving unknown callbacks to editor entry, silently replacing public field reads, DLC/mod-wide safety inferred from callback names, or treating the entire PartLoader envelope as removable icon work.

A future bounded stock-owned consumer could hold a typed obligation keyed by available-part identity and recipe generation:

`Declared -> Constructing -> Ready | Failed -> Retired`

`EnsureIcon` would run original construction once on Unity's owning thread, publish the complete object and reverse lookup atomically, and only then allow renderer caching or instantiation. Reentrant requests must fail explicitly; failed construction must dispose its own partial objects and retain the original eager fallback. Dependencies include part/model/material state, variant, all component runtime types and callback identities, current shader/backend recipe, and icon-only transforms. No serialized live module object is a prepared asset.

This design is viable only after replacing every admitted stock raw consumer and explicitly excluding external raw readers, or after preserving the eager clone lifecycle and proving a useful remaining representation-only cost. Unknown consumers retain eager production. It is a capability-boundary redesign, not a patch to skip `CreatePartIcon` globally.

The next resolving action is an offline component/lifecycle admission for an actual candidate family, including inherited and component `Awake`/`OnEnable`, and a map of its raw icon readers. Only if that establishes a nonempty useful supported family should a changed startup/editor demand observation be scheduled. Current audit resolves the broad transparent proposal negative; it does not justify another identical startup timing run.

## Next loading-kraken situation: docked reload followed by undock

This is a proposed recipe, not a runnable catalog capability beyond the existing separate drivers.

Use the existing explicit two-vessel, two-part-per-vessel daylight fixture. Complete real stock docking first. Freeze the checkpoint only after one four-part vessel, stable reciprocal port state and an actual docking joint are observed. Preserve the immutable seed and declare the merged checkpoint as a descendant of that stock docking receipt.

Run the actual stock quickload producer with the separately qualified packed mass-owner treatment. Require fresh vessel, part and module objects with the same four flight IDs, names, parent graph, reciprocal docking IDs, port state, resource totals and whole saved membership. Retain the zero-crew limitation. Reacquire both ports from new owners; never keep old module references as an oracle.

Then invoke original `ModuleDockingNode.Undock()` and require two disjoint two-part vessels with the original partition, no cross-vessel docking joint, reciprocal ready port state and measurable separation. Original physics, controllers and vessel events remain owners. Source/checkpoint bytes remain immutable; input, camera and initialization hooks must clean up across both topology transitions. Stock event timing is evidence of the short situation, not exclusive CPU or improved FPS.

Frozen reload gates remain 1 cm saved-epoch position and 0.00001 m/s velocity, with existing resource/primitive-field tolerances. Record packed mass publication and first physical orbit transition. Do not widen numerical gates for docking-induced drift. Abort on preexisting unexpected merge, missing joint, nonunique port identity, unsupported controller activity, resource budget violation or stale owner.

Primary private lifecycle anchors: `ModuleDockingNode.cs:5022–5105,6859` and `Vessel.cs:6193–6227`. The merged checkpoint has one physical vessel and fits current reload admission; the subsequent split needs explicit topology-aware diagnostic wiring. Current separate docking and quiet-reload successes do not establish this composed capability.
