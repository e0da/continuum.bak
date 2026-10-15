# Private UnityFS content audit

This standalone Rust tool checks whether a native-engine recompression preserved
the complete decoded payload and archive node directory. It does not rebuild
assets or qualify game behavior. Game files and generated receipts stay outside
the repository.

Run only outside timed KSP workloads:

```text
cargo run --release --manifest-path tools/bundle-audit/Cargo.toml -- \
  --manifest /absolute/private/cache/manifest.json \
  --output /absolute/private/new-content-equivalence.json
```

The tool admits UnityFS version 7 with combined block/directory metadata, inline
or trailing metadata, uncompressed/LZMA/LZ4/LZ4HC blocks, and optional payload
alignment. Unknown flags and formats fail closed. Metadata and LZ4 blocks are
bounded to 16 MiB, LZMA dictionary memory to 64 MiB, archive entries to 65,536,
and decoded payload to 2 GiB per file. LZMA payloads are streamed into SHA-256;
LZ4 payloads are decoded one bounded block at a time. No decoded payload is saved.

Each pair must retain engine/compatibility strings, decoded SHA-256 and length,
and normalized node names, offsets, lengths and flags. Raw artifact hashes and
sizes must match the private cache manifest and remain unchanged through the
audit. Compression and block layout may differ.

The decoder uses independent Rust implementations `lzma-rs` and `lz4_flex`,
instead of the Unity native recompression implementation. Synthetic tests cover
known hashes, changed content/directory, layout differences, malformed metadata,
truncation and corruption. This content proof remains separate from signature
verification of originals and DLC loading, scene, save and mission qualification.
# Native serialized textures

`serialized-textures --output NEW_JSON CONTAINER...` indexes the pinned Unity 2019.4 little-endian, stripped-tree serialized format without starting the player. It records Texture2D object IDs, exact field offsets, mips and validated stream ranges.

`bc7-derive` builds a private streamed BC7-to-RGBA32 derivative from that index. See its usage output for input and output arguments. It retains inline textures, original object IDs, all undeclared container bytes and the original stream prefix. The complete manifest records source/derived hashes, exact changed ranges and stored mip-payload proofs. Two analytic checks cover RGBA channels, mip layout, clipped block edges and malformed payload rejection. The decoder is pinned to `bcdec_rs=0.2.0`; successful byte/layout audits do not prove engine sampling equivalence or a performance improvement.

Keep generated indexes, manifests and derivative game data outside the repository. The research laboratory's `install-native-assets`, explicit `--native-asset-installation` run treatment and `restore-native-assets` operations manage the disposable installation checkpoint.
