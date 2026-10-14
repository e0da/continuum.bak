# Predictive flight windows

Automatic timeline pumping combines requested demand, held-input/event barriers,
numerical interval limits, advisory environment crossings and the thermal lag
deadline. Every selection observes the current committed bank. Numerical force,
surface and error checks still decide whether an attempted interval can commit.
`LastLocalWindow` records the source, code/configuration/context, selected request
and limiting kind. Adaptive retries or actuator boundaries can shorten it further;
the observation is not a current certificate after publication.

Radial forecasts use constant inertial velocity and do not bound unknown forces.
Advisory suggestions below the lesser of the quantum and 1e-4 seconds are skipped
to limit boundary chatter. This is an effort heuristic, not a motion tolerance or
safety guard; numerical refinement can still go finer.

Thermal lag uses the existing represented-clock predicate
`endTime - integratedTime >= maximumLag`. The first positive raw interval satisfying
it is selected, preserving the requested raw bound when endpoint subtraction would
enlarge it. The existing thermal fold owns the actual flush. Deferred Kelvin bounds
are retrospective; no future heating deadline is claimed without a power bound.

`PrepareCoastWindow` retains quiet/passive/provider/spin/pending-heat and elliptic
admission. It clips to the numeric outside crossing bracket minus at least .02
seconds of lead. Clipped progress below .02 seconds selects local work instead
of repeated microcoasts. All regions constrain the window. The optional exact
runtime upper bound avoids losing short-interval precision through a large UT
epoch. Endpoint radius, rounded clocks and source admission are checked before
publication. The bracket belongs to the admitted Kepler numeric model, not terrain.

Automatic pumping commits the partial coast and keeps remaining demand for local
work; `LastCoastWindow` reports both targets and bracket provenance. Existing
`PrepareCoast`, maneuver execution and manual `CoastTo` remain strict exact-target
operations. Radial/hyperbolic/tangent entry remains local. Contact resolution is
not supplied here. Windows are derived observations, not checkpoint authority.

Workbench commands: `flight-forecast SECONDS` observes the source;
`entry-orbit APO_ALTITUDE PERI_ALTITUDE` stages a cold authored ellipse;
`coast-mode local|freeze-passive` selects an explicit automatic passive-freeze
policy. Full timeline checkpoints restore that policy. Native rendering and full
playable KSP behavior require separate qualification.

With a flight timeline present, workbench `warp RATE` changes only demanded
simulation progress (up to the existing 1e6 timeline limit). It does not multiply
physical dt or inherit the old step-plan warp cap. Full execution checkpoints
restore this rate with the timeline policy. The legacy no-world host retains its
original fixed-step behavior.
