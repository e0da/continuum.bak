# Installed runtime map

This offline tool indexes an installed binary tree without loading game code into
the CLR, launching the game, changing binaries or repeating source decompilation.
It follows the Cecil metadata inspection precedent in `KspContinuum.Reconstruction`.

```text
dotnet run --project tools/KspContinuum.RuntimeMap -c Release -- map INSTALLED_ROOT NEW_PRIVATE_OUTPUT
```

The output directory must be new and outside the installation. Generated output
and input paths must have no symlink or reparse-point ancestors. Path admission
occurs before output creation. The isolated boundary check uses a new private
fixture directory and does not inspect or modify the game installation:

```text
dotnet run --project tools/KspContinuum.RuntimeMap -c Release -- self-check-paths NEW_PRIVATE_FIXTURE
```

Generated output
belongs in private research storage. Inputs are hashed before and after indexing;
the manifest records producer identity and every ledger hash. A nonzero exit
retains errors where the failure occurred. `completed` means metadata indexing
completed with unchanged inputs, not game reconstruction or runtime qualification.

The ledgers are newline-delimited JSON:

| File | Contents |
| --- | --- |
| `assemblies.ndjson` | Installed paths, SHA, MVID, CLI identity, exact dependencies, resources, native references |
| `types.ndjson` | Base/interfaces, attributes, generic parameters, Unity-component and serialization candidates |
| `methods.ndjson` | Tokens, raw IL hashes, signatures, implementation flags, exception regions, async/iterator state machines, Unity message candidates, structural instructions and boundary calls |
| `fields.ndjson` | Static/read-only/literal facts, attributes and persistence candidates |
| `call-edges.ndjson` | Exact declared method targets or unresolved reason, virtual/delegate/indirect dispatch obligations, reflection and component lookups |
| `field-accesses.ndjson` | Declared reads/writes/address escapes, including static fields and unresolved targets |
| `native-binaries.ndjson` | Native PE headers or Mach-O architectures, UUIDs, dependencies and segment section counts |
| `overview.json` | Counts grouped by game, plugins, engine-managed, framework and launcher; Unity message-name counts |
| `errors.ndjson` | Indexing errors, distinct from intentionally unresolved graph edges |

Definition IDs include the source SHA, module MVID and metadata token. Byte-identical
copies are canonicalized within a loader environment while their paths remain
inventoried. Game engine/managed libraries plus GameData plugins and the launcher
have separate resolvers. A launcher library cannot create artificial ambiguity
in the game's dependency closure. Exact CLI assembly identities are required;
missing version bindings and ambiguity within a closure stay explicit. Runtime
binding redirects are not guessed.

Resolved calls identify the declared method, not the actual virtual implementation
or a live invocation. Unity message names and serializer attributes similarly
identify candidates. `newobj` includes value-type construction; instruction counts
are not heap allocations, executions or timings. Backward branches can include
obfuscation-shaped control flow rather than useful algorithmic loops. Methods
without IL include abstract/interface declarations as well as internal/native
methods; use their implementation flags and P/Invoke rows to distinguish them.

Mach-O inventory is not native source recovery. Unity, Mono and PhysX native code
requires distinct symbols/disassembly, ABI and platform evidence; a matching
engine-source/toolchain build or a deliberate replacement is a separate outcome.
Existing managed decompiled sources may be searchable without being compiler-ready.
