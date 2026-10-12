# Scientific views and annotations

The Multiscale and Aero Field workbenches share a portable camera and annotation layer. It supports perspective and orthographic projection, world markers, metre measurements, rings, grids and value readouts. The current operator interface is the live control socket. Vehicle-editor and map-screen controls are subsequent consumers; human interaction and layout remain prototype work.

`ScientificView` owns projection math in double precision. Render adapters translate its camera convention to their native cameras. An orthographic span is the **full vertical height in metres**; at 1080 pixels and a 400 metre span the scale is 2.7 pixels/metre at every depth. Perspective FOV is vertical and measured in degrees. Pixels use top-left origin; positive eye depth points forward. All coordinates supplied to a view must share that adapter's coordinate frame.

`ScientificAnnotation` and `AnnotationStore` retain immutable values under stable IDs. Updating an ID replaces one annotation. The adapter exports rows only on a mutation or explicit status request; its live focus array is reused. Drawing introduces no per-marker GameObject and no per-frame JSON. Current bounds are 256 annotations and 64 grid cells per side. Rings use 64 world-plane chords; projected circle error is not bounded.

## Live controls

Both workbenches accept:

```text
projection perspective 60
projection ortho 400
cad grid on 10 100 view
cad grid off
cad marker target X Y Z Docking target
cad measure span AX AY AZ BX BY BZ Boom span
cad ring radius X Y Z 50 view 50 metre reference
cad value speed X Y Z 1234 m/s valid Speed
cad value speed X Y Z 1234 m/s stale Speed
cad remove target
cad clear
cad status
```

Planes are `view`, `xy`, `xz` and `yz`. The built-in grid follows the current focus; a view-plane grid also follows camera right/up. A ring keeps its world centre and follows either its selected world plane or the current view plane. Explicit API callers can supply their own axes. Measurements compute endpoint distance in metres. Readouts are authored values with explicit validity; the adapter does not infer dV, control authority or trajectory confidence.

The current depth policy is `overlay-first-unoccluded`. This helps compare measurements across views but does not prove visibility through solid geometry. A later depth-tested annotation mode must use each renderer's actual depth domain. Stale readouts remain visible in amber with a stale label.

Viewport requests settle asynchronously. Use actual width/height and projection readback before recording evidence. A rejected nonfinite command preserves the retained annotation/camera owner. Benchmark capture freezes controls. Scientific overlays and diagnostic readbacks are not suitable for clean performance measurements.

The Aero Field renderer requires the exact `continuum-aero-field-orthographic/v1` shader contract on Sheath, Ribbon and SceneDepthCopy. A legacy bundle is admitted for perspective. Orthographic selection or an incompatible reload during ortho is rejected before camera/material ownership changes. Orthographic effects use parallel camera rays and linear device-depth reconstruction. Shader reload retains authored overlays.

## Editor and map consumers

Use one annotation model with context-specific camera and interaction adapters:

| Context | First useful operations | Coordinate authority |
| --- | --- | --- |
| Vehicle editor | Front/side/top ortho views, snapping grid, axes, part/boom measurements, symmetry rings | Assembly-local frame and selected part IDs |
| Flight | Relative docking markers, scale references, observed force/authority readouts | Vessel/body frame with explicit origin and observation epoch |
| Map | Orbit-plane grids, maneuver handles, rendezvous separation, reentry corridor | Body-centred inertial frame and prediction epoch |

Keep camera navigation separate from simulation state. Selection identifies a stable domain object; dragging an annotation changes an authored diagnostic unless a command explicitly requests a maneuver or assembly edit. Switching projection should preserve the selected target and its on-screen scale. Establish that behavior and human controls before claiming a polished editor.

Prediction tubes, reentry paths and uncertainty bands should arrive as retained typed observations with units, source epoch, frame, validity and error bounds. Their displayed geometry may use screen-error simplification; their underlying numerical history remains authoritative. Relevance scheduling needs an independently conservative reachable-volume proof and a tail-cost budget before it can suppress coupled physical work. A rendered tube alone authorizes no physics culling.

## Evidence boundaries

Core checks cover projection/unprojection, constant orthographic scale, inverse depth, clipping, annotation mutation and linear orthographic trail depth. Native qualification separately checks projected marker pixels and equal-size objects at different depths, then actual Aero depth buffers against analytic geometry. Multiscale rendering does not qualify the separate Aero shader consumer. Retain the exact source/player/shader bindings and raw failures alongside successful results.

The workbenches currently run under matching Unity 2019.4.18f1. These controls are reusable camera/tool contracts; their existence does not establish a modern native KSP reconstruction or installed KSP editor/map integration.
