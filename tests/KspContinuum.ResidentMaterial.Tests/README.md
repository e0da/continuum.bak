# Resident material codec proof

```text
dotnet run --project tests/KspContinuum.ResidentMaterial.Tests
```

This C# 7.3/.NET 8 console proof uses an original analytic triangle and one sheared, scaled affine instance. Independent fixed byte offsets check the wire layout, signed tangent/UV preservation, separate raw/effective opacity and channel-major SH. Corruption cases cover truncated/trailing payloads, hostile counts/indices, projective/nonfinite state, and topology/count/identity mismatch. Writers must refuse existing files.

The codec validates structure and references. The caller still binds actual payload hashes and producer topology identity; Unity validates texture/shader construction and transactional publication. These proofs do not establish native rendering, material fidelity, stale-generation recovery or performance.

Derive cold reload fixtures from an actual captured generation-zero manifest:

```text
dotnet run --project tests/KspContinuum.ResidentMaterial.Tests -- --snapshots ABS_MANIFEST ABS_NEW_DIRECTORY
```

This mode parses public manifest fields, verifies geometry/state SHA-256 before and after reading, and validates the captured graph. It refuses an existing output directory. It writes unchanged generation 1, generation 2 with every instance translated +3 meters on root-local X and emission red increased by 0.25, wrong-topology generation 3, and a truncated generation-2 file. `fixtures.json` records hashes and change semantics. The original generation-zero file remains untouched for stale-rejection checks; no fixture is published to a running consumer by this tool.
