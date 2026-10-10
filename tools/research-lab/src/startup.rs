use crate::Result;
use serde_json::{json, Value};

pub fn deferred_terrain(log: &str) -> Result<Value> {
    let marker = "[Continuum deferred terrain] ";
    let lines: Vec<_> = log.lines().filter_map(|line| line.split_once(marker).map(|(_, value)| value)).collect();
    if !lines.iter().any(|line| line.starts_with("status=installed ")) {
        return Err("requested deferred terrain installation was not observed".into());
    }
    let mut journals = Vec::new();
    for line in &lines {
        if !line.starts_with("status=journal ") { continue; }
        let fields: std::collections::BTreeMap<_,_> = line.split_whitespace().filter_map(|field| field.split_once('=')).collect();
        let mut row = json!({});
        for key in ["deferred", "materialized", "pending", "failures", "unsupported"] {
            let number = fields.get(key).ok_or(format!("deferred terrain {key} absent"))?
                .parse::<u64>().map_err(|error| error.to_string())?;
            row[key] = json!(number);
        }
        for key in ["captureSeconds", "fulfillmentSeconds"] {
            let number = fields.get(key).ok_or(format!("deferred terrain {key} absent"))?
                .parse::<f64>().map_err(|error| error.to_string())?;
            if !number.is_finite() || number < 0.0 { return Err("deferred terrain cost invalid".into()); }
            row[key] = json!(number);
        }
        row["phase"] = json!(fields.get("phase").ok_or("deferred terrain phase absent")?);
        if row["failures"] != 0 || row["deferred"].as_u64() != row["materialized"].as_u64().and_then(|value| value.checked_add(row["pending"].as_u64()?)) {
            return Err("deferred terrain failure or obligation conservation violation".into());
        }
        journals.push(row);
    }
    let menu = journals.iter().find(|row| row["phase"] == "menu").ok_or("deferred terrain menu obligations absent")?.clone();
    Ok(json!({"schema":"continuum-startup-deferred-terrain/v1","status":"observed","menu":menu,"journals":journals,"nativeLines":lines,
        "scope":"owned-log-obligations-only; pending-debt-is-not-completed-placement-or-gameplay-proof"}))
}

#[cfg(test)]
mod deferred_tests {
    use super::*;
    #[test]
    fn pending_debt_requires_menu_witness_and_conserved_ownership() {
        let installed = "[Continuum deferred terrain] status=installed supported=analytic\n";
        let menu = "[Continuum deferred terrain] status=journal phase=menu deferred=2 materialized=0 pending=2 failures=0 unsupported=0 captureSeconds=0.01 fulfillmentSeconds=0 menuPending=explicit-deferred-debt\n";
        let value = deferred_terrain(&format!("{installed}{menu}")).unwrap();
        assert_eq!(value["menu"]["pending"], 2);
        assert!(deferred_terrain(installed).is_err());
        assert!(deferred_terrain(&format!("{installed}{}", menu.replace("pending=2", "pending=0"))).is_err());
        assert!(deferred_terrain(&format!("{installed}{}", menu.replace("phase=menu", "phase=shutdown"))).is_err());
    }
}

fn clock(text: &str) -> Option<f64> {
    let mut fields = text.split(':');
    let hour = fields.next()?.parse::<u32>().ok()?;
    let minute = fields.next()?.parse::<u32>().ok()?;
    let second = fields.next()?.parse::<f64>().ok()?;
    if fields.next().is_some()
        || hour >= 24
        || minute >= 60
        || !second.is_finite()
        || !(0.0..60.0).contains(&second)
    {
        return None;
    }
    Some(hour as f64 * 3600.0 + minute as f64 * 60.0 + second)
}

