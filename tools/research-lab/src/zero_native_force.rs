use crate::Result;
use serde_json::{json, Value};

fn n(v: &Value, k: &str) -> Result<u64> {
    v[k].as_u64().ok_or_else(|| format!("zero-force receipt field absent: {k}"))
}
fn require(ok: bool, reason: &str) -> Result<()> {
    if ok { Ok(()) } else { Err(reason.into()) }
}
fn vector(v: &Value, size: usize) -> Result<Vec<f64>> {
    v.as_array().filter(|a| a.len()==size).ok_or("native canary vector shape differs")?
        .iter().map(|x| x.as_f64().filter(|n| n.is_finite()).ok_or("native canary nonfinite vector".into())).collect()
}
fn distance(a: &[f64], b: &[f64]) -> f64 {
    a.iter().zip(b).map(|(x,y)| (x-y)*(x-y)).sum::<f64>().sqrt()
}
fn qualify_native(q: &Value, start: u64, frequency: u64) -> Result<Value> {
    require(q["schema"]=="continuum-zero-native-force-canary/v1" && q["status"]=="passed"
        && q["isolatedScene"]==true && q["cleanupComplete"]==true && q["cleanupPending"]==false
        && q["defaultAutoSimulationBefore"]==q["defaultAutoSimulationAfter"] && n(q,"failedCases")?==0
        && n(q,"clockFrequency")?==frequency && n(q,"startedTicks")?>0
        && n(q,"endedTicks")?>n(q,"startedTicks")? && n(q,"endedTicks")?<start,
        "native zero canary isolation/cleanup/clocks differ")?;
    for (key, tolerance) in [("positionToleranceMeters",1e-6),("velocityToleranceMetersPerSecond",1e-6),
        ("angularVelocityToleranceRadiansPerSecond",1e-6),("rotationToleranceDegrees",1e-5)] {
        require(q[key].as_f64()==Some(tolerance),"native zero canary frozen tolerance differs")?;
    }
    let rows=q["rows"].as_array().filter(|a|a.len()==159).ok_or("native canary body cases missing")?;
    let mut zero_cases=std::collections::BTreeSet::new(); let mut forwarding=std::collections::BTreeSet::new(); let mut analytic_cases=0;
    for row in rows {
        let state=row["state"].as_str().ok_or("native body state absent")?;
        let command=row["command"].as_str().ok_or("native command absent")?;
        let pending=row["pendingCommands"].as_str().ok_or("native pending commands absent")?;
        let encoding=row["zeroEncoding"].as_str().ok_or("native zero encoding absent")?;
        require(["dynamic-awake","dynamic-sleeping","kinematic","inactive"].contains(&state)
            && ["force","torque","force-at-position"].contains(&command)
            && ["none","linear","angular","linear-and-angular"].contains(&pending)
            && row["passed"]==true && row["setupPassed"]==true && row["pendingWakePassed"]==true
            && row["originalException"]==row["candidateException"]
            && row["originalZeroMessages"]==row["candidateZeroMessages"],"native canary setup/messages/exceptions differ")?;
        let bits=row["inputBits"].as_array().filter(|a|a.len()==3).ok_or("native input bits absent")?;
        let zero=bits.iter().all(|b|b.as_u64().is_some_and(|x|x==0||x==0x80000000));
        require(row["candidateOmitted"]==(zero && row["mode"]==0),"native candidate exact-zero policy differs")?;
        if let Some(expected) = match encoding {
            "positive-zero"=>Some([0,0,0]), "negative-zero"=>Some([0x80000000;3]),
            "mixed-zero"=>Some([0,0x80000000,0]), _=>None,
        } {
            require(row["mode"]==0 && bits.iter().zip(expected).all(|(b,e)|b.as_u64()==Some(e))
                && zero_cases.insert((state,command,pending,encoding)),"native signed-zero population differs")?;
        } else {
            let mode=n(row,"mode")?;
            let expected=match encoding {
                "minimum-positive-subnormal" if mode==0=>[1,0,0],
                "nonzero-forwarding" if mode==0=>[0.001f32.to_bits(),(-0.002f32).to_bits(),0.003f32.to_bits()],
                "nonzero-mode-forwarding" if [1,2,5].contains(&mode)=>[0,0,0],
                _=>return Err("unknown native forwarding control".into()),
            };
            require(state=="dynamic-awake" && pending=="linear-and-angular"
                && bits.iter().zip(expected).all(|(b,e)|b.as_u64()==Some(e as u64))
                && forwarding.insert((command,encoding,mode)),"native forwarding population differs")?;
        }
        for suffix in ["Before","AfterPending","AfterZero","AfterSimulate"] {
            let a=&row[format!("original{suffix}")]; let b=&row[format!("candidate{suffix}")];
            for key in ["sleeping","isKinematic","activeSelf","activeInHierarchy"] {
                require(a[key].is_boolean() && a[key]==b[key],"native zero body state differs")?;
            }
            if suffix=="Before" {
                require(a["isKinematic"]==(state=="kinematic") && a["activeSelf"]==(state!="inactive")
                    && a["activeInHierarchy"]==(state!="inactive")
                    && (state!="dynamic-sleeping" || a["sleeping"]==true)
                    && (state!="dynamic-awake" || a["sleeping"]==false),"native fixture label/state differs")?;
            }
            for key in ["position","velocity","angularVelocity"] {
                require(distance(&vector(&a[key],3)?,&vector(&b[key],3)?)<=1e-6,"native zero pose/velocity differs")?;
            }
            let ar=vector(&a["rotation"],4)?;let br=vector(&b["rotation"],4)?;
            let inverse: Vec<_>=br.iter().map(|x|-x).collect();
            let rotation=4.*(distance(&ar,&br).min(distance(&ar,&inverse))/2.).min(1.).asin()*180./std::f64::consts::PI;
            require(rotation<=1e-5,"native zero quaternion differs")?;
        }
        if zero && row["mode"]==0 && state.starts_with("dynamic-") {
            analytic_cases+=1;
            let linear=pending=="linear"||pending=="linear-and-angular";
            let angular=pending=="angular"||pending=="linear-and-angular";
            let dv=if linear {[0.02,-0.01,0.03]}else{[0.;3]};
            let dw=if angular {[0.004,-0.002,0.001]}else{[0.;3]};
            for arm in ["original","candidate"] {
                let before=&row[format!("{arm}Before")];let after=&row[format!("{arm}AfterSimulate")];
                for (key,expected) in [("velocity",dv),("angularVelocity",dw)] {
                    let a=vector(&after[key],3)?;let b=vector(&before[key],3)?;
                    let delta: Vec<_>=a.iter().zip(b).map(|(x,y)|x-y).collect();
                    require(distance(&delta,&expected)<=1e-6,"native pending-force analytic oracle differs")?;
                }
                if state=="dynamic-sleeping" && pending!="none" {
                    require(row[format!("{arm}AfterPending")]["sleeping"]==false,"native pending force did not wake sleeping fixture")?;
                }
            }
        }
    }
    require(zero_cases.len()==144 && analytic_cases==72 && forwarding.len()==15,"native zero/forwarding coverage incomplete")?;
    let receivers=q["receivers"].as_array().filter(|a|a.len()==6).ok_or("native receiver cases absent")?;
    let mut population=std::collections::BTreeSet::new();
    for row in receivers {
        let receiver=row["receiver"].as_str().ok_or("native receiver absent")?;
        let command=row["command"].as_str().ok_or("native receiver command absent")?;
        require(["managed-null","destroyed-native-object"].contains(&receiver)
            && ["force","torque","force-at-position"].contains(&command) && population.insert((receiver,command))
            && row["candidateOmitted"]==false && row["originalException"]==row["candidateException"]
            && row["originalMessages"]==row["candidateMessages"],"invalid native receiver forwarding differs")?;
    }
    Ok(json!({"bodyCases":rows.len(),"signedZeroCases":zero_cases.len(),"forwardingCases":forwarding.len(),"analyticPendingForceCases":analytic_cases,"receiverCases":receivers.len(),"status":"independently-rederived"}))
}
pub fn summarize(p: &Value, pid: u32) -> Result<Value> {
    let run = p["runId"].as_str().filter(|s| !s.is_empty()).ok_or("probe run absent")?;
    let start = n(p, "captureStartClockTimestamp")?;
    let end = n(p, "captureEndClockTimestamp")?;
    let frequency = n(p, "clockFrequency")?;
    let vessel = p["frames"][0]["vesselId"].as_str().filter(|s| !s.is_empty()).ok_or("probe vessel absent")?;
    require(p["status"] == "complete" && start > 0 && end > start && frequency > 0,
        "enclosing zero-force capture incomplete")?;
    let q = &p["zeroNativeForceCanary"];
    require(q["passed"] == true && q["runId"] == run && q["processId"] == pid
        && q["unity"] == p["unity"] && q["cleanupStatus"] == "scene-unloaded",
        "isolated native zero-force qualification absent or foreign")?;
    let native=qualify_native(q,start,frequency)?;
    let r = &p["zeroNativeForcePruning"];
    require(r["schema"] == "continuum-zero-native-force-pruning/v1" && r["status"] == "observed"
        && r["cleanupStatus"] == "removed-owned-patches" && r["runId"] == run
        && r["processId"] == pid && r["vesselId"] == vessel && n(r, "clockFrequency")? == frequency
        && r["nativeQualificationStatus"] == "qualified-isolated-zero-command"
        && r["graphAdmissions"] == true && r["graphInstalled"] == true && r["graphReleased"] == true,
        "zero-force native/capture/graph lifecycle incomplete")?;
    for field in ["errors", "originalExceptions", "unsupportedZeroCalls", "bodyGuardNativeReads"] {
        require(n(r, field)? == 0, "zero-force fault, unsupported command or unexpected native guard")?;
    }
    require(n(r,"coldCaptureRoots")?==0 && n(r,"warmupCandidateRoots")?>0
        && n(r,"warmupCandidateSkippedForceCalls")?>0 && n(r,"warmupCandidateSkippedTorqueCalls")?>0
        && (r["mode"]!="compare" || n(r,"warmupOriginalRoots")?>0
            && n(r,"warmupOriginalForwardedForceCalls")?>0 && n(r,"warmupOriginalForwardedTorqueCalls")?>0),
        "zero-force original/candidate warmup incomplete")?;
    require(n(r,"staticPlainForceSites")? == 1 && n(r,"staticPlainTorqueSites")? == 1
        && n(r,"staticUnchangedAccelerationSites")? == 2 && n(r,"staticUnchangedPointSites")? == 1
        && n(r,"shapeAdmissions")? == 1 && n(r,"patchedMethods")? == 1,
        "zero-force original callsite shape differs")?;
    let methods = r["methods"].as_array().filter(|a| !a.is_empty()).ok_or("zero-force method graph absent")?;
    let integrators: Vec<_> = methods.iter().filter(|m| m["patched"] == true).collect();
    require(integrators.len() == 1, "zero-force patched method set differs")?;
    let method = integrators[0];
    require(method["declaringType"] == "FlightIntegrator" && method["method"] == "Integrate"
        && method["metadataToken"] == 100677461 && method["instructionCount"] == 147
        && method["assemblySha256"] == "8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27"
        && method["assemblyMvid"] == "10657063-2fc3-43a7-84fa-d39e75e877bf"
        && method["ilSha256"] == "71f3e8ba1599644c13be502e24d7463524c98188b1f3a075b898c9a8430a0f04",
        "zero-force pinned original Integrate identity differs")?;
    let windows = r["windows"].as_array().ok_or("zero-force windows absent")?;
    let mode = r["mode"].as_str().ok_or("zero-force mode absent")?;
    require((mode == "compare" && windows.len() == 4 && n(r,"completedComparisons")? == 4
        && n(r,"controlComparisonRoots")? == 2 && n(r,"candidateComparisonRoots")? == 2)
        || (mode == "query" && windows.is_empty()), "zero-force treatment coverage differs")?;
    let mut last = start;
    let mut roots = Vec::new();
    for (i, w) in windows.iter().enumerate() {
        let candidate = i == 1 || i == 2;
        let a = n(w,"startedTicks")?; let b = n(w,"endedTicks")?;
        require(w["sequence"] == i && w["pair"] == i/2 && w["captured"] == true
            && w["treatment"] == if candidate { "candidate" } else { "original" }
            && w["originalReturned"] == true && w["topologyIntact"] == true
            && n(w,"rowCount")? == n(r,"reachableParts")? && a >= last && b > a && b <= end
            && n(w,"completeTicks")? == b-a && n(w,"unsupportedZeroCalls")? == 0
            && n(w,"bodyGuardNativeReads")? == 0, "zero-force root coverage/order/clocks differ")?;
        last = b;
        for (calls, forwarded, skipped, zero) in [("forceCalls","forwardedForceCalls","skippedForceCalls","zeroForceCalls"),
            ("torqueCalls","forwardedTorqueCalls","skippedTorqueCalls","zeroTorqueCalls")] {
            require(n(w,calls)? == n(w,forwarded)? + n(w,skipped)? && n(w,zero)? <= n(w,calls)?
                && n(w,skipped)? == if candidate { n(w,zero)? } else { 0 },
                "zero-force command conservation differs")?;
        }
        roots.push(json!({"treatment":w["treatment"],"completeMilliseconds":(b-a) as f64*1000./frequency as f64,
            "omittedCalls":n(w,"skippedForceCalls")?+n(w,"skippedTorqueCalls")?,
            "firstCandidateInvocation":w["firstCandidateInvocation"]}));
    }
    let mut pairs = Vec::new();
    for pair in windows.chunks_exact(2) {
        let original = if pair[0]["treatment"] == "original" { &pair[0] } else { &pair[1] };
        let candidate = if pair[0]["treatment"] == "candidate" { &pair[0] } else { &pair[1] };
        for key in ["rowCount","forceCalls","torqueCalls","zeroForceCalls","zeroTorqueCalls"] {
            require(original[key] == candidate[key], "adjacent zero-force pair command population changed")?;
        }
        let o = n(original,"completeTicks")? as f64; let c = n(candidate,"completeTicks")? as f64;
        pairs.push(json!({"originalMilliseconds":o*1000./frequency as f64,
            "candidateMilliseconds":c*1000./frequency as f64,"observedReductionFraction":1.-c/o}));
    }
    Ok(json!({"schema":"continuum-zero-native-force-summary/v1","status":"observed","runId":run,"processId":pid,
        "mode":mode,"nativeQualification":native,"roots":roots,"pairs":pairs,"capturedRoots":r["captureRoots"],
        "scope":"Adjacent changing-state root comparisons include installed common hooks and all candidate guards; isolated native zero-command behavior, not whole-game equivalence or clean FPS.",
        "promotion":"Research candidate. Two pairs are a screening observation, not a confidence interval or playable milestone."}))
}
