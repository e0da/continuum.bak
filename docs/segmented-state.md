# Segmented owned-runtime state

`OwnedRuntimeStateStore` transfers builtin runtime state through bounded typed
columns. It writes directly from a host-gated committed bank lease; it does not
construct legacy per-part checkpoint DTOs, clone thermal history or stage a full
payload. The caller owns the stream. Output can be nonseekable, including a
verifying stream that compares canonical writes against an existing file.

The public seam is `Write(Stream, Host, Provenance, Controls, Environment, Plan,
StateLimits)` and `Read(Stream, StateLimits)`. Read returns a new Host plus the
retained provenance, controls, environment and plan. Original admission capacity
is stored independently of reader budgets. Default row budgets retain the small
native admission; larger standalone budgets require explicit selection. This is
source implementation and portable verification, not installed KSP capacity.

Generated and numeric provenance bind a source ID, SHA-256 and recipe without
native process/body tables. KSP provenance carries the optional original binding
validated by the unchanged legacy `CopySource` rules, including its 50,000-body
limit. Native sidecars must match actual definition body IDs and renderer part
IDs. The first host scope is the exact loaded builtin executable and its code
generation. Custom schedules and scalar supply are explicitly refused. This
interface accepts builtin Host state only; it has no external session/timeline
input and must not be described as a complete flight execution checkpoint.
Controls and environmental
inputs are caller-declared continuation context, not an inferred stock state.

## Format v1

All fixed numeric fields are little-endian. Doubles and unit quaternion components
retain exact IEEE bits. The stream starts with `ORSCOL01`, version 1, a 32-byte
schema digest, ten dimension counts, segment size and bounded scalar metadata.
Metadata contains provenance, original capacity, stamp, executable/configuration
revisions, mechanical and thermal scalar continuation, input/control orientation,
controls, environment and step plan. Thermal arrays and all large runtime tables
remain direct columns. Native metadata has an explicit small-byte budget.

Schema-ordered chunks contain ID, starting row, row count, width, encoding,
payload length, SHA-256 and payload. The header is 57 bytes. Exact positive-zero
fixed columns use encoding 1 with no payload; negative zero uses raw encoding 0.
The reader keeps dormant thermal histories compact, but hydrates all five arrays
together for pending state or any signed-zero history/aggregate data. Logical
zero extents still count toward row/element limits. The footer binds body byte
count, segment count and the complete envelope hash; trailing bytes reject.

Columns retain authored part definitions, dry and current wet physical members,
point resources and flows, provider reservoirs, topology connections, region
motions, temperatures, chronological input history, actuator mixtures/enabled
state, surfaces, thermal links and every ordered thermal forcing ledger. Derived
mass/property caches are reconstructed through the existing semantic restore.

Limits validate dimensions before large arrays. They separately bound encoded
bytes, logical elements, conservative restore allocation, strings, metadata and
scratch segments. Source capacity has its own hard research maxima. Restore
checks section order/extent/width/checksum and full integrity before constructing
an unpublished world, then uses the original `RestoreSnapshot` physical/history
guards and system configuration admission. Failure cannot publish into a live
owner. Successful restore requires the current exact builtin image; persistence
does not make arbitrary executable state portable across code changes.

## Verification and remaining scope

The nominal portable suite passed 19 checks. Run
`dotnet run --project tests/KspContinuum.SegmentedState.Tests -c Release` for
nonseekable direct writing, canonical rewrite, legacy-v6 versus new continuation,
nonzero provider/resource/thermal work, canceled-bank identity, pending heat,
original capacity retention and malformed/budget refusal. The independent proof
suite also passed checksum-valid semantic corruption, ownership/reentry and
signed-zero witnesses, including pure-heat pending continuation and dormant
signed-zero history padding. The final wire implementation reuses one bounded
segment buffer for writing and one for reading. Variable rows cannot grow that
buffer; a row that exceeds its selected segment budget rejects. .NET 8 span IO
and incremental hashing avoid primitive adapter copies; net472 uses the existing
byte-array ABI. Individual strings, digests and restored rows still allocate.

Isolated capacity receipts passed complete persistence, rollback and future
continuation at 65,536 independent bodies, then 131,072 and 1,048,576 authored
parts with active engines. The million-part family has 65,536 physical
contributors, 128 rigid regions, 131,072 enabled engines and 2,097,152 resource
rows. Its image is 505,433,418 bytes. This proves the new state contract beyond
the old native-ledger, definition and 64 MiB payload boundaries; it does not
establish million-body contact solving or rendered KSP capacity. See
[capacity results](capacity-runtime.md) for complete costs and remaining gates.
