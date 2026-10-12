# Recorded trail emitter

The private laboratory selects a fresh mapped multiscale scene and `--flight-trail-emitter` output, an explicit `--flight-trail-emitter-part root|FLIGHT_ID`, and `--flight-trail-emitter-mode trajectory-tracer|engine-readback`. The capture binds the actual vessel, resolved part, body, scene hashes and observation time. `trajectory-tracer` supplies artistic emission along measured motion. `engine-readback` additionally observes the first engine module and thrust transform; it does not record native plume particles.

The bounded TER1 recording carries body-fixed root and emitter positions, a captured scene basis, orientation observations, engine scalars and monotonic capture brackets. It performs no physics writes. This prototype consumes the existing cold-manifest lease; simultaneous vessel-pose and emitter capture is rejected. Laboratory emitter selection clears an inherited pose-recording request.

The workbench accepts `trail recording ABSOLUTE_PATH`, then `trail seek SECONDS`, play/pause, step, reset and wind controls. Recorded emission ownership cannot be overridden. Camera and immutable craft geometry follow the measured root translation. Craft rotation and deformation retain their captured state; recording those values does not qualify their presentation. Existing shader reload preserves the recording, cursor and retained parcel history. Full scene replacement releases the trail owner.

Actual KSP capture, binary time/coordinate proof and visible native consumption are separate gates. This is an artistic persistent trail driven by real motion, with endpoint interpolation and authored diffusion/decay. Weather and exhaust microphysics are unimplemented.
