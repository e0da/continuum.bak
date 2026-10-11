# Station capacity fixture

Creates a private 256- or 512-part checkpoint from the SHA-bound 196-part station save. This tool uses only .NET 8 and reads the owner's stock part configurations; it has no game assembly or package dependencies.

```text
dotnet run --project tools/KspContinuum.FlightFixture -- \
  --source /absolute/private/source.sfs \
  --expected-sha <source-sha256> \
  --game-data /absolute/private/installation/GameData \
  --output /absolute/private/new-checkpoint.sfs \
  --report /absolute/private/new-fixture-report.json \
  --parts 256 \
  --title "Continuum Capacity 256"
```

Output and report must be new, distinct absolute paths. The output filename supplies the checkpoint name. Keep saves, stock configurations and generated reports outside Git.

`--parts` accepts only 256 or 512 and defaults to 256. The recipe appends four chains of fifteen or seventy-nine physical `trussPiece1x` girders at free docking ports 9, 110, 113 and 116. Each docking module must be `Ready` with `dockUId=0`. The tool copies simple PART47 metadata, allocates new UIDs across the entire save, and uses the dock quaternion and installed stack node coordinates. It preserves all original part blocks except those four reciprocal top references, and preserves other vessels. Only the game's Title additionally changes.

The 512-part fixture adds 316 physical girders, 39.5 tonnes, and four approximately 88-metre flexible booms. It probes structural graph and solver pressure, not a general module-density or mod-capacity workload. The four original docking modules remain unchanged; new girders use stock defaults with autostruts off and rigid attachment false.

The receipt records source/config/output hashes, ordered additions, node alignment and inventory proofs. Node alignment does not prove collider separation, loading, physical-body/joint counts, stability or performance. New girders inherit current stock prefab module defaults; the original save's modules, crew and resources remain unchanged. Verify those runtime properties through the flight consumer before treating this as a qualified capacity fixture.
