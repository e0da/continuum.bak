use continuum_research_lab::{
    compare, hash, inventory, matrix, metrics, read, source_tree_hash, write_new, FlightReceipt,
    Result, VariantManifest,
};
use serde_json::{json, Value};
use std::{
    collections::BTreeMap,
    env, fs,
    path::{Path, PathBuf},
    process::{Command, Stdio},
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

fn main() {
    if let Err(error) = run() {
        eprintln!("lab: {error}");
        std::process::exit(1);
    }
}
fn run() -> Result<()> {
    let args: Vec<String> = env::args().skip(1).collect();
    let operation = args
        .first()
        .ok_or("expected inventory, index, recompose, prepare, run, compare, or qualify")?;
    let mut options = BTreeMap::new();
    if (args.len() - 1) % 2 != 0 {
        return Err("options require --name VALUE pairs".into());
    }
    for pair in args[1..].chunks(2) {
        if !pair[0].starts_with("--") || options.insert(pair[0].clone(), pair[1].clone()).is_some()
        {
            return Err("invalid or duplicate option".into());
        }
    }
    let allowed: &[&str] = match operation.as_str() {
        "inventory" => &["--instance", "--output"],
        "prepare" => &["--output", "--variant"],
        "index" => &["--assembly", "--ilspy", "--output", "--project"],
        "recompose" => &[
            "--assembly",
            "--sha256",
            "--output",
            "--method",
            "--suffix",
            "--project",
            "--project-sha256",
        ],
        "run" => &[
            "--instance",
            "--reservation",
            "--output",
            "--timeout-seconds",
            "--startup-unlimited",
        ],
        "compare" => &[
            "--baseline",
            "--candidate",
            "--treatments",
            "--output",
            "--startup-screen",
        ],
        "qualify" => &["--receipt", "--output"],
        "recovery" => &[
            "--catalog",
            "--state",
            "--trial-id",
            "--outcome",
            "--failure-signature",
            "--output",
        ],
        _ => return Err("unknown laboratory operation".into()),
    };
    if options.keys().any(|k| !allowed.contains(&k.as_str())) {
        return Err("unsupported option".into());
    }
    let get = |key: &str| {
        options
            .get(key)
            .map(String::as_str)
            .ok_or_else(|| format!("missing {key}"))
    };
    let output = Path::new(get("--output")?);
    match operation.as_str() {
        "recovery" => continuum_research_lab::recovery::advance(
            Path::new(get("--catalog")?),
            options.get("--state").map(|v| Path::new(v)),
            options.get("--trial-id").map(String::as_str),
            options.get("--outcome").map(String::as_str),
            options.get("--failure-signature").map(String::as_str),
            output,
        )?,
        "inventory" => write_new(output, &inventory(Path::new(get("--instance")?))?)?,
        "prepare" => {
            let mut blueprint = matrix();
            if let Some(path) = options.get("--variant") {
                let manifest: VariantManifest = read(Path::new(path))?;
                manifest.validate()?;
                blueprint["variantManifestSha256"] = json!(hash(Path::new(path))?);
                blueprint["variant"] = serde_json::to_value(manifest).map_err(|e| e.to_string())?;
            }
            write_new(output, &blueprint)?;
        }
        "index" => {
            if let Some(project) = options.get("--project") {
                if options.contains_key("--assembly") || options.contains_key("--ilspy") {
                    return Err(
                        "project indexing and assembly decompilation are separate inputs".into(),
                    );
                }
                let project = Path::new(project)
                    .canonicalize()
                    .map_err(|e| e.to_string())?;
                if project.extension().and_then(|v| v.to_str()) != Some("csproj") {
                    return Err("source index requires a csproj".into());
                }
                write_new(
                    output,
                    &json!({"schema":"continuum-source-index/v1","projectSha256":source_tree_hash(project.parent().unwrap())?,"excludes":["bin","obj"],"qualification":"source-inputs-indexed-external-references-unqualified"}),
                )?;
                return Ok(());
            }
            if output.exists() {
                return Err("index output already exists".into());
            }
            let source = Path::new(get("--assembly")?)
                .canonicalize()
                .map_err(|e| e.to_string())?;
            let source_hash = hash(&source)?;
            let ilspy = get("--ilspy")?;
            let version = Command::new(ilspy)
                .arg("--version")
                .output()
                .map_err(|e| e.to_string())?;
            let version_text = String::from_utf8_lossy(&version.stdout).into_owned();
            if !version.status.success() || !version_text.contains("11.1.0.9782") {
                return Err("index requires pinned ilspycmd 11.1.0.9782".into());
            }
            fs::create_dir(output).map_err(|e| e.to_string())?;
            let metadata = managed(&["inspect", source.to_str().ok_or("non-UTF8 assembly path")?])?;
            write_new(&output.join("metadata.json"), &metadata)?;
            let log = fs::File::create(output.join("decompiler.log")).map_err(|e| e.to_string())?;
            let project = output.join("project");
            let mut child = Command::new(ilspy)
                .args(["--disable-updatecheck", "-p", "-r"])
                .arg(source.parent().unwrap())
                .arg("-o")
                .arg(&project)
                .arg(&source)
                .stdout(Stdio::from(log.try_clone().map_err(|e| e.to_string())?))
                .stderr(Stdio::from(log))
                .spawn()
                .map_err(|e| e.to_string())?;
            let started = Instant::now();
            let (status, timed_out) = loop {
                match child.try_wait() {
                    Ok(Some(status)) => break (status, false),
                    Ok(None) => {}
                    Err(error) => {
                        let _ = child.kill();
                        let _ = child.wait();
                        return Err(error.to_string());
                    }
                }
                if started.elapsed() >= Duration::from_secs(900) {
                    child.kill().map_err(|e| e.to_string())?;
                    break (child.wait().map_err(|e| e.to_string())?, true);
                }
                thread::sleep(Duration::from_millis(100));
            };
            let unchanged = hash(&source)? == source_hash;
            write_new(
                &output.join("receipt.json"),
                &json!({"schema":"continuum-decompilation/v1","sourceSha256":source_hash,"sourceUnchanged":unchanged,
                "decompilerVersion":version_text,"decompilerExitCode":status.code(),"timedOut":timed_out,"wallSeconds":started.elapsed().as_secs_f64(),"qualification":if status.success()&&unchanged {"decompiled-rebuild-unqualified"} else {"failed"}}),
            )?;
            if !status.success() || !unchanged {
                return Err("decompilation failed; retained metadata, log, and receipt".into());
            }
        }
        "recompose" => {
            if output.exists() {
                return Err("recomposition output already exists".into());
            }
            let source = get("--assembly")?;
            let digest = get("--sha256")?;
            if hash(Path::new(source))? != digest {
                return Err("source hash differs from frozen input".into());
            }
            if let Some(project) = options.get("--project") {
                if options.contains_key("--method") || options.contains_key("--suffix") {
                    return Err("source rebuilding cannot also apply an IL probe".into());
                }
                return recompose_source(
                    Path::new(source),
                    digest,
                    Path::new(project),
                    get("--project-sha256")?,
                    output,
                );
            }
            if options.contains_key("--project-sha256") {
                return Err("project hash requires a project".into());
            }
            fs::create_dir(output).map_err(|e| e.to_string())?;
            let candidate = output.join(
                Path::new(source)
                    .file_name()
                    .ok_or("missing assembly filename")?,
            );
            let mut command = vec![
                "rewrite",
                source,
                digest,
                candidate.to_str().ok_or("non-UTF8 output")?,
            ];
            if let Some(method) = options.get("--method") {
                command[0] = "probe";
                command.push(method);
                command.push(get("--suffix")?);
            } else if options.contains_key("--suffix") {
                return Err("suffix requires method".into());
            }
            write_new(&output.join("receipt.json"), &managed(&command)?)?;
        }
        "run" => smoke(
            Path::new(get("--instance")?),
            Path::new(get("--reservation")?),
            output,
            options
                .get("--timeout-seconds")
                .map(|s| s.parse::<u64>())
                .transpose()
                .map_err(|e| e.to_string())?
                .unwrap_or(300),
            match options.get("--startup-unlimited").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-unlimited must be true or false".into()),
            },
        )?,
        "compare" => {
            if let Some(path) = options.get("--startup-screen") {
                if options.contains_key("--baseline")
                    || options.contains_key("--candidate")
                    || options.contains_key("--treatments")
                {
                    return Err("startup and flight comparisons require separate inputs".into());
                }
                let specification: Value = read(Path::new(path))?;
                if specification["schema"] != "continuum-startup-screen/v1"
                    || specification["cacheState"] != "warm-observed-not-reset"
                {
                    return Err(
                        "startup screen requires its frozen schema/cache declaration".into(),
                    );
                }
                let load = |key: &str| -> Result<Vec<(Value, Value)>> {
                    specification[key]
                        .as_array()
                        .ok_or("startup run list absent")?
                        .iter()
                        .map(|row| {
                            Ok((
                                read(Path::new(
                                    row["receipt"].as_str().ok_or("receipt path absent")?,
                                ))?,
                                read(Path::new(
                                    row["observation"]
                                        .as_str()
                                        .ok_or("observation path absent")?,
                                ))?,
                            ))
                        })
                        .collect()
                };
                return write_new(
                    output,
                    &continuum_research_lab::startup_screen(
                        &load("baseline")?,
                        &load("candidate")?,
                    )?,
                );
            }
            let left: FlightReceipt = read(Path::new(get("--baseline")?))?;
            let right: FlightReceipt = read(Path::new(get("--candidate")?))?;
            let treatments = get("--treatments")?
                .split(',')
                .map(str::to_owned)
                .collect::<Vec<_>>();
            write_new(output, &compare(&left, &right, &treatments)?)?;
        }
        "qualify" => write_new(
            output,
            &metrics(&read::<FlightReceipt>(Path::new(get("--receipt")?))?)?,
        )?,
        _ => unreachable!(),
    }
    println!("{}", output.display());
    Ok(())
}
fn recompose_source(
    source: &Path,
    digest: &str,
    project: &Path,
    project_hash: &str,
    output: &Path,
) -> Result<()> {
    let project = project.canonicalize().map_err(|e| e.to_string())?;
    let root = project.parent().ok_or("project directory absent")?;
    if source_tree_hash(root)? != project_hash {
        return Err("source project differs from frozen tree".into());
    }
    let parent = output
        .parent()
        .ok_or("output parent absent")?
        .canonicalize()
        .map_err(|e| e.to_string())?;
    if parent.starts_with(root) {
        return Err("source rebuild output must be outside project inputs".into());
    }
    fs::create_dir(output).map_err(|e| e.to_string())?;
    let output = output.canonicalize().map_err(|e| e.to_string())?;
    let log = fs::File::create(output.join("build.log")).map_err(|e| e.to_string())?;
    let started = Instant::now();
    let mut command = Command::new("dotnet");
    command
        .arg("build")
        .arg(&project)
        .args([
            "-c",
            "Release",
            "-p:AutomaticallyUseReferenceAssemblyPackages=true",
            "-p:UseSharedCompilation=false",
        ])
        .arg(format!("-p:OutputPath={}/bin/", output.display()))
        .stdout(Stdio::from(log.try_clone().map_err(|e| e.to_string())?))
        .stderr(Stdio::from(log));
    #[cfg(unix)]
    {
        use std::os::unix::process::CommandExt;
        command.process_group(0);
    }
    let mut child = command.spawn().map_err(|e| e.to_string())?;
    let (status, timed_out) = loop {
        match child.try_wait() {
            Ok(Some(status)) => break (status, false),
            Ok(None) => {}
            Err(error) => {
                stop_build(&mut child);
                return Err(error.to_string());
            }
        }
        if started.elapsed() >= Duration::from_secs(900) {
            stop_build(&mut child);
            break (child.wait().map_err(|e| e.to_string())?, true);
        }
        thread::sleep(Duration::from_millis(100));
    };
    let unchanged = hash(source)? == digest && source_tree_hash(root)? == project_hash;
    let candidate = output
        .join("bin")
        .join(source.file_name().ok_or("assembly filename absent")?);
    let artifact = if candidate.is_file() {
        Some(hash(&candidate)?)
    } else {
        None
    };
    let qualified = status.success() && unchanged && artifact.is_some() && !timed_out;
    write_new(
        &output.join("receipt.json"),
        &json!({"schema":"continuum-source-recomposition/v1","sourceSha256":digest,"projectSha256":project_hash,"sourceUnchanged":unchanged,
        "candidateSha256":artifact,"buildExitCode":status.code(),"timedOut":timed_out,"wallSeconds":started.elapsed().as_secs_f64(),
        "qualification":if qualified {"source-rebuilt-runtime-and-interface-unqualified"}else{"failed"}}),
    )?;
    if !qualified {
        return Err("source rebuild failed; retained log and receipt".into());
    }
    Ok(())
}
fn stop_build(child: &mut std::process::Child) {
    #[cfg(unix)]
    {
        let _ = Command::new("/bin/kill")
            .args(["-KILL", "--", &format!("-{}", child.id())])
            .status();
    }
    let _ = child.kill();
    let _ = child.wait();
}
fn managed(arguments: &[&str]) -> Result<Value> {
    let root = Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .and_then(Path::parent)
        .ok_or("repository root unavailable")?;
    let dll = root.join(
        "tools/KspContinuum.Reconstruction/bin/Release/net8.0/KspContinuum.Reconstruction.dll",
    );
    if !dll.is_file() {
        return Err(
            "build tools/KspContinuum.Reconstruction in Release before this operation".into(),
        );
    }
    let output = Command::new("dotnet")
        .arg(dll)
        .args(arguments)
        .output()
        .map_err(|e| e.to_string())?;
    if !output.status.success() {
        return Err(String::from_utf8_lossy(&output.stderr).into_owned());
    }
    serde_json::from_slice(&output.stdout).map_err(|e| e.to_string())
}
struct Lease(PathBuf);
impl Drop for Lease {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.0);
    }
}
fn smoke(
    instance: &Path,
    reservation: &Path,
    output: &Path,
    timeout: u64,
    startup_unlimited: bool,
) -> Result<()> {
    if timeout == 0 || timeout > 1800 {
        return Err("timeout must be 1..1800 seconds".into());
    }
    if output.exists() {
        return Err("run output already exists".into());
    }
    let instance = instance.canonicalize().map_err(|e| e.to_string())?;
    let ownership: Value = read(&instance.join("continuum-lab.local.json"))?;
    if ownership["scope"] != "disposable-lab" {
        return Err("instance is not marked as a disposable laboratory copy".into());
    }
    let session: Value = read(reservation)?;
    if session["schema"] != "continuum-lab-reservation/v1"
        || session["host"] != "forge"
        || session["active"] != true
        || session["authorization"] != "user-reserved-until-revoked"
    {
        return Err("active user-reserved Forge session required".into());
    }
    let host = Command::new("scutil")
        .args(["--get", "LocalHostName"])
        .output()
        .map_err(|e| e.to_string())?;
    if !host.status.success() || String::from_utf8_lossy(&host.stdout).trim() != "forge" {
        return Err("reservation does not admit this host".into());
    }
    let lease_path = reservation.with_extension("run-lock");
    let mut lease = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&lease_path)
        .map_err(|e| format!("laboratory lease unavailable: {e}"))?;
    let _guard = Lease(lease_path);
    use std::io::Write;
    writeln!(lease, "ownerPid={}", std::process::id()).map_err(|e| e.to_string())?;
    let executable = instance.join("KSP.app/Contents/MacOS/KSP");
    let assembly = instance.join("KSP.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll");
    let assembly_hash = hash(&assembly)?;
    let executable_hash = hash(&executable)?;
    fs::create_dir(output).map_err(|e| e.to_string())?;
    let stdout = fs::File::create(output.join("player-stdout.log")).map_err(|e| e.to_string())?;
    let stderr = fs::File::create(output.join("player-stderr.log")).map_err(|e| e.to_string())?;
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|e| e.to_string())?
        .as_secs();
    let log_path = output
        .canonicalize()
        .map_err(|e| e.to_string())?
        .join("Player.log");
    let run_id = format!("{}-{}", stamp, std::process::id());
    let observer_directory = instance.join("GameData/KspContinuum/PluginData");
    let started = Instant::now();
    let mut child = Command::new(&executable)
        .current_dir(&instance)
        .args([
            "--continuum-native-boundary-bench",
            "-screen-width",
            "1920",
            "-screen-height",
            "1080",
            "-logFile",
        ])
        .arg(&log_path)
        .arg(format!("--continuum-research-run={run_id}"))
        .args(if startup_unlimited {
            vec!["--continuum-startup-unlimited"]
        } else {
            vec![]
        })
        .stdout(Stdio::from(stdout))
        .stderr(Stdio::from(stderr))
        .spawn()
        .map_err(|e| e.to_string())?;
    let mut observation = None;
    let mut menu_elapsed = None;
    let (status, timed_out) = loop {
        if observation.is_none() {
            if let Ok(entries) = fs::read_dir(&observer_directory) {
                for entry in entries.flatten() {
                    if !entry
                        .file_name()
                        .to_string_lossy()
                        .starts_with("reconstruction-")
                    {
                        continue;
                    }
                    if let Ok(value) = read::<Value>(&entry.path()) {
                        if value["schema"] == "continuum-managed-runtime-observation/v1"
                            && value["runId"] == run_id
                            && value["assemblySha256"] == assembly_hash
                        {
                            menu_elapsed = Some(started.elapsed().as_secs_f64());
                            observation = Some(value);
                            break;
                        }
                    }
                }
            }
        }
        match child.try_wait() {
            Ok(Some(status)) => break (status, false),
            Ok(None) => {}
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(error.to_string());
            }
        }
        let reserved = read::<Value>(reservation)
            .map(|v| v["active"] == true)
            .unwrap_or(false);
        if !reserved || started.elapsed() >= Duration::from_secs(timeout) {
            child.kill().map_err(|e| e.to_string())?;
            break (child.wait().map_err(|e| e.to_string())?, true);
        }
        thread::sleep(Duration::from_millis(100));
    };
    let source_unchanged = hash(&assembly)? == assembly_hash;
    if let Some(value) = &observation {
        write_new(&output.join("runtime-observation.json"), value)?;
    }
    write_new(
        &output.join("receipt.json"),
        &json!({"schema":"continuum-lab-process-smoke/v1","scope":"menu-native-boundary-process-smoke",
        "startedUnixSeconds":stamp,"ownedPid":child.id(),"reservationSha256":hash(reservation)?,"assemblySha256":assembly_hash,"playerSha256":executable_hash,
        "sourceUnchanged":source_unchanged,"wallSeconds":started.elapsed().as_secs_f64(),"exitCode":status.code(),
        "runId":run_id,"mainMenuStartObservedWallSeconds":menu_elapsed,"startupBoundary":"first-owned-addon-Start-in-main-menu","startupPollingResolutionSeconds":0.1,
        "startupUnlimitedRequested":startup_unlimited,
        "status":if timed_out {"interrupted"} else if status.success()&&source_unchanged {"passed"} else {"failed"},
        "qualification":"process-exit-only-native-receipt-review-and-flight-proof-required"}),
    )?;
    if timed_out || !status.success() || !source_unchanged {
        return Err("owned process smoke failed; retained logs and receipt".into());
    }
    Ok(())
}
