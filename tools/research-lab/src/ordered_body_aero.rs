use crate::{hash, read, Result};
use serde_json::{json, Value};
use std::{collections::BTreeSet, path::Path};

fn check(ok: bool, why: &str) -> Result<()> { if ok { Ok(()) } else { Err(why.into()) } }
fn n(v: &Value, field: &str) -> Result<u64> { v[field].as_u64().ok_or_else(|| format!("ordered aero field absent: {field}")) }
fn words<'a>(v: &'a Value, field: &str, count: usize) -> Result<&'a Vec<Value>> {
    v[field].as_array().filter(|a| a.len() == count).ok_or_else(|| format!("ordered aero bit layout differs: {field}"))
}
fn f(v: &Value) -> Result<f32> { Ok(f32::from_bits(u32::try_from(v.as_u64().ok_or("binary32 bits absent")?).map_err(|_| "binary32 bits overflow")?)) }
fn d(v: &Value) -> Result<f64> { Ok(f64::from_bits(v.as_i64().ok_or("binary64 bits absent")? as u64)) }
fn floats(v: &Value, field: &str, count: usize) -> Result<Vec<f32>> { words(v, field, count)?.iter().map(f).collect() }
fn dot(a: &[f32], b: &[f32]) -> f32 { (a[0] * b[0] + a[1] * b[1]) + a[2] * b[2] }
fn near(a: f64, b: f64) -> Result<()> {
    let tolerance = (1e-6 + 2e-5 * b.abs()).min(2e-5 * b.abs().max(1.));
    check(a.is_finite() && b.is_finite() && (a - b).abs() <= tolerance, "independent dry-tail numerical bound exceeded")
}
fn force(command: &Value, expected: &[f32], point_bits: &[Value], mode: u64, phase: &str) -> Result<()> {
    check(command["kind"] == "AddForceAtPosition" && command["phase"] == phase && n(command, "forceMode")? == mode,
        "independent dry-tail command order/mode differs")?;
    let actual = floats(command, "forceBits", 3)?;
    for i in 0..3 { near(expected[i] as f64, actual[i] as f64)?; }
    check(words(command, "pointBits", 3)?.as_slice() == point_bits, "original world application point bits differ")
}

