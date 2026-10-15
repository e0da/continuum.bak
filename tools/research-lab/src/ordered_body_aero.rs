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

const BODY_FLOAT_FIELDS: [&str; 27] = ["mass","position.x","position.y","position.z","rotation.x","rotation.y","rotation.z","rotation.w","localCenterOfMass.x","localCenterOfMass.y","localCenterOfMass.z","worldCenterOfMass.x","worldCenterOfMass.y","worldCenterOfMass.z","inertiaTensor.x","inertiaTensor.y","inertiaTensor.z","inertiaTensorRotation.x","inertiaTensorRotation.y","inertiaTensorRotation.z","inertiaTensorRotation.w","velocity.x","velocity.y","velocity.z","angularVelocity.x","angularVelocity.y","angularVelocity.z"];

fn body_snapshots(root: &Value, commands: &[Value], bindings: &[Value], bound: usize) -> Result<usize> {
    let snapshots = root["bodySnapshots"].as_array().filter(|a| a.len() <= bound * 3).ok_or("v2 first-command body snapshots absent/unbounded")?;
    let mut ids = BTreeSet::new();
    let mut prior_end = n(root, "startedTicks")?;
    let mut prior_first = None;
    for (index, s) in snapshots.iter().enumerate() {
        let id = s["bodyId"].as_i64().filter(|id| *id != 0).ok_or("v2 snapshot body identity absent")?;
        let first = usize::try_from(n(s, "firstCommandSequence")?).map_err(|_| "snapshot sequence overflow")?;
        let start = n(s, "startedTicks")?; let end = n(s, "endedTicks")?;
        check(s["index"] == index && ids.insert(id) && start >= prior_end && end >= start && end <= n(root, "endedTicks")?
            && prior_first.is_none_or(|p| first > p) && bindings.iter().any(|b| b["bodyId"] == id), "v2 snapshot identity/order/clock differs")?;
        prior_end = end; prior_first = Some(first);
        let c = commands.get(first).ok_or("v2 snapshot first command absent")?;
        check(c["bodySnapshotIndex"] == index && c["recipientBodyId"] == id
            && !commands[..first].iter().any(|c| c["recipientBodyId"] == id), "v2 snapshot is not first reached command")?;
        let values = floats(s, "floatBits", 27)?;
        check(values.iter().all(|v| v.is_finite()) && values[0] > 0. && values[14..17].iter().all(|v| *v > 0.), "v2 body mass/principal inertia/nonfinite properties differ")?;
        for q in [&values[4..8], &values[17..21]] {
            check((q.iter().map(|v| (*v as f64) * (*v as f64)).sum::<f64>() - 1.).abs() <= 0.001, "v2 native quaternion outside frozen unit bound")?;
        }
        for name in ["alive","isKinematic","dynamic","activeSelf","activeInHierarchy","sleeping"] {
            check(s[name].is_boolean(), "v2 native body boolean absent")?;
        }
        check(s["alive"] == true && s["dynamic"].as_bool() != s["isKinematic"].as_bool()
            && !(s["activeInHierarchy"] == true && s["activeSelf"] == false), "v2 body liveness/dynamic/activity contradiction")?;
    }
    for (sequence, c) in commands.iter().enumerate() {
        let index = usize::try_from(n(c, "bodySnapshotIndex")?).map_err(|_| "body snapshot index overflow")?;
        let s = snapshots.get(index).ok_or("v2 reached command has no body snapshot")?;
        check(s["bodyId"] == c["recipientBodyId"] && n(s, "firstCommandSequence")? <= sequence as u64, "v2 command/body snapshot binding differs")?;
    }
    Ok(snapshots.len())
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
    let v2 = r["schema"] == "continuum-retained-ordered-body-aero-capture/v2";
    check((v2 || r["schema"] == "continuum-retained-ordered-body-aero-capture/v1") && r["status"] == "observed"
        && r["cleanupStatus"] == "removed-owned-patches" && r["requested"] == true && r["runId"] == run
        && r["processId"] == pid && &r["vesselId"] == vessel && n(&r, "clockFrequency")? == frequency
        && r["path"].as_str() == path.to_str() && outer["sha256"] == hash(path)?, "original tape identity/hash/lifecycle differs")?;
    for field in ["schema", "runId", "processId", "vesselId", "clockFrequency", "status", "cleanupStatus", "topologySha256", "providerEpoch", "roots", "bodyBindings", "methods"] {
        check(r[field] == outer[field], "original sidecar and enclosing receipt diverge")?;
    }
    if v2 {
        check(r["bodyFloatFields"] == json!(BODY_FLOAT_FIELDS) && r["bodyFloatFields"] == outer["bodyFloatFields"]
            && r["bodySnapshotScope"] == "Per-root first reached command properties, before the original native call. Once per distinct recipient; no absence-of-later-writes, acceleration conversion or aggregate/native equivalence claim."
            && r["bodySnapshotScope"] == outer["bodySnapshotScope"], "v2 native property layout/scope differs")?;
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
        && r["outputFloatFields"] == json!(["speedSquared","speed","direction.x","direction.y","direction.z","dragScalar","bodyLiftScalar"])
        && r["extraFloatFields"] == json!(["initialBodyLiftScalar","bodyLiftLocalPosition.x","bodyLiftLocalPosition.y","bodyLiftLocalPosition.z","bodyLiftLocalVector.x","bodyLiftLocalVector.y","bodyLiftLocalVector.z","liftCurveInput"])
        && r["extraDoubleFields"] == json!(["exposedArea","aerodynamicArea","submergedDynamicPressurekPa"]), "declared numeric layouts differ")?;
    let bindings = r["bodyBindings"].as_array().ok_or("native body ledger absent")?;
    let bound = n(&r, "boundParts")?;
    let reachable = n(&r, "reachableParts")?;
    check(bound > 0 && bound <= 4096 && reachable > 0 && reachable <= bound && bindings.len() <= bound as usize * 3, "native owner bounds differ")?;
    let roots = r["roots"].as_array().filter(|a| a.len() == 2).ok_or("two immutable original roots absent")?;
    let mut matched = 0u64; let mut unsupported = 0u64; let mut last = start; let mut first_ids = Vec::new();
    let mut root_ticks = 0u64; let mut reference_ticks = 0u64; let mut copy_ticks = 0u64; let mut command_count = 0u64; let mut snapshot_count = 0usize;
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
        if v2 {
            for owner in bindings {
                check(owner["bodyId"].as_i64().is_some_and(|id| id != 0) && n(owner, "partSlot")? < bound && n(owner, "partId")? > 0
                    && ["part-rb","servo-rb","aero-recipient"].iter().any(|role| owner["role"] == *role), "v2 native alias ledger malformed")?;
            }
            snapshot_count += body_snapshots(root, commands, bindings, bound as usize)?;
        }
        let mut prior_ordinal = 0u64; let mut aero_seen = BTreeSet::new();
        for (i, command) in commands.iter().enumerate() {
            let ordinal = n(command, "ordinal")?;
            check(command["sequence"] == i && ordinal < reachable && ordinal >= prior_ordinal && command["originalReturned"] == true,
                "original command return/preorder differs")?; prior_ordinal = ordinal;
            let row = &rows[ordinal as usize];
            check(command["partId"] == row["partId"] && command["recipientBodyId"].as_i64().is_some_and(|id| id != 0)
                && bindings.iter().any(|owner| owner["bodyId"] == command["recipientBodyId"]), "original command native owner differs")?;
            if v2 { check(bindings.iter().any(|owner| owner["bodyId"] == command["recipientBodyId"] && owner["partId"] == row["partId"] && owner["partSlot"] == row["partSlot"]), "v2 command alias owner differs")?; }
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
            } else {
                check(words(row,"outputFloatBits",7)?[6] == words(row,"extraFloatBits",8)?[0], "unwritten original lift field changed bits")?;
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
        "captureSchema":r["schema"],"firstCommandBodySnapshots":snapshot_count,"bodyPropertyCoverage":if v2 {"first-command-only"} else {"not-captured-v1"},
        "meanObservedRootMilliseconds":root_ticks as f64*ms/2.,"meanReferenceMilliseconds":reference_ticks as f64*ms/2.,"meanRecordingMilliseconds":copy_ticks as f64*ms/2.,
        "scope":"Independent raw IEEE input/scalar/ordered actual-tail command math under frozen numerical bounds. Actual queued tape is bound and ordered but queued input values are not reconstructed. No candidate force publication, complete aero replacement or clean FPS claim."}))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn first_command_body_properties_reject_incomplete_or_foreign_binding() {
        let mut bits = vec![0u32; 27];
        bits[0] = 2f32.to_bits(); bits[7] = 1f32.to_bits();
        bits[14] = 2f32.to_bits(); bits[15] = 3f32.to_bits(); bits[16] = 4f32.to_bits(); bits[20] = 1f32.to_bits();
        bits[21] = (-0f32).to_bits();
        let root = json!({"startedTicks":100,"endedTicks":200,"bodySnapshots":[{
            "index":0,"bodyId":-42,"firstCommandSequence":0,"startedTicks":110,"endedTicks":120,
            "floatBits":bits,"alive":true,"isKinematic":false,"dynamic":true,
            "activeSelf":true,"activeInHierarchy":true,"sleeping":false}]});
        let commands = vec![json!({"recipientBodyId":-42,"bodySnapshotIndex":0}), json!({"recipientBodyId":-42,"bodySnapshotIndex":0})];
        // Two alias generations/parts share one actual body, and signed zero remains a bit value.
        let bindings = vec![json!({"bodyId":-42,"partId":1}),json!({"bodyId":-42,"partId":2})];
        assert_eq!(body_snapshots(&root,&commands,&bindings,2).unwrap(),1);
        assert_eq!(root["bodySnapshots"][0]["floatBits"][21],json!(0x80000000u32));
        for (field, value) in [("bodyId",json!(99)),("firstCommandSequence",json!(1)),("endedTicks",json!(201)),("dynamic",json!(false)),("alive",json!(false))] {
            let mut bad=root.clone();bad["bodySnapshots"][0][field]=value;
            assert!(body_snapshots(&bad,&commands,&bindings,2).is_err(),"{field}");
        }
        for (index,value) in [(0,0f32.to_bits()),(14,(-1f32).to_bits()),(15,f32::INFINITY.to_bits()),(7,0f32.to_bits())] {
            let mut bad=root.clone();bad["bodySnapshots"][0]["floatBits"][index]=json!(value);
            assert!(body_snapshots(&bad,&commands,&bindings,2).is_err());
        }
        let mut bad_commands=commands.clone();bad_commands[1]["bodySnapshotIndex"]=json!(1);
        assert!(body_snapshots(&root,&bad_commands,&bindings,2).is_err());
        let mut incomplete=root.clone();incomplete["bodySnapshots"]=json!([]);
        assert!(body_snapshots(&incomplete,&commands,&bindings,2).is_err());
    }
}
