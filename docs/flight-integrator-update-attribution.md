# FlightIntegrator Update attribution with accepted query owners

This opt-in diagnostic brackets six declared `FlightIntegrator` entry/exit seams under the exact reported installed variant. It ranks the remaining thermal and occlusion work while the accepted aero and convection query owners remain active. It does not benchmark clean FPS. Parent and child rows are inclusive, overlap, and must not be added together.

The stock assembly SHA-256 is `8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27`, with MVID `10657063-2fc3-43a7-84fa-d39e75e877bf`. The assembly byte pin covers the original method bodies; `seamOrder` also retains each original IL digest.

| Declared method | Metadata token | Parameters |
| --- | --- | --- |
| Update | 100677458 | none |
| ThermoPrecalculate | 100677459 | none |
| UpdateOcclusion | 100677505 | bool |
| UpdateOcclusionConvection | 100677506 | none |
| UpdateOcclusionSolar | 100677507 | none |
| UpdateOcclusionBody | 100677508 | none |

## Installation and cleanup contract

Create the convection query and aero query owners first. Optional `CylinderOcclusionCapture` installs its exact solar/body capture hooks next. Then construct `FlightIntegratorUpdateAttribution(report.runId, aeroStepContext, convectionReplacement, cylinderOcclusionCapture)` and call `Start()`. Reject an unavailable observer before beginning capture. Broad fixed and Update callback observers remain excluded. Aero verify/compare and convection verify are not admitted alongside this observer.

The aero reachable graph has no overlap with these six targets. Its existing frozen ownership admission is audited at the four observer boundaries; no new aero hook or per-root graph scan is introduced. If a future graph overlaps, admission fails. The convection owner grants one explicit lease to this exact observer instance. Its existing seam check admits only its original Normal-priority prefix/transpiler/finalizer plus this observer's exact registered prefix/finalizer. A matching owner string with different callbacks, ordering, counts or target identity fails admission. Optional cylinder capture admits only its exact Normal-priority solar/body prefix/transpiler/finalizer plus the same observer subset.

The observer prefix is void, priority First, explicitly before the convection and cylinder owners. It starts before their bookkeeping and never suppresses execution. The observer finalizer is priority Last, explicitly after those owners, and returns the received exception unchanged. Thus candidate convection rows include the replacement admission, shadow copy, fold, publication, original FX and replacement hook cleanup; they do not represent execution of the skipped original scalar fold. Solar/body rows include their optional tape-copy instrumentation. Unexpected original exceptions invalidate the diagnostic.

Audit the PlayerLoop while it is still installed, then dispose and validate the narrow observer **before** cylinder capture, aero or convection cleanup. Dispose cylinder capture next, followed by aero and convection; remove the PlayerLoop after the observer. The observer removes only its own patches. It must restore all preinstalled graphs exactly. Removing a coowner early invalidates the observer evidence; it does not authorize reconstruction or removal of that owner's hooks.

## Raw evidence

The existing schema remains `continuum-flight-integrator-attribution/v1`. Added fields are strings `runId`, `variant`, `patchGraphWitnessStatus`, and string arrays `knownOwners`, `preinstallPatchGraph`, `installedPatchGraph`, `precleanupPatchGraph`, `restoredPatchGraph`. No additional nested serialization types are introduced.

Each graph descriptor binds target assembly/MVID/declaring type/token/original IL and patch category/owner/callback identity and IL/priority/before/after/index. The arrays also include the complete qualified aero graph when present. Successful `patchGraphWitnessStatus` is `passed`; preinstall must equal restored, and installed must equal precleanup. Failed admission retains the full exception in `detail`. Final validation also requires owned cleanup, six patched methods, valid raw-to-aggregate witness, both phase denominators, all six Update seams reached, and zero errors, exceptions or drops.

Update and Fixed windows have distinct denominators. Only windows fully enclosed by the Probe capture timestamps support capture means. Same-method outermost timing removes that method's recursion alone. Zero-call Fixed rows retain the explicit no-coverage category. The observer has bounded storage for 4,096 lifetime phase windows. Hook tax and optional tape-copy tax remain intrusive and unqualified; admission, graph hashing, storage, export and validation occur outside the method samples.

## Resolving checks owned by the integrator

Before one changed native diagnostic, build the integrated source with the cylinder lane and check these bounded counterexamples:

1. Exact query/capture graphs and callbacks admit; an extra foreign callback, a same-owner wrong callback or changed before/after ordering rejects. Preinstalled hooks without the exact bound owner object reject. Original MVID/token/signature and assembly-byte pins stay mandatory.
2. Normal cleanup restores byte-identical descriptor arrays. Early coowner removal or an extra hook inserted after admission makes the lifecycle witness invalid while cleanup removes only the observer owner. A partial installation failure removes its already-installed hooks and cannot report a passed graph witness.
3. A sentinel original exception remains the same exception object through each finalizer, and the observer counts it as invalid evidence. Its void First prefix still executes when the admitted replacement selects its candidate path.

The changed diagnostic must retain the exact source/helper/profile bindings, four graph snapshots, PlayerLoop integrity and fully enclosed raw windows. Use it to choose the next computation owner; it does not establish replacement correctness for the solar/body work or a full-frame performance gain.
