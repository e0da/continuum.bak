# Owned runtime integrated lab

This consumer imports an actual native-body scene and its same-epoch acquired part/resource/module supplement. It executes retained wheel/RCS controls, a declared uniform atmospheric workload, bounded 4× simulation steps, body-region split and join, complete checkpoint serialization/restoration with exact continuation, and a source-bound replacement executable schedule with rejection and rollback controls. It does not invoke KSP or Unity, approximate stock gameplay parity, simulate contacts, or establish FPS.

```
dotnet run --project tools/KspContinuum.OwnedRuntimeLab -- \
  --native /absolute/new-scene/manifest.json \
  --parts /absolute/new-scene/owned-parts.json \
  --aero /absolute/new-scene/owned-aero.json \
  --resource-policy acquired-surplus \
  --output /absolute/new-result-directory
```

Alternatively `--declared-aero ABS_JSON` supplies an explicit array of `OwnedRuntimeCheckpointSurface` rows, with stable part IDs, point/normal vectors, drag/lift/thermal areas, emissivity and heating fraction. The receipt distinguishes this authored input from acquired six-face areas. Acquired areas use the declared isotropic mean-face Cd=1 model, heating fraction .0001 and emissivity .8; source drag-cube/occlusion/thermal parity remains unqualified. Wind is 40 m/s in the atmospheric stage because imported motion starts in an explicitly subtracted native aggregate-COM rest frame.

The default resource policy is `strict`. `--resource-policy acquired-surplus` explicitly admits acquired amounts above the original capacity, retaining both original values. Such initial surplus may be consumed but cannot be replenished above the original capacity. This policy and each affected resource are retained in source/checkpoint metadata; it does not repair or reinterpret the save.

All files must be absolute, regular paths without symlink components. Output is a fresh directory; original inputs are hashed before and after. Failure leaves a distinct failed receipt. JSON reads are bounded to64MiB; collider geometry uses the existing bounded codec and a1GiB file bound.

`advances` records complete `OwnedRuntimeHost.Advance` wall brackets and same-thread managed allocation, including cold first calls. Checkpoint encode/restore, source ingestion, mission orchestration and validation are separately charged or included in the mission envelope. These are headless observation scopes, not exclusive CPU/GPU time or a matched performance treatment. Original/restored continuation executes both worlds and is charged twice. The replacement schedule is a distinct executable owner in this tool's hash-bound assembly with the same declared physical schedule; no external code-download or Unity HMR claim is made.
