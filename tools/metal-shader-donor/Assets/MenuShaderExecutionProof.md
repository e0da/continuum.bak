# Ordinary-camera menu shader proof

`MenuShaderExecutionProof` starts only when `CONTINUUM_MENU_SHADER_PROOF_OUTPUT` is declared. Copy its C# source into the existing matching workbench project's `Assets` before rebuilding with `BuildAeroFieldWorkbench.Build`. No shader cook is needed. Configure the workbench's normal field bundle/output as usual, omit its automatic smoke-exit option, and launch the rebuilt player with `-force-metal` plus:

- `CONTINUUM_MENU_SHADER_PROOF_OUTPUT`: new absolute output directory.
- `CONTINUUM_MENU_SHADER_PROOF_DONOR`: original matching Metal menu bundle.
- `CONTINUUM_MENU_SHADER_PROOF_GRAFT`: new bundle produced by `transplant` from the derived `sharedassets2.assets`, selecting `Sprite/Simple Texture (Unlit)` and replacing that same donor shader.

The proof explicitly loads the shader asset rather than looking it up by name globally. It sequentially releases bundles because transplantation retains their container identity. Both stages use one identical white-textured, white-colored quad, a forward orthographic camera, layer 30, and a 64×64 ARGB32/depth-24 target with MSAA disabled. Only this isolated camera target is evaluated; the workbench's normal scene is not the compared image.

`shader-execution.json` binds actual Unity/backend/PID, player assembly and before/after bundle hashes, supported flags, center RGBA, occupied pixel counts, PNG hashes, and owned cleanup. The frozen gate requires center RGB ≥0.9, alpha error ≤1/255, and over 1024 occupied pixels. `donor.png` and `graft.png` retain the complete target images. Exit zero requires both stages passing and cleanup. The distinction between donor-only success, both failing, and both succeeding remains visible in individual fields even when the operation exits nonzero.

This tests ordinary camera execution of one unlit donor/grafted shader. It does not prove menu composition, MSAA behavior, other shader families, source visual fidelity, or performance.