pub fn summarize(path: &Path, probe: &Value, pid: u32) -> Result<Value> {
    let r: Value = read(path)?;
    let outer = &probe["orderedBodyAeroCapture"];
    let run = probe["runId"].as_str().ok_or("enclosing run absent")?;
    let frames = probe["frames"].as_array().ok_or("enclosing vessel frames absent")?;
    let vessel = &frames.first().ok_or("enclosing vessel absent")?["vesselId"];
    let start = n(probe, "captureStartClockTimestamp")?;
    let end = n(probe, "captureEndClockTimestamp")?;
    let frequency = n(probe, "clockFrequency")?;
    check(probe["status"] == "complete" && frequency > 0 && end > start && start > 0, "enclosing capture incomplete")?;
    check(r["schema"] == "continuum-retained-ordered-body-aero-capture/v1" && r["status"] == "observed"
        && r["cleanupStatus"] == "removed-owned-patches" && r["requested"] == true && r["runId"] == run
        && r["processId"] == pid && &r["vesselId"] == vessel && n(&r, "clockFrequency")? == frequency
        && r["path"].as_str() == path.to_str() && outer["sha256"] == hash(path)?, "original tape identity/hash/lifecycle differs")?;
    for field in ["schema", "runId", "processId", "vesselId", "clockFrequency", "status", "cleanupStatus", "topologySha256", "providerEpoch", "roots", "bodyBindings", "methods"] {
        check(r[field] == outer[field], "original sidecar and enclosing receipt diverge")?;
    }
    check(r["originalAssemblySha256"] == "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27"
        && r["originalAssemblyMvid"] == "10657063-2fc3-43a7-84fa-d39e75e877bf"
        && r["topologySha256"].as_str().is_some_and(|s| s.len() == 64 && s.bytes().all(|b| b.is_ascii_hexdigit()))
        && r["providerEpoch"].as_str().is_some_and(|s| s.ends_with(r["topologySha256"].as_str().unwrap_or("absent"))), "frozen original provider identity differs")?;
    for field in ["errors", "originalExceptions", "droppedCommands", "referenceMismatches"] { check(n(&r, field)? == 0, "original recording/reference fault")?; }
    for field in ["graphAdmissions", "graphInstalled", "graphReleased"] { check(r[field] == true, "original graph ownership incomplete")?; }
    check(r["patchedMethods"] == 4 && r["shapeAdmissions"] == 4 && r["staticCallsites"] == 14
        && r["capturedRoots"] == 2 && r["completedRoots"] == 2, "two original callsite roots incomplete")?;
    let methods = r["methods"].as_array().ok_or("original method graph absent")?;
    let pinned = [(100677461, "71f3e8ba1599644c13be502e24d7463524c98188b1f3a075b898c9a8430a0f04"),
        (100677495, "c7f90df6de22e42796d8076c908102f98fabab7893ba91cf3bd646833573d2c6"),
        (100677493, "0d86e046558359841c1d9c35d4ad03711631bce9b647654962925a689684f6e4"),
        (100677494, "b1aee44213b1cb169c95b98e7b69c8f6524b5615ab8b6d863440231e839c6094")];
    check(methods.iter().filter(|m| m["patched"] == true).count() == 4, "patched method ledger differs")?;
    for (token, digest) in pinned {
        check(methods.iter().filter(|m| m["metadataToken"] == token && m["patched"] == true
            && m["declaringType"] == "FlightIntegrator" && m["assemblyMvid"] == r["originalAssemblyMvid"] && m["ilSha256"] == digest).count() == 1,
            "pinned original command body differs")?;
    }
    check(r["inputFloatFields"] == json!(["velocity.x","velocity.y","velocity.z","dragApplicationPoint.x","dragApplicationPoint.y","dragApplicationPoint.z","liftApplicationPoint.x","liftApplicationPoint.y","liftApplicationPoint.z","rotatedScaledLift.x","rotatedScaledLift.y","rotatedScaledLift.z","angularDrag","angularDragMultiplier","dragMultiplier","bodyLiftMultiplier","globalBodyLiftMultiplier","liftMachSample"])
        && r["inputDoubleFields"] == json!(["submergedPortion","atmosphericDensity","originalDragValue","pseudoReMultiplier"])
        && r["inputFlagFields"] == json!(["ownBody","servoBody","shielded","applyDrag","applyParentDrag","angularDragByIntegrator","accelerationMode","bodyLiftEligible","liftForceEligible","liftGeometryCaptured"])
        && r["outputFloatFields"] == json!(["speedSquared","speed","direction.x","direction.y","direction.z","dragScalar","bodyLiftScalar"]), "declared numeric layouts differ")?;
    let bindings = r["bodyBindings"].as_array().ok_or("native body ledger absent")?;
    let bound = n(&r, "boundParts")?;
    let reachable = n(&r, "reachableParts")?;
    check(bound > 0 && bound <= 4096 && reachable > 0 && reachable <= bound && bindings.len() <= bound as usize * 3, "native owner bounds differ")?;
    let roots = r["roots"].as_array().filter(|a| a.len() == 2).ok_or("two immutable original roots absent")?;
    let mut matched = 0u64; let mut unsupported = 0u64; let mut last = start; let mut first_ids = Vec::new();
    let mut root_ticks = 0u64; let mut reference_ticks = 0u64; let mut copy_ticks = 0u64; let mut command_count = 0u64;
    for (sequence, root) in roots.iter().enumerate() {
        let a = n(root, "startedTicks")?; let b = n(root, "endedTicks")?;
        check(root["sequence"] == sequence && root["originalReturned"] == true && root["commandTapeComplete"] == true && root["topologyIntact"] == true
            && a >= last && b > a && b <= end && root["fixedTimeSeconds"].as_f64().is_some_and(|f| f.is_finite()) && n(root, "frame")? > 0,
            "original root clock/topology/return differs")?; last = b;
        root_ticks += b-a; reference_ticks += n(root, "referenceTicks")?; copy_ticks += n(root, "copyTicks")?;
        check(n(root, "referenceTicks")? <= b-a && n(root, "copyTicks")? <= b-a, "observer reference clock escapes root")?;
        let rows = root["rows"].as_array().filter(|a| a.len() as u64 == reachable).ok_or("reachable ordered rows absent")?;
        let commands = root["commands"].as_array().filter(|a| a.len() <= bound as usize * 16).ok_or("original command tape absent/unbounded")?;
        check(n(root, "rowCount")? == rows.len() as u64 && n(root, "commandCount")? == commands.len() as u64, "original tape counts differ")?;
        command_count += commands.len() as u64;
        let mut prior_ordinal = 0u64; let mut aero_seen = BTreeSet::new();
        for (i, command) in commands.iter().enumerate() {
            let ordinal = n(command, "ordinal")?;
            check(command["sequence"] == i && ordinal < reachable && ordinal >= prior_ordinal && command["originalReturned"] == true,
                "original command return/preorder differs")?; prior_ordinal = ordinal;
            let row = &rows[ordinal as usize];
            check(command["partId"] == row["partId"] && command["recipientBodyId"].as_i64().is_some_and(|id| id != 0)
                && bindings.iter().any(|owner| owner["bodyId"] == command["recipientBodyId"]), "original command native owner differs")?;
            floats(command, "forceBits", 3)?; floats(command, "pointBits", 3)?; f(&command["angularDragBits"])?;
            check([0,5].contains(&n(command, "forceMode")?), "original command mode differs")?;
            if command["phase"] == "queued-integration" {
                check(!aero_seen.contains(&ordinal) && ["AddForce", "AddTorque", "AddForceAtPosition"].iter().any(|k| command["kind"] == *k), "queued command follows aero publication")?;
            } else { aero_seen.insert(ordinal); check(["aero-angular","aero-drag","aero-lift"].iter().any(|k| command["phase"] == *k), "unknown original command phase")?; }
        }
        let mut ids = BTreeSet::new(); let mut slots = BTreeSet::new(); let mut root_matched = 0u64; let mut root_unsupported = 0u64;
        for (ordinal, row) in rows.iter().enumerate() {
            let part = n(row, "partId")?; let slot = n(row, "partSlot")?; let flags = n(row, "inputFlags")?;
            check(row["ordinal"] == ordinal && part > 0 && slot < bound && ids.insert(part) && slots.insert(slot) && flags < 1024
                && row["aeroEntered"] == true && row["aeroReturned"] == true && row["queuedStateCleared"] == true, "original row owner/return differs")?;
            if sequence == 0 { first_ids.push((slot, part)); } else { check(first_ids[ordinal] == (slot,part), "original traversal/owner changed between roots")?; }
            let x = floats(row, "inputFloatBits", 18)?; let out = floats(row, "outputFloatBits", 7)?;
            floats(row, "extraFloatBits", 8)?; words(row, "extraDoubleBits", 3)?.iter().map(d).collect::<Result<Vec<_>>>()?;
            let doubles = words(row, "inputDoubleBits", 4)?.iter().map(d).collect::<Result<Vec<_>>>()?;
            let tail: Vec<_> = commands.iter().filter(|c| c["ordinal"] == ordinal && c["phase"] != "queued-integration").collect();
            check(n(row, "tailCommandCount")? == tail.len() as u64 && row["tailKinds"].as_array().is_some_and(|a| a.len() == tail.len()), "original row tail count differs")?;
            if row["referenceStatus"] == "unsupported" { root_unsupported += 1; check(row["referenceReason"].as_str().is_some_and(|s| !s.is_empty()), "unsupported row reason absent")?; continue; }
            check(row["referenceStatus"] == "matched" && row["dragCalculated"] == true && flags & 6 == 0 && flags & 8 != 0
                && (flags & 1 != 0 || flags & 16 != 0) && doubles[0] == 0. && doubles[1].is_finite() && doubles[1] > 0.
                && !doubles[2].is_infinite() && doubles[3].is_finite() && x.iter().all(|f| f.is_finite()), "declared dry-tail domain differs")?;
            check(bindings.iter().any(|owner| owner["partSlot"] == slot && owner["partId"] == part && owner["bodyId"] == row["recipientBodyId"] && owner["role"] == "aero-recipient"), "dry tail recipient ownership differs")?;
            let square = dot(&x[..3], &x[..3]); check(square.is_finite() && square > 0., "dry velocity domain differs")?;
            let speed = (square as f64).sqrt() as f32;
            let direction = [x[0]/speed,x[1]/speed,x[2]/speed];
            let pressure = doubles[1] * (0.0005 * square as f64);
            let value = doubles[2] * doubles[3];
            let drag = if value.is_nan() || value == 0. {0.} else {(pressure * value * 1.) as f32 * x[14]};
            near(square as f64,out[0] as f64)?; near(speed as f64,out[1] as f64)?;
            for i in 0..3 { near(direction[i] as f64,out[i+2] as f64)?; }
            near(pressure,d(&row["dynamicPressureBits"])?)?; near(drag as f64,out[5] as f64)?;
            let mut cursor = 0usize; let mode = if flags & 64 != 0 {5} else {0};
            if flags & 1 != 0 && flags & 32 != 0 {
                for expected in [0.,x[12] * (pressure * 0.009869232667160128).max(0.) as f32 * x[13]] {
                    let c = tail.get(cursor).ok_or("original angular commands absent")?;
                    check(c["phase"] == "aero-angular" && c["kind"] == "set_angularDrag", "original angular command order differs")?;
                    near(expected as f64,f(&c["angularDragBits"])? as f64)?; cursor += 1;
                }
            }
            if !value.is_nan() && value != 0. {
                force(tail.get(cursor).ok_or("original drag command absent")?, &[-direction[0]*drag,-direction[1]*drag,-direction[2]*drag], &words(row,"inputFloatBits",18)?[3..6], mode,"aero-drag")?; cursor += 1;
            }
            let lift_written = flags & 128 != 0;
            check(row["bodyLiftScalarWritten"] == lift_written && row["liftCurveReached"] == lift_written, "original lift field-write mask differs")?;
            if lift_written {
                let lift = pressure as f32 * x[15] * x[16] * x[17]; near(lift as f64,out[6] as f64)?;
                if lift != 0. && flags & 256 != 0 {
                    check(flags & 512 != 0, "original lift preprojection absent")?;
                    let normal = [-direction[0],-direction[1],-direction[2]]; let norm = dot(&normal,&normal); let product = dot(&x[9..12],&normal);
                    let mut projected = [x[9],x[10],x[11]];
                    if norm >= f32::from_bits(1) { for i in 0..3 { projected[i] -= normal[i] * product / norm; } }
                    force(tail.get(cursor).ok_or("original lift command absent")?, &projected,&words(row,"inputFloatBits",18)?[6..9],mode,"aero-lift")?; cursor += 1;
                }
            }
            check(cursor == tail.len() && tail.iter().all(|c| c["recipientBodyId"] == row["recipientBodyId"]), "original tail command count/body differs")?;
            root_matched += 1;
        }
        check(n(root,"matchedRows")? == root_matched && n(root,"eligibleRows")? == root_matched && n(root,"unsupportedRows")? == root_unsupported
            && n(root,"mismatchedRows")? == 0 && root_matched + root_unsupported == reachable, "independent row coverage differs")?;
        matched += root_matched; unsupported += root_unsupported;
    }
    check(matched > 0 && n(&r,"matchedRows")? == matched && n(&r,"eligibleRows")? == matched && n(&r,"unsupportedRows")? == unsupported, "independent total coverage differs")?;
    let ms = 1000./frequency as f64;
    Ok(json!({"schema":"continuum-independent-ordered-body-aero/v1","status":"passed","runId":run,"processId":pid,
        "recordingSha256":hash(path)?,"originalRoots":2,"matchedDryRows":matched,"unsupportedRows":unsupported,"actualCommands":command_count,
        "meanObservedRootMilliseconds":root_ticks as f64*ms/2.,"meanReferenceMilliseconds":reference_ticks as f64*ms/2.,"meanRecordingMilliseconds":copy_ticks as f64*ms/2.,
        "scope":"Independent raw IEEE input/scalar/ordered actual-tail command math under frozen numerical bounds. Actual queued tape is bound and ordered but queued input values are not reconstructed. No candidate force publication, complete aero replacement or clean FPS claim."}))
}
