# Local moving-frame topology transfer

Split and join project unique physical members in the chosen source region's
axes and COM-velocity frame. Member COM lever arms come from retained local
geometry, without adding and subtracting a large world origin. For a foreign
region, root positions are subtracted before its rotated local offset is added.
The projection receives relative COM velocities; publication adds the chosen
frame velocity once. Angular velocities and complete rotated inertia tensors
remain physical inputs.

Removed kinetic energy is computed in this moving frame. Its value is Galilean
invariant and avoids subtracting large common translational energies. Existing
projection momentum and energy admission budgets remain 1e-10. Input floating
point values still have their original representational limits; no algorithm can
recover differences already lost when a caller constructs a boosted state.

The focused topology-transfer project checks nontrivial body/principal frames,
local COM offsets, unequal masses, a million-metre origin translation and a
common velocity boost. Independent local P/L/energy sums cover rigid splitting
and dissipative joining after a foreign-root reframe and relative slip. Source
snapshot immutability and rejected topology identity checks remain explicit.
Source acceptance is separate from the acquired mission conservation gate.
