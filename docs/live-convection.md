# Sparse convection in the live game

The opt-in replacement removes dense rectangle queries from the supersonic `FlightIntegrator.UpdateOcclusionConvection` fold. The original game still builds, updates and sorts its projected thermal rows. A conservative spatial index finds receiver candidates; ordered prefix accumulation and last-reset certificates preserve the original scalar operations and cone propagation. This changes the algorithm at a whole-fold boundary rather than moving the same dense loop to another language.

`ConvectionSparseKernel` copies into reusable shadow rows, computes without publishing, revalidates live ownership and inputs, then commits all thermal/cone results on the main thread. Original thermal objects, array references, physics integration and the original FX expression retain their ownership. Unsupported domains return to the untouched original fold. Unexpected faults disable the candidate and invalidate its qualification.

## Selecting the experiment

The research runner accepts `--flight-convection-replacement query|verify` while resolving a private frozen atmospheric normal-time profile. This is exclusive with live play, convection capture, integrator attribution, docking and checkpoint reload. Engine assembly, method IL, provider chain and patch shape admission are mandatory. Normal launches do not enable the replacement.

`verify` executes two alternating same-state stock/candidate observations and compares every thermal/cone field bit-for-bit, including the backing array's references. It publishes the candidate only on equality, then uses stock for the remaining capture. Snapshot, rollback and comparison are intrusive verification costs and are reported separately. Its frame rate is not candidate-only performance.

`query` uses the candidate for every admitted invocation and records actual method envelopes, projection, candidate-only seam admission/copy, fold, commit and the original common FX call. Copying and publication are part of candidate cost. Cold-call flags distinguish JIT observations from steady capture.

Both modes produce an owned `continuum-convection-replacement-v1` report. The runner preserves it before independently checking identity, status, cost sums, comparison order and hook removal. Private inputs, receipts, generated code and game binaries are not repository content.

## Observed scope

Two same-state 512-part native Mono comparisons passed bit-for-bit. Complete stock/candidate costs were 31.2065/21.2220 ms on the first cold call and 30.2029/5.7367 ms on the second. Both include original projection, candidate copying/publication and one original FX call. The verification tax is separate.

A separate three-simulated-second candidate-only 1080p capture preserved 512 parts, applied twenty captured replacements without fallback or fault, and removed its owned hooks. Mean interval was 25.2706 ms (39.57 FPS), p95 31.5961 ms, p99 46.0868 ms and achieved simulation rate 1.0033. Captured complete convection cost averaged 4.96171 ms per call; the actual method envelope averaged 4.96697 ms. The retained flight image was decoded and visually inspected.

This is one experimental workload, with no matched full-frame FPS baseline. It does not qualify the 60 FPS playable milestone, broad thermal evolution, topology transitions, unsupported inputs or mod compatibility. The visible flame/depth-camera path was inactive or disabled in these diagnostic views; heated FX fidelity remains a separate gate. Stock/mod shutdown errors remain separate findings. The normal playable installation is restored after experiments.

The same candidate-only capture measures managed fixed-update callbacks at 9.18694 ms per rendered frame (7.24004 ms per physics iteration), native physics at 2.85343 ms per frame, managed Update at 5.03985 ms per frame, and the render-end envelope at 4.16978 ms per frame. These are phase scopes with observer overhead, not exclusive function costs or isolated GPU time. Managed fixed-update work is the largest measured remaining scope; attribute its owners with the candidate active before selecting the next replacement.
