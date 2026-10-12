use crate::{hash,launch::Diagnostics,Result};
use serde_json::{json,Value};
use std::path::Path;
fn n(v:&Value,k:&str)->Result<u64>{v[k].as_u64().ok_or_else(||format!("fixed-owner field absent: {k}"))}
fn check(v:bool,s:&str)->Result<()>{if v{Ok(())}else{Err(s.into())}}
pub fn summarize(d:&Diagnostics,p:&Value,pid:u32)->Result<Value>{
    let run=p["runId"].as_str().ok_or("probe run absent")?;
    let start=n(p,"captureStartClockTimestamp")?;let end=n(p,"captureEndClockTimestamp")?;
    let frequency=n(p,"clockFrequency")?;let frames=n(p,"completedFrames")?;
    let contexts=p["frames"].as_array().ok_or("probe contexts absent")?;
    check(p["status"]=="complete"&&frequency>0&&frames>0&&start>0&&end>start&&frames as usize<=contexts.len(),"enclosing probe invalid")?;
    let vessel=&contexts[0]["vesselId"];check(vessel.as_str().is_some_and(|s|!s.is_empty())&&contexts.iter().take(frames as usize).all(|f|&f["vesselId"]==vessel),"probe vessel changed")?;
    let ms=1000./frequency as f64;let mut result=json!({"schema":"continuum-fixed-owner-and-treatment-summary/v1","runId":run,"processId":pid,"status":"observed"});
    if let Some(mode)=d.aero_step_context.as_deref(){
        let r=&p["aeroStepContext"];
        check(r["schema"]=="continuum-aero-step-context/v1"&&r["mode"]==mode&&r["runId"]==run&&r["processId"]==pid&&&r["vesselId"]==vessel
            &&r["status"]=="completed-experimental"&&r["cleanupStatus"]=="removed-owned-hooks"&&r["dragTransactionRequested"]==d.aero_drag_transaction
            &&n(r,"clockFrequency")?==frequency,"requested aero treatment not observed/closed")?;
        for k in ["errors","originalExceptions","recursionStops","safetyStops","verificationMismatches","droppedWindows","captureFallbacks"]{check(n(r,k)?==0,"aero owned fault/incomplete coverage")?;}
        let windows=r["windows"].as_array().filter(|a|!a.is_empty()).ok_or("aero root windows absent")?;
        let mut complete=0;let mut roots=0;let mut fixed=0;let mut candidate_drag=0;
        for w in windows{
            let a=n(w,"fixedStartedTicks")?;let b=n(w,"fixedEndedTicks")?;
            check(w["completed"]==true&&w["captureActiveAtEnd"]==true&&w["fallback"].is_null()&&w["mismatch"].is_null()
                &&a>=start&&b<=end&&b>=a&&n(w,"contextCompleteTicks")?>=n(w,"rootTicks")?,"aero window outside capture or incomplete")?;
            complete+=n(w,"contextCompleteTicks")?;roots+=n(w,"rootTicks")?;fixed+=b-a;
            if d.aero_drag_transaction{
                let calls=n(w,"setDragCalls")?;let candidates=n(w,"candidateSetDragCalls")?;
                check(calls>0&&calls==n(w,"originalSetDragCalls")?+candidates&&n(w,"shadowSetDragCalls")?==n(w,"verifiedSetDragCalls")?,"complete drag accounting differs")?;
                if mode=="query"{check(calls==candidates&&n(w,"originalSetDragCalls")?==0&&n(w,"verifiedSetDragCalls")?==0,"query did not publish complete drag donor")?;}
                candidate_drag+=candidates;
            }
        }
        if d.aero_drag_transaction{
            check(r["dragQualificationRoots"]==2&&n(r,"dragQualificationCalls")?>0&&r["staticSetDragCallsites"]==1
                &&r["dragMaximumRelativeError"].as_f64().is_some_and(|v|v.is_finite()&&v>=0.&&v<=2e-5),"two unchanged-tolerance drag proofs absent")?;
            check(candidate_drag==n(r,"captureCandidateSetDragCalls")?,"drag publication total differs")?;
        }
        result["aero"]=json!({"mode":mode,"completeDragRequested":d.aero_drag_transaction,"roots":windows.len(),"candidateDragCalls":candidate_drag,
            "meanRootMilliseconds":roots as f64*ms/windows.len() as f64,"meanCompleteContextMilliseconds":complete as f64*ms/windows.len() as f64,
            "meanFixedOwnerMilliseconds":fixed as f64*ms/windows.len() as f64,"dragMaximumRelativeError":r["dragMaximumRelativeError"],
            "scope":"Original force integration and publication remain authoritative; context includes copy/admission/arithmetic/reuse/bookkeeping. Root and context are nested within FI fixed owner. No same-input or whole-frame speedup claim."});
    }
    if d.fixed_step_owner_attribution{
        let r=&p["fixedStepOwnerAttribution"];
        check(r["schema"]=="continuum-fixed-step-owner-attribution/v1"&&r["runId"]==run&&r["processId"]==pid&&&r["vesselId"]==vessel
            &&r["status"]=="observed"&&r["cleanupStatus"]=="removed-owned-patches"&&n(r,"clockFrequency")?==frequency
            &&r["phase"]=="UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate"
            &&r["targetCount"]==6&&r["patchedMethods"]==6&&r["preinstallPatchGraph"]==r["restoredPatchGraph"]
            &&r["installedPatchGraph"]==r["precleanupPatchGraph"],"requested fixed owner absent or ownership not closed")?;
        for k in ["errors","ownershipErrors","contextErrors","originalExceptions","droppedWindows"]{check(n(r,k)?==0,"fixed-owner observer fault")?;}
        for k in ["preinstallPatchGraph","installedPatchGraph","precleanupPatchGraph","restoredPatchGraph"]{
            check(r[k].as_array().is_some_and(|a|!a.is_empty()&&a.iter().all(|v|v.as_str().is_some_and(|s|!s.is_empty()))),"fixed-owner graph evidence absent")?;
        }
        let targets=r["targets"].as_array().filter(|a|a.len()==6).ok_or("six fixed owner identities absent")?;
        let tokens=[100677455,100677504,100677477,100677715,100665296,100665304];
        for (slot,t) in targets.iter().enumerate(){
            let (sha,mvid)=if slot<4 {("8a20892953fc14c02f352b393eb6712c665156d94a7d846d16c20a7de3e22f27","10657063-2fc3-43a7-84fa-d39e75e877bf")}
                else {("6851b17fd2a73cb09e399f55a3322de3eaa8f047136251f52a343fae2f3df929","30fbe3c4-e77e-4abc-895b-a635d5924a08")};
            check(t["slot"]==slot&&t["metadataToken"]==tokens[slot]&&t["assemblySha256"]==sha&&t["assemblyMvid"]==mvid
                &&t["ilSha256"].as_str().is_some_and(|v|v.len()==64&&v.bytes().all(|b|b.is_ascii_hexdigit())),"fixed target identity differs")?;
        }
        let windows=r["windows"].as_array().filter(|a|!a.is_empty()).ok_or("fixed-owner phase windows absent")?;
        check(windows.len() as u64==n(r,"completedWindows")?&&n(r,"startedWindows")?==n(r,"completedWindows")?,"fixed windows truncated")?;
        let mut calls=[0u64;6];let mut inclusive=[0u64;6];let mut exclusive=[0u64;6];let mut inside=[0u64;6];let mut outside=[0u64;6];let mut phase=0;let mut last=start;
        for (sequence,w) in windows.iter().enumerate(){
            let a=n(w,"startedTicks")?;let b=n(w,"endedTicks")?;let elapsed=n(w,"elapsedTicks")?;
            check(w["sequence"]==sequence&&w["captureActiveAtEnd"]==true&&a>=last&&b>=a&&b<=end&&elapsed==b-a,"fixed phase clocks not enclosed")?;last=b;phase+=elapsed;
            let entries=w["entries"].as_array().filter(|a|a.len()==6).ok_or("fixed entries incomplete")?;let mut own=0;
            for (slot,e) in entries.iter().enumerate(){let count=n(e,"calls")?;let inc=n(e,"inclusiveTicks")?;let own_ticks=n(e,"selfTicks")?;
                check(e["slot"]==slot&&own_ticks<=inc&&inc<=elapsed*count&&(count>0||inc==0),"fixed body clock/call mismatch")?;
                if slot>=4{check(n(e,"insideFeedCalls")?+n(e,"outsideFeedCalls")?==count&&n(e,"insideFeedTicks")?+n(e,"outsideFeedTicks")?==inc,"MechJeb feed nesting differs")?;}
                calls[slot]+=count;inclusive[slot]+=inc;exclusive[slot]+=own_ticks;inside[slot]+=n(e,"insideFeedTicks")?;outside[slot]+=n(e,"outsideFeedTicks")?;own+=own_ticks;
            }
            check(own<=elapsed,"observed self exceeds enclosing phase")?;
        }
        check(calls.iter().sum::<u64>()==n(r,"capturedCalls")?,"fixed call total differs")?;
        let owners=r["stateOwners"].as_array().filter(|a|!a.is_empty()).ok_or("actual MechJeb state owners absent")?;
        let update_total=owners.iter().map(|s|n(s,"updateCalls")).collect::<Result<Vec<_>>>()?.iter().sum::<u64>();
        let analyze_total=owners.iter().map(|s|n(s,"analyzeCalls")).collect::<Result<Vec<_>>>()?.iter().sum::<u64>();
        check(update_total==calls[4]&&analyze_total==calls[5],"MechJeb per-state reached counts differ")?;
        result["fixedOwners"]=json!({"phaseWindows":windows.len(),"meanPhaseMilliseconds":phase as f64*ms/windows.len() as f64,
            "rows":targets.iter().enumerate().map(|(slot,t)|json!({"target":t,"calls":calls[slot],"coverage":if calls[slot]>0{"executed"}else{"not-reached"},
                "meanInclusiveMillisecondsPerStep":inclusive[slot] as f64*ms/windows.len() as f64,"meanObservedSelfMillisecondsPerStep":exclusive[slot] as f64*ms/windows.len() as f64,
                "insideFeedMillisecondsPerStep":inside[slot] as f64*ms/windows.len() as f64,"outsideFeedMillisecondsPerStep":outside[slot] as f64*ms/windows.len() as f64})).collect::<Vec<_>>(),
            "stateOwners":owners,"scope":"Only six named bulk boundaries; inclusive rows overlap, self removes directly observed children. Remaining phase includes unobserved callbacks/native work/observer overhead and is not removable-time evidence."});
    }
    if let Some(selection)=&d.trail_emitter{
        let r=&p["trailEmitterCapture"];
        check(r["schema"]=="continuum-trail-emitter-capture/v1"&&r["status"]=="complete"&&r["cleanupStatus"]=="readonly-owner-released-no-hooks-or-physics-writes"
            &&r["runId"]==run&&r["processId"]==pid&&&r["vesselId"]==vessel&&r["mode"]==selection.mode&&r["partSelector"]==selection.part
            &&n(r,"clockFrequency")?==frequency&&n(r,"frames")?>1&&n(r,"partId")?>0
            &&r["recordingPath"].as_str()==selection.recording.to_str(),"requested measured emitter capture not completed/bound")?;
        let digest=hash(&selection.recording)?;check(r["recordingSha256"]==digest,"emitter payload changed")?;
        let scene=d.multiscale_scene.as_ref().ok_or("emitter scene absent")?;
        check(r["sceneSha256"]==hash(&scene.join("manifest.json"))?&&r["wholeCraftSha256"]==hash(&scene.join("whole-craft/manifest.json"))?,"emitter cold scene drift")?;
        let sidecar:Value=crate::read(Path::new(&format!("{}.json",selection.recording.display())))?;
        for key in ["schema","status","cleanupStatus","runId","processId","vesselId","mode","partSelector","partId","clockFrequency","frames","recordingPath","recordingSha256","sceneSha256","wholeCraftSha256"] {
            check(sidecar[key]==r[key],"emitter bound sidecar/Probe fields diverged")?;
        }
        result["emitter"]=json!({"mode":selection.mode,"partId":r["partId"],"frames":r["frames"],"recordingSha256":digest,"sceneSha256":r["sceneSha256"],"captureMilliseconds":n(r,"captureTicks")? as f64*ms,
            "scope":"Measured part motion with artistic births; binary time/coordinate decoding and native visible consumer remain separate gates."});
    }
    Ok(result)
}
