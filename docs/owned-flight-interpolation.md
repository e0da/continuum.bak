# Render-only delayed flight poses

`OwnedFlightInterpolation` copies bounded numeric region poses from coherent
worker publications. It retains no read banks and has no world mutation path.
The two endpoints contain body-coordinate center position, local center of mass,
quaternion and velocity. The returned display frame is reusable observation
storage; readers must use it on its owning presentation thread.

The requested delay defaults to .04 simulation seconds and is bounded by the
maximum accepted sample interval (.25 seconds). The display target advances with
the accepted warp and monotonic wall clock, then clamps to the two available
physical endpoints. Its effective delay may be smaller than requested when the
history interval is shorter. It never extrapolates past accepted physical time.
Centers, local COM and velocity interpolate linearly; orientations use shortest
arc spherical interpolation. Root position follows from displayed center,
orientation and local COM. This is visual smoothing, not a dynamics predictor.

Epoch, topology, definition, reference frame, context, executable generation and
configuration discontinuities snap. So do pause/warp transitions, nonadvancing
physical mutations, identity changes and long gaps. Restore, placement, rebase,
topology and HMR commands explicitly reset history. Ordinary increasing physical
time/state revision may interpolate. An unversioned changed pose is rejected.

The Unity consumer selects one display frame per presentation update. Craft
matrices, camera focus/ship axes, planet orientation and scientific grid share its
time/poses. Actual immutable part/body local transforms come from the matching
physical bank; each region receives one visual rigid delta. No interpolated state
is passed to controls, environment, physics, prediction or checkpoint saving.
Prediction curves retain their physical-source provenance and can therefore be
labelled stale independently of the delayed ship grid.

Controls: `interpolation on`, `interpolation off`, and
`interpolation delay SECONDS`. Status reports selected and physical universal
times, effective delay, alpha, region pose, revisions and reset reason/counters.
Pausing snaps to the accepted physical state. Presentation-off gaps snap on
resumption rather than inventing skipped motion or trail emission.

Focused portable checks cover known center/root/quaternion geometry, copied
input isolation, antipodal rotations, no extrapolation, epoch/frame/code/pause
and gap resets, and invalid/unversioned-input rejection. Native pose/pixel
qualification remains the integration owner's separate gate; this document
does not claim FPS improvement or original KSP rendering fidelity.