pub fn timeline(log: &str) -> Result<Value> {
    let markers = [
        ("log-start", "Log Initiated for Kerbal Space Program"),
        ("assets-finished", "AssetLoader: Finished loading."),
        ("first-part", "PartLoader: Compiling Part '"),
        (
            "expansions-finished",
            "ExpansionsLoader: Expansions loaded in ",
        ),
        (
            "loading-systems-finished",
            "Loading Systems: Elapsed time is ",
        ),
        ("menu-transition", "Scene Change : From LOADING to MAINMENU"),
        ("observer", "[Continuum reconstruction] version="),
    ];
    let mut origin = None;
    let mut previous = None;
    let mut day = 0.0;
    let mut events = Vec::new();
    let mut seen = std::collections::BTreeSet::new();
    let mut replay_blocks = Vec::new();
    let mut replay_block_count = 0;
    for (line_index, line) in log.lines().enumerate() {
        if let Some(name) = line
            .strip_prefix("#### BEGIN ")
            .and_then(|s| s.strip_suffix(" ####"))
        {
            if name != "MODULEMANAGER LOG" && name != "PATCH LOG" {
                return Err("unknown replay log boundary".into());
            }
            replay_blocks.push(name);
            replay_block_count += 1;
            continue;
        }
        if let Some(name) = line
            .strip_prefix("#### END ")
            .and_then(|s| s.strip_suffix(" ####"))
        {
            if replay_blocks.pop() != Some(name) {
                return Err("unmatched replay log boundary".into());
            }
            continue;
        }
        if !replay_blocks.is_empty() {
            continue;
        }
        let Some(rest) = line.strip_prefix("[LOG ") else {
            continue;
        };
        let Some((timestamp, message)) = rest.split_once("] ") else {
            continue;
        };
        let current = clock(timestamp).ok_or("invalid log timestamp")?;
        if let Some(last) = previous {
            if current < last {
                if last - current > 43200.0 && day == 0.0 {
                    day = 86400.0;
                } else {
                    return Err("log clock moved backward; timing invalid".into());
                }
            }
        }
        previous = Some(current);
        let absolute = day + current;
        for (name, pattern) in markers {
            if !message.contains(pattern) {
                continue;
            }
            if name == "log-start" {
                if origin.is_some() {
                    return Err("multiple launches in one log".into());
                }
                origin = Some(absolute);
            }
            if !seen.insert(name) {
                if name == "first-part" {
                    continue;
                }
                return Err(format!("repeated startup boundary: {name}"));
            }
            if name == "observer" && !seen.contains("menu-transition") {
                return Err("observer precedes menu transition".into());
            }
            let start = origin.ok_or("startup marker precedes log origin")?;
            events.push(json!({"name":name,"line":line_index+1,
                "secondsSinceLogStart":absolute-start,"message":message}));
        }
    }
    if !replay_blocks.is_empty() {
        return Err("unterminated replay log".into());
    }
    if !seen.contains("log-start")
        || !seen.contains("menu-transition")
        || !seen.contains("observer")
    {
        return Err("incomplete launch: log start, menu and observer required".into());
    }
    let intervals: Vec<Value> = events
        .windows(2)
        .map(|pair| {
            json!({
                "from":pair[0]["name"],"to":pair[1]["name"],
                "wallSeconds":pair[1]["secondsSinceLogStart"].as_f64().unwrap()
                    - pair[0]["secondsSinceLogStart"].as_f64().unwrap()
            })
        })
        .collect();
    Ok(
        json!({"schema":"continuum-startup-timeline/v1","events":events,"intervals":intervals,
        "ignoredReplayBlocks":replay_block_count,
        "qualification":"wall-clock-log-boundaries-only-not-exclusive-costs-or-speedup-proof",
        "missingMarkers":markers.iter().filter(|(name,_)| !seen.contains(name)).map(|(name,_)| *name).collect::<Vec<_>>()}),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn midnight_and_intervals() {
        let result = timeline("[LOG 23:59:59.000] Log Initiated for Kerbal Space Program\n[LOG 00:00:01.000] AssetLoader: Finished loading.\n[LOG 00:00:03.000] Scene Change : From LOADING to MAINMENU\n[LOG 00:00:03.500] [Continuum reconstruction] version=stock").unwrap();
        assert_eq!(result["events"][3]["secondsSinceLogStart"], 4.5);
        assert_eq!(result["intervals"][1]["wallSeconds"], 2.0);
        assert_eq!(result["missingMarkers"].as_array().unwrap().len(), 3);
    }
    #[test]
    fn rejects_partial_repeated_and_backward_clocks() {
        assert!(timeline("[LOG 12:00:00.000] Log Initiated for Kerbal Space Program").is_err());
        assert!(timeline("[LOG 12:00:00.000] Log Initiated for Kerbal Space Program\n[LOG 11:59:59.000] anything").is_err());
        assert!(timeline("[LOG 12:00:00.000] Log Initiated for Kerbal Space Program\n[LOG 12:00:01.000] Log Initiated for Kerbal Space Program").is_err());
        assert!(clock("25:00:00").is_none());
        assert!(clock("12:00:NaN").is_none());
    }
    #[test]
    fn excludes_replayed_logs_and_rejects_terminal_disorder() {
        let prefix = "[LOG 12:00:00.000] Log Initiated for Kerbal Space Program\n";
        let suffix = "[LOG 12:00:04.000] Scene Change : From LOADING to MAINMENU\n[LOG 12:00:05.000] [Continuum reconstruction] version=stock";
        let replay = "#### BEGIN MODULEMANAGER LOG ####\n[LOG 03:00:00.000] old log\n#### BEGIN PATCH LOG ####\n[LOG 02:00:00.000] Scene Change : From LOADING to MAINMENU\n#### END PATCH LOG ####\n#### END MODULEMANAGER LOG ####\n";
        assert_eq!(
            timeline(&format!("{prefix}{replay}{suffix}")).unwrap()["events"][2]
                ["secondsSinceLogStart"],
            5.0
        );
        assert!(timeline(&format!("{prefix}{suffix}\n[LOG 12:00:06.000] {suffix}")).is_err());
        assert!(timeline(&format!(
            "{prefix}[LOG 12:00:01.000] [Continuum reconstruction] version=stock\n{suffix}"
        ))
        .is_err());
        assert!(timeline(&format!("{prefix}#### BEGIN PATCH LOG ####\n{suffix}")).is_err());
        assert!(timeline(&format!(
            "{prefix}#### BEGIN UNKNOWN ####\n#### END UNKNOWN ####\n{suffix}"
        ))
        .is_err());
        assert!(timeline(&format!(
            "{prefix}#### BEGIN MODULEMANAGER LOG ####\n#### END PATCH LOG ####\n{suffix}"
        ))
        .is_err());
    }
}
