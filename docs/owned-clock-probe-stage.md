# Stock nine-part owned-clock staging

`OwnedClockProbeStage` is an opt-in original-game bootstrap producer. It reads a SHA-bound sandbox template from a separate private save folder and the installed stock 18-part ComSat Lx craft. The first nine payload parts must be, in order: OCTO v2, small battery, antenna, small fuel tank, Spark engine and four fixed solar panels. The lifter and references to its removed parts are omitted from the in-memory ConfigNode; source files stay untouched.

Root launches its private instance with these flags (values use `=`):

```text
--continuum-owned-clock-stage
--continuum-stage-save=PrivateCloneName
--continuum-stage-template=checkpoint
--continuum-stage-template-sha=<exact SHA256>
--continuum-stage-craft=/absolute/stock/Ships/VAB/ComSat Lx.craft
--continuum-stage-craft-sha=<exact SHA256>
--continuum-stage-output=owned-clock-probe
--continuum-stage-report=/absolute/fresh/stage.json
```

The output checkpoint name and report must be fresh. Do not select the separate scale-checkpoint loader during this bootstrap. Root owns launches, compilation and installing the helper.

The producer uses the actual ConfigNode parser, ShipConstruct.LoadShip, the exact original internal `ShipConstruction.AssembleForLaunch` preCreate path, then original ProtoVessel.Save and Game.Save serialization. Assembly SHA/MVID and unpatched assembly seam are admitted cold. The preCreate stock path initializes an actual part graph and serializes actual module/resource snapshots; no fabricated MODULE fields or guessed flight MODULE defaults are introduced. Source module names and actual snapshot module names are recorded separately because installed stock/mod initialization may augment the editor state. The recipe does not claim byte-identical module serialization or general prefab equivalence.

The declared preparation creates a 120km circular Kerbin orbit toward the Sun at the template epoch. It uses original Orbit.UpdateFromStateVectors and the same explicit epoch-Zup/current-Zup conversion pattern as entry preparation. Attitude is deterministic with the selected Sun/tangent basis. The report retains placement operands and solar angles. Physical front lighting and the craft/planet composition remain actual camera/pixel qualification; the stage does not create fake global lights or claim this from save metadata.

Temporary original construction objects are inventoried relative to the pre-load Part census, retired, and observed destroyed before a fresh stock LoadGame/Game.Start enters the staged save. Failure cleanup reports unresolved ownership rather than deleting a foreign root. Readiness requires the actual active nine-part loaded/unpacked vessel and records loaded altitude and orbital Sun angle. `flight-ready` establishes this staging scope, not owned-clock takeover, aggregate physics, timewarp, save/reload or performance qualification. The template and stock craft are rehashed before closing the receipt. Generated saves/reports stay private and outside Git.

The native stage-02 observation constructed and serialized all nine parts but failed during stock load because `ConfigNode.ToString()` wraps an unnamed root in braces. That text is not the file layout produced by `ConfigNode.Save`. The corrected source invokes the exact pinned original `WriteRootNode(StreamWriter)` serializer into a fresh stream, preserving create-new output ownership. It then independently parses the file with original ConfigNode.Load and requires top-level GAME/FLIGHTSTATE, active index zero and exactly one nine-part vessel before LoadGame. It does not add guessed active-vessel metadata. The failed file/receipt remain evidence; the corrected serializer still needs actual native load qualification.

Native stage-03 reached a visible nine-part Flight, but ownership acquisition correctly rejected duplicate zero part flight IDs. Original AssembleForLaunch creates its initial preCreate ProtoVessel inside Vessel.Initialize, then assigns the live parts' flight/mission/launch IDs afterward. Saving that early snapshot retained zero IDs. The corrected producer records both sets, admits the actual original allocator's nine distinct nonzero flight IDs, and refreshes the whole snapshot through original `new ProtoVessel(vessel, true)` after assembly. Saved part IDs must match the original live assigned IDs and order; parent/node references come from the original constructor. No replacement ID generator or direct ID setter is introduced. The stage-03 save/receipt remain immutable negative evidence; changed native acquisition remains the qualification gate.

The same producer invokes original GamePersistence.UpdateScenarioModules on the in-memory template before saving and on the freshly loaded game before Start. This admits stock AddToExistingSandboxGames scenario lifecycle, including the AlarmClock type registry required by the actual Flight UI. The report witnesses successful invocation rather than claiming independent scenario-data equivalence. The template file is still only read and rehashed; its bytes are unchanged.

Native stage-04 closed these construction/load gates: nine loaded/unpacked parts,
distinct nonzero stock IDs in saved order, original root-file readback and scenario
upgrades. The actual initialized module census contained 25 modules. This result
does not establish the separate clock/replacement behavior gates.

An existing validated checkpoint can be reused without construction:

```text
--continuum-experiment-host
--continuum-scale-save PrivateSaveFolder
--continuum-scale-checkpoint validated-checkpoint
--continuum-owned-clock-frame
--continuum-stage-report=/absolute/fresh/camera.json
```

The camera-only path waits for original startup to settle, then uses the original
FREE camera and root-part target. It sets a 60-degree field of view and a distance
derived from the live mesh envelope, with the camera about 20 degrees from the
Sun direction. Its report verifies actual viewport placement and planet/Sun
angles. Native-05 and subsequent runs showed the craft with Kerbin behind it;
these pixels supersede the earlier unhelpful default framing. Camera preparation
does not change the source checkpoint bytes or add a synthetic light.
