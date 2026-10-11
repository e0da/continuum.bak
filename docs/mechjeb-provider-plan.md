# Ordered MechJeb provider consumer

Opt-in `--continuum-mechjeb-provider-plan` compiles ordered module references and nine CLR type bindings for the installed MechJeb `AnalyzeParts` method. The existing `VesselState` remains authoritative. Its arithmetic, module enablement checks, part/module order, engine grouping and provider/extension callbacks remain original instructions. No provider is replayed for parity.

The pinned MechJeb assembly is SHA256 `6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929`, MVID `30fbe3c4-e77e-4abc-895b-a635d5924a08`; method token `100665304`, IL SHA256 `4abbb739651c412f89141209b17bf46213069d599c45dea10dd463baad7c175b`. Source reconstruction stays private.

Only one original part indexer, one module indexer and fifteen `ldloc.s 13 / isinst` pairs change. Indexers still retrieve the original live object exactly once. The pairs load already typed references directly; they introduce no helper call, cast or counter per classification. The IL witness checks all other opcodes/operands, typed stack results and branch/exception metadata.

Membership validation occurs at original retrieval, without another full traversal each step. Owner/part-list count or identity changes rebuild before invocation. A changed part/module reference, list identity, count or order selects conservative live classification and marks the catalog dirty for the next invocation. Live enablement/resource/force values are never cached. A cursor holds its own immutable generation. Thread-local depth includes every invocation, including non-active vessels, so deeper calls cannot borrow an outer call's busy primary cursor. Finalizers restore the previous cursor, typed entry and depth. Unknown admission or IL seams leave the original method unpatched; an unavailable catalog falls back without replay. Unsupported non-active-vessel invocations use uncached typed entries.

`--continuum-mechjeb-provider-plan-timing` adds intrusive monotonic timings for analysis, catalog rebuild and retrieval validation. Default treatment has no hot clocks; report cache/fallback counts aggregate per module retrieval. Costs overlap and must not be added. `FinishForFlight()` removes owned hooks and writes nonce/PID-bound `PluginData/mechjeb-provider-plan-RUNID.json`, schema `continuum-mechjeb-provider-plan/v1`. Native qualification must require installed/pinned seam evidence, positive calls/cache coverage, zero errors, and removed hooks; completed-experimental is not numeric equivalence or a speedup claim.

Focused offline checks:

```text
dotnet run --project tests/KspContinuum.OrderedProvider.Tests
dotnet run --project tests/KspContinuum.OrderedProvider.Tests -- --inspect ABS_MECHJEB_DLL
dotnet run --project tests/KspContinuum.OrderedProvider.Tests -- --il-proof ABS_MANAGED ABS_MECHJEB_DLL ABS_BUILT_PLUGIN
```

Checks cover live-value changes, membership/order/count/identity drift, frozen caller input and generation bounds. The actual assembly proof verifies rewritten instructions and typed bindings, rejects an unknown type seam, and exercises the actual forwarding wrapper with a same-count module replacement. It also exercises active-to-nonactive-to-active reentrance with a rebuilt inner generation and proves the outer generation and context are restored. It never invokes providers. Native execution, complete-workload benefit, unknown mod mutations and long-play/save behavior remain unqualified. The next resolving observation is a treatment-only 512-part complete flight; the classification cost removed may be smaller than validation/publication overhead.
