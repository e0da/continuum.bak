# Acquired forecast situations

The causal lab's `--forecast-situations all|entry|atmosphere` consumer runs one
functional observation per selected situation against the acquired native body,
part/resource/actuator, area and central-body packets. Original actuators are
retained but disabled; placements and zero chassis spin are authored treatments.
This is not a performance comparison or stock-flight equivalence proof.

The entry situation constructs an ellipse whose periapsis is below, and apoapsis
above, the acquired craft's finite-size atmosphere guard. It starts at apoapsis
and requests a target beyond entry. The unchanged strict coast must reject with
an unchanged checkpoint. A single timeline commit must then record a positive
partial coast outside the lower crossing bracket by the declared lead. The
crossing time is independently calculated from eccentric anomaly, Kepler's
equation and mean motion. Remaining requested time stays owed and completes via
bounded local work; repeated zero-progress coast retries fail. The final sample
must have positive captured atmospheric density. Radial, hyperbolic and tangent
coast domains remain unsupported; the spherical no-contact guard remains active.

The atmospheric situation starts just above an actual captured table knot with
declared inward velocity. Constant-velocity forecast rows are checked against
independent quadratic roots. Source checkpoint bytes must remain unchanged.
Disposable local physical prediction records all returned samples and its own
completion/cost metadata, without asserting future force parity. Local thermal
slices use an explicit 0.06-second maximum lag and retain the pending forcing
ledger. A binary checkpoint with positive pending duration is restored, followed
by identical slices; the full physical/execution continuation must match exactly.
The pending and continuation binary files remain available for independent decode.

`forecast-situations.json` retains failed cases, source/executable hashes,
authored orbit parameters, numeric coast/local observations, raw forecast rows,
sampled density/temperature and thermal frontier data. Complete clocks include
preparation, worker dispatch, forecasts, checks, replay and cleanup; nested clocks
are not summed. Thermal integration limits bound the retained forcing ledger,
not an independent error bound on future atmospheric forcing quadrature. This
runner makes no renderer, FPS, contact or universal trajectory-accuracy claim.

The disposable .1-second local projection permits at most 2048 accepted steps
and 30 wall seconds; an earlier 128-step budget legitimately exhausted while
preserving its source. The continuation explicitly grants demand and drains
bounded intervals before cold replacement. It repeats the same relative wall
sequence after restore, preserving full checkpoint bytes; one manual step is not
assumed to consume all requested time.
