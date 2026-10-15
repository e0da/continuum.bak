# Checkpoint streaming

`OwnedRuntimeCheckpointBinary` retains the ORCB/OFCB version 6 bytes and readers
for versions 1–6. It adds:

```csharp
EncodeTo(Stream destination, OwnedRuntimeHostCheckpoint checkpoint)
EncodeFlightTo(Stream destination, OwnedFlightCheckpointData checkpoint)
OwnedRuntimeHostCheckpoint DecodeFrom(Stream source)
OwnedFlightCheckpointData DecodeFlightFrom(Stream source)
```

Destinations must be writable, seekable, empty and positioned at zero. Sources
must be readable, seekable and positioned at zero, containing exactly one whole
checkpoint. Every stream remains caller-owned, including failure paths. A file
consumer should open a fresh file with `FileMode.CreateNew`; an interrupted
encode leaves an invalid envelope, not a committed checkpoint. Seekable
write-only destinations are supported. Nonseekable streams are explicitly
unsupported rather than silently copied into memory.

The writer reserves the 76-byte header, emits and hashes the payload through a
64 KiB buffer, then backfills the original length/digest/schema header. Existing
byte-array APIs use this implementation and make one final `ToArray` copy; there
is no separate full payload array or second envelope staging stream. Header
backfill is not filesystem crash-atomic publication. Consumers still own durable
flush, retention and publication of a completed file.

The reader checks header, size, schema and the full payload digest before
constructing any DTOs. It then rewinds and parses through a separate 64 KiB
buffer, hashing the exact bytes consumed. The second digest and final length
must agree before a checkpoint is returned. A source changed between verification
and parsing is rejected. A failed parse constructs no runtime/world and publishes
nothing, although temporary DTO allocations can occur after the first digest
passes. The caller must still supply a stable source for predictable operation.

Primitive arrays use typed loops for `double`, `long`, `int`, `uint` and `bool`.
Double IEEE bits, including signed zero and NaN payloads, remain exact. This
removes per-element reflection/boxing for these columns. DTO fields and reference
arrays retain the existing explicit whitelist and reflection path.

Arrays with at least 1,024 elements additionally use one lazily allocated 64 KiB
scratch buffer per encode/decode operation. Numeric columns use `Buffer.BlockCopy`
only on little-endian hosts; other hosts retain exact scalar little-endian I/O.
Boolean columns explicitly emit only 0/1 and reject any other decoded value.
Small vector/quaternion arrays do not allocate this scratch buffer. Bulk reads
handle arbitrarily short successful stream reads; they do not assume one read
fills a chunk. Header/digest admission and both payload verification passes remain.

On .NET 8, the buffered payload streams override span reads/writes directly so
primitive `BinaryReader`/`BinaryWriter` operations do not pass through the default
stream span-to-array adapter. These overrides are conditional on
`NET8_0_OR_GREATER`; Unity/.NET Framework keeps the existing byte-array path with
no added `System.Memory` ABI dependency. Complete performance remains a measured
consumer result rather than an inference from this adapter change.

The first streamed implementation reduced allocations in the measured 4,096-part
fixture but regressed encode/decode throughput. The bulk-column change targets
that observed primitive-I/O cost; its performance requires a changed measured
comparison. Neither allocation reduction nor larger successful payloads imply
an installed-game speedup.

All existing bounds remain: 64 MiB payload, 1,048,576 elements in one array,
8,388,608 aggregate array elements, 65,536 UTF-8 bytes per string and depth 32.
Primitive array byte requirements are checked before allocation. Neither these
APIs nor the portable checks establish million-part runtime admission or remove
the checkpoint snapshot/DTO and thermal-bank allocations. Systems admitting
more than 200,000 actuator/surface/link rows while checkpoint restore rejects
them remains a separate contract mismatch.

## Compatibility evidence

With zero arguments, the portable suite checks immutable host/flight golden
digests and integrity without private files. Its output explicitly states that
private old-producer and historical-layout checks were not run. An optional
retained, independently loaded pre-change Core DLL enables those checks; its
source-image SHA-256 is pinned in the test. Full host and flight fixtures
exercise every DTO field, null/reference arrays, exact large integers, Unicode,
signed zero, subnormal, NaN payload and infinity values. The original encoder
produces frozen golden bytes; the streaming producer must match every byte, and
the old decoder must accept the candidate envelope. Historical layouts 1–5 use
the retained producer's field/schema tables and are checked against both readers.
Malformed length/digest/schema/markers/UTF-8, short reads, changing inputs,
nonseekable/nonempty streams, oversize inputs/arrays/strings and borrowed stream
ownership remain independent rejection checks.
Large 2,048- and 65,536-element columns also compare against a manual scalar
little-endian byte oracle, and the retained scalar producer when supplied.
This covers exact double bits, long/int/uint columns and canonical booleans,
including short-read decoding and malformed boolean rejection. Int/uint primitive
checks exercise the codec seam directly; they do not invent new DTO fields.

```text
dotnet build tests/KspContinuum.CheckpointStreaming.Tests
dotnet tests/KspContinuum.CheckpointStreaming.Tests/bin/Debug/net8.0/KspContinuum.CheckpointStreaming.Tests.dll
dotnet tests/KspContinuum.CheckpointStreaming.Tests/bin/Debug/net8.0/KspContinuum.CheckpointStreaming.Tests.dll BASELINE_CORE_DLL
```

The baseline image and generated artifacts remain private inputs. Complete
capture/encode/decode/restore cost is measured by the capacity consumer; these
functional checks make no throughput or installed-flight claim.
