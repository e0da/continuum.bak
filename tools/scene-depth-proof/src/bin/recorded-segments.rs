use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{
    env,
    fs::{self, OpenOptions},
    io::Write,
    path::Path,
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error>>;
const POSITION: f64 = 1e-5;
const SCREEN: f64 = 0.5;
const DEPTH_ABS: f64 = 1e-4;
const DEPTH_REL: f64 = 1e-5;
fn require(ok: bool, reason: &str) -> Result<()> {
    if ok {
        Ok(())
    } else {
        Err(reason.into())
    }
}
fn number(v: &Value) -> Result<f64> {
    v.as_f64()
        .filter(|x| x.is_finite())
        .ok_or_else(|| "Finite number required".into())
}
fn array(v: &Value, size: usize) -> Result<Vec<f64>> {
    let a = v
        .as_array()
        .filter(|a| a.len() == size)
        .ok_or("Numeric array shape differs")?;
    a.iter().map(number).collect()
}
fn vector(v: &Value) -> Result<[f64; 3]> {
    let a = array(v, 3)?;
    Ok([a[0], a[1], a[2]])
}
fn hash(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn digest(v: &Value) -> bool {
    v.as_str().is_some_and(|s| {
        s.len() == 64
            && s.bytes()
                .all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c))
    })
}
fn sub(a: [f64; 3], b: [f64; 3]) -> [f64; 3] {
    [a[0] - b[0], a[1] - b[1], a[2] - b[2]]
}
fn dot(a: [f64; 3], b: [f64; 3]) -> f64 {
    a.iter().zip(b).map(|(x, y)| x * y).sum()
}
fn distance(a: [f64; 3], b: [f64; 3]) -> f64 {
    dot(sub(a, b), sub(a, b)).sqrt()
}
fn decode(bytes: &[u8]) -> Result<(usize, usize, Vec<[u8; 3]>)> {
    let mut decoder = png::Decoder::new(bytes);
    decoder.set_transformations(png::Transformations::EXPAND);
    let mut reader = decoder.read_info()?;
    require(
        reader.info().width as u64 * reader.info().height as u64 <= 16_777_216,
        "PNG pixel bound exceeded",
    )?;
    let mut buffer = vec![0; reader.output_buffer_size()];
    let info = reader.next_frame(&mut buffer)?;
    require(
        info.bit_depth == png::BitDepth::Eight,
        "Eight-bit PNG required",
    )?;
    let channels = match info.color_type {
        png::ColorType::Rgb => 3,
        png::ColorType::Rgba => 4,
        _ => return Err("RGB/RGBA PNG required".into()),
    };
    Ok((
        info.width as usize,
        info.height as usize,
        buffer[..info.buffer_size()]
            .chunks_exact(channels)
            .map(|p| [p[0], p[1], p[2]])
            .collect(),
    ))
}
#[derive(Clone, Copy)]
struct Sample {
    t: f64,
    p: [f64; 3],
}
fn interpolate(rows: &[Sample], t: f64) -> Result<([f64; 3], usize)> {
    require(
        t >= rows[0].t && t <= rows[rows.len() - 1].t,
        "Source interpolation outside recording",
    )?;
    let i = rows.partition_point(|s| s.t <= t).saturating_sub(1);
    if i + 1 == rows.len() {
        return Ok((rows[i].p, i));
    }
    let u = (t - rows[i].t) / (rows[i + 1].t - rows[i].t);
    Ok((
        [
            rows[i].p[0] + (rows[i + 1].p[0] - rows[i].p[0]) * u,
            rows[i].p[1] + (rows[i + 1].p[1] - rows[i].p[1]) * u,
            rows[i].p[2] + (rows[i + 1].p[2] - rows[i].p[2]) * u,
        ],
        i,
    ))
}
struct Camera {
    origin: [f64; 3],
    view: Vec<f64>,
    projection: Vec<f64>,
    width: usize,
    height: usize,
}
impl Camera {
    fn eye(&self, p: [f64; 3]) -> f64 {
        let r = sub(p, self.origin);
        -(self.view[8] * r[0] + self.view[9] * r[1] + self.view[10] * r[2] + self.view[11])
    }
    fn project(&self, p: [f64; 3]) -> Result<[f64; 3]> {
        let r = sub(p, self.origin);
        let world = [r[0], r[1], r[2], 1.];
        let mut eye = [0.; 4];
        let mut clip = [0.; 4];
        for (i, e) in eye.iter_mut().enumerate() {
            *e = (0..4).map(|k| self.view[i * 4 + k] * world[k]).sum();
        }
        for (i, c) in clip.iter_mut().enumerate() {
            *c = (0..4).map(|k| self.projection[i * 4 + k] * eye[k]).sum();
        }
        require(
            clip.iter().all(|x| x.is_finite()) && clip[3] > 0. && -eye[2] > 0.,
            "Source point behind projection",
        )?;
        require(
            clip[2] >= -1e-6 * clip[3] && clip[2] <= clip[3] * (1. + 1e-6),
            "Source point outside actual Metal depth clip",
        )?;
        Ok([
            (clip[0] / clip[3] * 0.5 + 0.5) * self.width as f64,
            (0.5 - clip[1] / clip[3] * 0.5) * self.height as f64,
            -eye[2],
        ])
    }
}
fn chord(a: [f64; 3], b: [f64; 3], p: [f64; 3]) -> (f64, f64) {
    let dx = b[0] - a[0];
    let dy = b[1] - a[1];
    let length = dx * dx + dy * dy;
    let u = if length == 0. {
        0.
    } else {
        (((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / length).clamp(0., 1.)
    };
    let x = p[0] - a[0] - u * dx;
    let y = p[1] - a[1] - u * dy;
    (u, (x * x + y * y).sqrt())
}
struct PathSpan {
    a: [f64; 3],
    b: [f64; 3],
    radius: f64,
}
fn close_to_span(x: f64, y: f64, s: &PathSpan) -> bool {
    chord(s.a, s.b, [x, y, 0.]).1 <= s.radius
}
fn proof(paths: &[String], bytes: &[Vec<u8>]) -> Result<Value> {
    let off: Value = serde_json::from_slice(&bytes[2])?;
    let on: Value = serde_json::from_slice(&bytes[3])?;
    let wire: Value = serde_json::from_slice(&bytes[4])?;
    require(
        off["schema"] == "continuum-multiscale-workbench/v1"
            && wire["schema"] == "continuum-independent-emitter-wire-proof/v1"
            && wire["status"] == "passed",
        "Source schema/proof differs",
    )?;
    require(
        off["processId"].as_u64().is_some_and(|p| p > 0)
            && off["status"] == "resident"
            && off["error"] == ""
            && off["trailInstalled"] == true,
        "Native owner not resident",
    )?;
    for key in ["assemblySha256", "sceneSha256", "wholeCraftSha256"] {
        require(digest(&off[key]), "Native/source digest missing")?;
    }
    require(
        off["trail"]["visible"] == false && on["trail"]["visible"] == true,
        "Expected visibility-only off/on pair",
    )?;
    let top_incidental = [
        "bodyDraws",
        "commands",
        "craftDrawCalls",
        "craftFrames",
        "frames",
        "lastPng",
        "lastPngSha256",
        "wholeSubmissionTicks",
    ];
    let trail_incidental = [
        "compositionTicks",
        "renderTicks",
        "renderedFrames",
        "submissionTicks",
        "viewSetupTicks",
        "visible",
    ];
    let mut left = off.clone();
    let mut right = on.clone();
    for v in [&mut left, &mut right] {
        for key in top_incidental {
            v.as_object_mut()
                .ok_or("Status object required")?
                .remove(key);
        }
        for key in trail_incidental {
            v["trail"]
                .as_object_mut()
                .ok_or("Trail object required")?
                .remove(key);
        }
    }
    require(
        left == right,
        "Process/view/source/history or non-incidental state changed",
    )?;
    let trail = &on["trail"];
    for (a, b) in [
        ("recordingSha256", "recordingSha256"),
        ("recordingRunId", "runId"),
        ("recordingProcessId", "processId"),
        ("recordingFrames", "frames"),
        ("recordingMode", "mode"),
    ] {
        require(
            !trail[a].is_null() && trail[a] == wire[b],
            "Recording identity differs",
        )?;
    }
    require(
        digest(&wire["recordingSha256"])
            && wire["processId"].as_u64().is_some_and(|p| p > 0)
            && wire["runId"].as_str().is_some_and(|s| !s.is_empty()),
        "Decoded source identity incomplete",
    )?;
    for key in ["sceneSha256", "wholeCraftSha256"] {
        require(
            on[key] == wire[key] && digest(&wire[key]),
            "Cold scene differs",
        )?;
    }
    require(
        trail["representation"] == "segments"
            && trail["paused"] == true
            && trail["emission"] == true
            && trail["probeInstalled"] == false
            && trail["recordingMode"] == "trajectory-tracer",
        "Paused measured segment tracer required",
    )?;
    for key in ["windX", "windY", "windZ"] {
        require(number(&trail[key])? == 0., "Zero wind required")?;
    }
    require(
        trail["segmentWindChanges"].as_u64() == Some(0),
        "Zero historical wind ledger required",
    )?;
    let raw = wire["rows"]
        .as_array()
        .filter(|r| r.len() >= 2 && r.len() <= 4096)
        .ok_or("Bounded source rows absent")?;
    require(
        wire["frames"].as_u64() == Some(raw.len() as u64),
        "Decoded source row count differs",
    )?;
    let mut rows: Vec<Sample> = Vec::with_capacity(raw.len());
    let mut roots: Vec<Sample> = Vec::with_capacity(raw.len());
    for (i, r) in raw.iter().enumerate() {
        require(
            r["sequence"].as_u64() == Some(i as u64) && r["emission"] == true,
            "Ordered emitting source rows required",
        )?;
        let t = number(&r["time"])?;
        require(
            if i == 0 {
                t == 0.
            } else {
                t > rows[i - 1].t && t - rows[i - 1].t <= 0.25
            },
            "Source gap/nonmonotonic time requires a separate proof",
        )?;
        rows.push(Sample {
            t,
            p: vector(&r["emitterScene"])?,
        });
        roots.push(Sample {
            t,
            p: vector(&r["rootScene"])?,
        });
    }
    let time = number(&trail["simulationTime"])?;
    let lifetime = number(&trail["lifetimeSeconds"])?;
    let width0 = number(&trail["initialWidthMeters"])?;
    let diffusion = number(&trail["diffusionSquareMetersPerSecond"])?;
    let decay = number(&trail["decayPerSecond"])?;
    require(
        time > 0.
            && time <= rows.last().unwrap().t
            && lifetime > time
            && width0 >= 0.001
            && width0 <= 1000.
            && diffusion >= 0.
            && diffusion <= 1000.
            && decay >= 0.
            && decay <= 100.,
        "History parameters outside frozen proof scope",
    )?;
    let (root, cursor) = interpolate(&roots, time)?;
    require(
        distance(root, vector(&trail["recordedRootScene"])?) <= POSITION
            && distance(root, vector(&on["focusTargetMeters"])?) <= POSITION
            && trail["recordingCursor"].as_u64() == Some(cursor as u64),
        "Measured root/cursor differs",
    )?;
    require(
        number(&trail["recordingUniversalTime"])? == number(&raw[0]["universalTime"])? + time,
        "Source UT differs",
    )?;
    let (w, h, off_pixels) = decode(&bytes[0])?;
    let (ww, hh, on_pixels) = decode(&bytes[1])?;
    require(
        (w, h) == (ww, hh)
            && on["width"].as_u64() == Some(w as u64)
            && on["height"].as_u64() == Some(h as u64)
            && trail["segmentViewportWidth"] == w
            && trail["segmentViewportHeight"] == h,
        "Viewport differs",
    )?;
    for (i, state) in [(0, &off), (1, &on)] {
        require(
            state["lastPngSha256"].as_str() == Some(hash(&bytes[i]).as_str())
                && Path::new(&paths[i]).file_name().and_then(|s| s.to_str())
                    == state["lastPng"].as_str(),
            "PNG capture binding differs",
        )?;
    }
    let origin = vector(&trail["segmentCameraOrigin"])?;
    require(
        distance(origin, vector(&on["cameraAnchorMeters"])?) <= POSITION,
        "Segment camera origin differs",
    )?;
    let view = array(&trail["segmentView"], 16)?;
    let projection = array(&trail["segmentGpuProjection"], 16)?;
    let yaw = number(&on["yaw"])?.to_radians();
    let pitch = number(&on["pitch"])?.to_radians();
    let fov = number(&on["fov"])?;
    require(fov > 0. && fov < 180., "Camera FOV invalid")?;
    let forward = [
        pitch.cos() * yaw.sin(),
        -pitch.sin(),
        pitch.cos() * yaw.cos(),
    ];
    let right_axis = [yaw.cos(), 0., -yaw.sin()];
    let up = [
        forward[1] * right_axis[2] - forward[2] * right_axis[1],
        forward[2] * right_axis[0] - forward[0] * right_axis[2],
        forward[0] * right_axis[1] - forward[1] * right_axis[0],
    ];
    for (i, axis) in [right_axis, up, [-forward[0], -forward[1], -forward[2]]]
        .iter()
        .enumerate()
    {
        require(
            (0..3).all(|j| (view[i * 4 + j] - axis[j]).abs() <= 1e-5)
                && view[i * 4 + 3].abs() <= POSITION,
            "Actual view differs from independent yaw/pitch or zero local camera",
        )?;
    }
    require(
        view[12] == 0. && view[13] == 0. && view[14] == 0. && view[15] == 1.,
        "Actual view affine row differs",
    )?;
    let slope = 1. / (fov.to_radians() / 2.).tan();
    require(
        (projection[0] - slope * h as f64 / w as f64).abs() <= 1e-5
            && (projection[5] - slope).abs() <= 1e-5
            && projection[11].is_finite()
            && projection[14] == -1.
            && projection[15] == 0.,
        "Actual GPU perspective/UV convention unsupported",
    )?;
    for i in [1, 2, 3, 4, 6, 7, 8, 9, 12, 13] {
        require(
            projection[i].abs() <= 1e-7,
            "Off-axis GPU projection unsupported",
        )?;
    }
    let camera = Camera {
        origin,
        view,
        projection,
        width: w,
        height: h,
    };
    let near = number(&trail["segmentNearPlane"])?;
    require(
        near > 0.
            && near.is_finite()
            && number(&trail["segmentErrorPixels"])? == SCREEN
            && number(&trail["segmentDepthAbsoluteToleranceMeters"])? == DEPTH_ABS
            && number(&trail["segmentDepthRelativeTolerance"])? == DEPTH_REL
            && number(&trail["segmentMaximumSourceSpanSeconds"])? == 0.25,
        "Frozen segment gates differ",
    )?;
    require(
        number(&trail["segmentRemovedErrorPixels"])? <= SCREEN
            && number(&trail["segmentRemovedDepthErrorRatio"])? <= 1.
            && trail["segmentGapSpans"] == 0,
        "Reported segment gate or gap failed",
    )?;
    let geometry = trail["segmentGeometry"]
        .as_array()
        .filter(|g| !g.is_empty() && g.len() % 13 == 0 && g.len() / 13 <= 8192)
        .ok_or("Bounded segment geometry absent")?;
    require(
        trail["segmentDraws"].as_u64() == Some((geometry.len() / 13) as u64)
            && trail["segmentCapacity"] == 8192,
        "Segment count/capacity differs",
    )?;
    let mut checks = Vec::new();
    let mut intervals = Vec::new();
    let mut previous_end = 0.;
    let mut previous_run = 0.;
    let mut max_screen: f64 = 0.;
    let mut max_depth: f64 = 0.;
    for (ordinal, s) in geometry.chunks_exact(13).enumerate() {
        let a = [number(&s[0])?, number(&s[1])?, number(&s[2])?];
        let b = [number(&s[3])?, number(&s[4])?, number(&s[5])?];
        let ta = number(&s[6])?;
        let tb = number(&s[7])?;
        let run = number(&s[12])?;
        require(
            ta >= 0.
                && tb > ta
                && tb <= time
                && ta >= previous_end
                && run >= previous_run
                && run.fract() == 0.,
            "Segment time/run ordering differs",
        )?;
        let source_a = interpolate(&rows, ta)?.0;
        let source_b = interpolate(&rows, tb)?.0;
        require(
            distance(a, source_a) <= POSITION && distance(b, source_b) <= POSITION,
            "Segment endpoint differs from measured worldline",
        )?;
        for (t, wi, oi) in [(ta, 8, 10), (tb, 9, 11)] {
            require(
                (number(&s[wi])? - (width0 * width0 + 2. * diffusion * (time - t)).sqrt()).abs()
                    <= 1e-10
                    && (number(&s[oi])? - (-decay * (time - t)).exp()).abs() <= 1e-12,
                "Endpoint width/opacity differs from analytic age",
            )?;
        }
        require(
            camera.eye(a) >= near - DEPTH_ABS && camera.eye(b) >= near - DEPTH_ABS,
            "Segment crosses unsupported near plane",
        )?;
        let pa = camera.project(a)?;
        let pb = camera.project(b)?;
        let mut knot_count = 0;
        let mut screen_error: f64 = 0.;
        let mut depth_error: f64 = 0.;
        for r in rows.iter().filter(|r| r.t > ta && r.t < tb) {
            let q = camera.project(r.p)?;
            let (u, d) = chord(pa, pb, q);
            let depth = 1. / ((1. - u) / pa[2] + u / pb[2]);
            let z = (q[2] - depth).abs();
            require(
                d <= SCREEN + 1e-6 && z <= DEPTH_ABS + DEPTH_REL * q[2].abs() + 1e-8,
                "Independent source knot screen/depth certificate failed",
            )?;
            knot_count += 1;
            screen_error = screen_error.max(d);
            depth_error = depth_error.max(z);
        }
        max_screen = max_screen.max(screen_error);
        max_depth = max_depth.max(depth_error);
        previous_end = tb;
        previous_run = run;
        intervals.push((ta, tb));
        checks.push(json!({"ordinal":ordinal,"birthA":ta,"birthB":tb,"run":run,"sourceKnotsChecked":knot_count,"maximumScreenErrorPixels":screen_error,"maximumDepthErrorMeters":depth_error,"endpointA":a,"endpointB":b}));
    }
    // Coverage derives from each measured interval, not from reported simplification
    // decisions. Near-plane clipping is solved directly on that linear source span.
    let mut source_paths = Vec::new();
    let mut covered = 0;
    for pair in rows.windows(2) {
        if pair[0].t >= time {
            break;
        }
        let mut ta = pair[0].t;
        let mut tb = pair[1].t.min(time);
        let mut a = pair[0].p;
        let mut b = interpolate(&rows, tb)?.0;
        let da = camera.eye(a);
        let db = camera.eye(b);
        if da < near && db < near {
            continue;
        }
        if da < near {
            let u = (near - da) / (db - da);
            ta += u * (tb - ta);
            a = interpolate(&rows, ta)?.0;
        }
        if db < near {
            let u = (near - camera.eye(a)) / (db - camera.eye(a));
            tb = ta + u * (tb - ta);
            b = interpolate(&rows, tb)?.0;
        }
        let mut cursor = ta;
        for &(lo, hi) in &intervals {
            if hi <= cursor {
                continue;
            }
            if lo > cursor + 1e-10 {
                break;
            }
            cursor = cursor.max(hi.min(tb));
            if cursor >= tb - 1e-10 {
                break;
            }
        }
        require(
            cursor >= tb - 1e-10,
            "Visible source span omitted from retained geometry",
        )?;
        covered += 1;
        let pa = camera.project(a)?;
        let pb = camera.project(b)?;
        let widest = (width0 * width0 + 2. * diffusion * (time - ta)).sqrt();
        let radius = widest * (h as f64 * slope / 2.) / pa[2].min(pb[2]) + 1. + 0.5 + SCREEN;
        source_paths.push(PathSpan {
            a: pa,
            b: pb,
            radius,
        });
    }
    require(covered > 0, "No source-visible span coverage")?;
    let cadence = number(&trail["emissionCadenceSeconds"])?;
    require(cadence == 0.1, "Prior particle cadence differs")?;
    let mut old_births = Vec::new();
    for i in 1..=4096 {
        let birth = i as f64 * cadence;
        if birth > time {
            break;
        }
        let p = interpolate(&rows, birth)?.0;
        if camera.eye(p) <= 0. {
            continue;
        }
        let p = camera.project(p)?;
        let width = (width0 * width0 + 2. * diffusion * (time - birth)).sqrt();
        old_births.push((p, width * (h as f64 * slope / 2.) / p[2] + 1. + 0.5));
    }
    let mut changed = 0;
    let mut matched = 0;
    let mut interior = 0;
    let mut changes = Vec::new();
    for y in 0..h {
        for x in 0..w {
            let at = y * w + x;
            if (x < 450 && y < 410) || off_pixels[at] == on_pixels[at] {
                continue;
            }
            changed += 1;
            let near_path = source_paths
                .iter()
                .any(|s| close_to_span(x as f64, y as f64, s));
            let away = old_births
                .iter()
                .all(|(p, r)| (x as f64 - p[0]).abs() > *r || (y as f64 - p[1]).abs() > *r);
            if near_path {
                matched += 1;
                if away {
                    interior += 1;
                }
            }
            if changes.len() < 4096 {
                changes.push(json!({"x":x,"y":y,"onRgb":on_pixels[at],"offRgb":off_pixels[at],"nearIndependentSourcePath":near_path,"awayFromOldParticleBirths":away}));
            }
        }
    }
    let passed = matched >= 32 && interior >= 16;
    Ok(
        json!({"status":if passed{"passed"}else{"failed"},"error":if passed{""}else{"Insufficient measured-path/interior pixel coverage"},"ownedPid":on["processId"],"assemblySha256":on["assemblySha256"],"sceneSha256":on["sceneSha256"],"wholeCraftSha256":on["wholeCraftSha256"],"recordingSha256":trail["recordingSha256"],"sourceRunId":wire["runId"],"sourceProcessId":wire["processId"],"simulationTime":time,"cursor":cursor,"sourceRows":rows.len(),"sourceVisibleSpans":covered,"segments":checks,"width":w,"height":h,"maximumIndependentScreenErrorPixels":max_screen,"maximumIndependentDepthErrorMeters":max_depth,"changedRgbPixelsOutsideHud":changed,"matchedSourcePathPixels":matched,"unmatchedSourcePathPixels":changed-matched,"connectedInteriorPixels":interior,"changes":changes,"changesTruncated":changed>4096,"incidentalTopFieldsExcluded":top_incidental,"incidentalTrailFieldsExcluded":trail_incidental}),
    )
}
fn main() -> Result<()> {
    let args: Vec<String> = env::args().skip(1).collect();
    require(
        args.len() == 6,
        "OFF_PNG ON_PNG OFF_STATUS ON_STATUS DECODED_WIRE NEW_OUTPUT required",
    )?;
    let mut bytes = Vec::new();
    for p in &args[..5] {
        require(
            fs::metadata(p)?.len() <= 128 * 1024 * 1024,
            "Input file bound exceeded",
        )?;
        bytes.push(fs::read(p)?);
    }
    let inputs: Vec<Value> = args[..5]
        .iter()
        .zip(&bytes)
        .map(|(p, b)| json!({"path":p,"sha256":hash(b),"bytes":b.len()}))
        .collect();
    let mut report = match proof(&args, &bytes) {
        Ok(v) => v,
        Err(e) => json!({"status":"failed","error":e.to_string()}),
    };
    for (path, initial) in args[..5].iter().zip(&bytes) {
        if fs::read(path)? != *initial {
            report["status"] = json!("failed");
            report["error"] = json!("Input changed during proof");
        }
    }
    report["schema"] = json!("continuum-independent-recorded-segments-pixel-proof/v1");
    report["inputs"] = json!(inputs);
    report["positionToleranceMeters"] = json!(POSITION);
    report["screenErrorPixels"] = json!(SCREEN);
    report["depthAbsoluteToleranceMeters"] = json!(DEPTH_ABS);
    report["depthRelativeTolerance"] = json!(DEPTH_REL);
    report["minimumMatchedPixels"] = json!(32);
    report["minimumConnectedInteriorPixels"] = json!(16);
    report["scope"]=json!("Independent emitting zero-wind worldline endpoint/time/width/opacity and source-knot centerline/perspective-depth certificates, actual matrix plus independent yaw/pitch/FOV admission, full source-visible interval coverage, and decoded changed RGB near measured source spans away from former particle disks. Frozen bounded tracer only; no general occlusion, wind, emission transitions, plume/density physics, whole-scene equivalence or CPU/GPU speed claim.");
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args[5])?;
    writeln!(output, "{}", serde_json::to_string_pretty(&report)?)?;
    require(
        report["status"] == "passed",
        "Recorded segment proof failed; see new output",
    )
}
