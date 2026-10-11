# Private stock docking seed

This offline C# tool borrows complete serialized `mk2LanderCabin` and first Ready/unlinked `dockingPort2` blocks from an owner-provided Space Station 1 save. It creates two distinct uncrewed two-part vessels with circular 120 km Kerbin orbital elements at the source epoch, separated by an explicit mean-anomaly offset corresponding to approximately 20 metres. It preserves roster/scenario blocks and resources, reindexes reciprocal part attachments, disables saved action groups and the existing root reaction-wheel state, and independently reparses the generated document before publication.

```sh
dotnet run --project tools/KspContinuum.DockingFixture -c Release -- \
  --source /private/SpaceStation.sfs --expected-sha SOURCE_SHA256 \
  --game-data /private/instance/GameData \
  --output /private/new-checkpoint.sfs --report /private/new-recipe.json
```

All five paths/options are required. Input/configuration paths reject symbolic links. Output and report must be new distinct absolute paths with existing parent directories. Source SHA must match; source is rehashed before publication. The report records stock configuration identities, new vessel/part IDs, preserved resources, source/output hashes and orbital estimates. No game source, saves or assets are distributed by this tool.

This is **offline structure qualification only**. Identity saved rotation is surface-relative, not a world orientation; configuration stack axes do not establish the actual model `dockingNode.forward`. Two circular phases do not give identical velocity vectors. The runtime fixture driver must establish daylight, unpacked physics, actual facing node frames, safe nonoverlapping gap, zero lateral/angular relative velocity and controlled axial approach before capture. The zero-crew cabin is an inert physics fixture; ordinary piloting/control authority is not qualified. No native launch or installation occurs here.
