# Whole-installation source atlas

Decompile all distinct managed `.dll`/`.exe` payloads in a private installation
with bounded parallel ILSpy processes. Record duplicate locations, native PE
boundaries, frozen binary identities, source tree hashes, logs and failures.
The installed game is read-only; output must be new and outside it. Symlinks
are excluded explicitly. Input/output ancestors containing symlinks are rejected
so an external alias cannot redirect generated output into the installation.

```text
dotnet run --project tools/KspContinuum.SourceAtlas -c Release -- \
  --instance PRIVATE_INSTANCE --ilspy PINNED_ILSPY --output NEW_PRIVATE_DIRECTORY \
  --reuse-index EXISTING_CORE_INDEX --jobs 3 --timeout-seconds 900
```

`--reuse-index` accepts the existing research-lab index directory only when its
receipt binds the exact source hash, successful pinned decompiler, unchanged
input and a nonempty generated project. The reused source tree is hashed in
the new atlas. Use `self-test` for hash-drift, incomplete-reuse, installation
output and existing-evidence falsifiers.

Subsequent whole-installation maps can use `--reuse-atlas EXISTING_ATLAS`.
Only exact binary identities with a matching decompiler and unchanged generated
source tree are reused. Missing or changed trees are decompiled again. This
avoids reconstructing the entire installation after changing one assembly.
New jobs use separate game/plugin and launcher reference directories. Reused
trees retain their recorded reference policy; an old atlas without that field
is labelled `prior-unrecorded`, not retroactively assigned the new policy.
Legacy single-assembly index receipts lack historical source-tree digests;
their current trees are hashed without claiming they were never edited.

Exit 0 means all discovered unique managed payloads decompiled or reused with
unchanged source. Exit 2 preserves an incomplete atlas; 1 is an invocation
failure. Successful source recovery does not prove source recompilation,
unchanged behavior, reflection closure or native engine reconstruction. This
tool complements RuntimeMap's metadata/IL graph; it does not replace it.
