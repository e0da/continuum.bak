#![recursion_limit = "256"]
mod native_assets;
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
        "admit-cache" => &["--instance", "--startup-cache", "--output"],
        "install-native-assets" => &["--instance", "--manifest", "--output"],
        "restore-native-assets" => &["--instance", "--installation", "--output"],
        "startup-timeline" => &["--log", "--output"],
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
            "--startup-profile",
            "--startup-reuse-bundle-data",
            "--startup-file-bundles",
            "--startup-build-cache",
            "--startup-cache",
            "--startup-cached-bundles",
            "--startup-native-admission",
            "--graphics-api",
            "--startup-prepared-bundles",
            "--startup-catalog-mode",
            "--startup-catalog",
            "--startup-texture-prefetch",
            "--startup-texture-observe",
            "--startup-dds-mapped",
            "--startup-audio-prefetch",
            "--startup-readiness",
            "--startup-resource-image-mode",
            "--startup-resource-image",
            "--startup-work-scheduler",
            "--native-asset-installation",
            "--startup-world-timing",
            "--flight-save",
            "--flight-checkpoint",
            "--flight-max-delta",
            "--flight-seconds",
            "--flight-settle-seconds",
            "--flight-warp",
            "--flight-atmosphere",
            "--flight-resolution",
            "--flight-no-shadows",
            "--flight-no-aa",
            "--flight-native-sample",
            "--flight-perturbation",
            "--flight-pulse-delta-v",
            "--flight-callback-attribution",
            "--flight-hide-vessel",
            "--flight-no-aero-fx",
            "--flight-aero-quality",
            "--flight-entry-target-altitude",
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
        "startup-timeline" => {
            let path = Path::new(get("--log")?);
            let source_hash = hash(path)?;
            let log = fs::read_to_string(path).map_err(|e| e.to_string())?;
            let mut report = continuum_research_lab::startup::timeline(&log)?;
            if hash(path)? != source_hash {
                return Err("log changed during analysis".into());
            }
            report["logSha256"] = json!(source_hash);
            write_new(output, &report)?;
        }
        "admit-cache" => {
            let started = Instant::now();
            let instance = Path::new(get("--instance")?)
                .canonicalize()
                .map_err(|e| e.to_string())?;
            if read::<Value>(&instance.join("continuum-lab.local.json"))?["scope"]
                != "disposable-lab"
            {
                return Err("disposable laboratory ownership required".into());
            }
            let source_hashes = expected_bundle_hashes(Path::new(get("--startup-cache")?))?;
            let admission = continuum_research_lab::admission::admit(
                &instance,
                Path::new(get("--startup-cache")?),
                "offline-admission-proof",
                &source_hashes,
            )?;
            write_new(
                output,
                &json!({"schema":"continuum-offline-cache-admission/v1", "wallSeconds":started.elapsed().as_secs_f64(),
                "proof":admission.proof,"files":admission.files,"sourceSha256":source_hashes,"scope":"no-game-launch-content-admission"}),
            )?;
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
            match options.get("--startup-profile").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-profile must be true or false".into()),
            },
            match options
                .get("--startup-reuse-bundle-data")
                .map(String::as_str)
            {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-reuse-bundle-data must be true or false".into()),
            },
            match options.get("--startup-file-bundles").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-file-bundles must be true or false".into()),
            },
            match options.get("--startup-build-cache").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-build-cache must be true or false".into()),
            },
            options.get("--startup-cache").map(String::as_str),
            match options.get("--startup-cached-bundles").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-cached-bundles must be true or false".into()),
            },
            match options
                .get("--startup-native-admission")
                .map(String::as_str)
            {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-native-admission must be true or false".into()),
            },
            match options.get("--graphics-api").map(String::as_str) {
                None | Some("current") => "current",
                Some("metal") => "metal",
                _ => return Err("graphics-api must be current or metal".into()),
            },
            match options
                .get("--startup-prepared-bundles")
                .map(String::as_str)
            {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-prepared-bundles must be true or false".into()),
            },
            match options.get("--startup-catalog-mode").map(String::as_str) {
                None | Some("none") => "none",
                Some("prepare") => "prepare",
                Some("prepare-minimal") => "prepare-minimal",
                Some("reuse") => "reuse",
                _ => {
                    return Err(
                        "startup-catalog-mode must be none, prepare, prepare-minimal, or reuse"
                            .into(),
                    )
                }
            },
            options.get("--startup-catalog").map(String::as_str),
            match options
                .get("--startup-texture-prefetch")
                .map(String::as_str)
            {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-texture-prefetch must be true or false".into()),
            },
            match options.get("--startup-texture-observe").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-texture-observe must be true or false".into()),
            },
            match options.get("--startup-dds-mapped").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-dds-mapped must be true or false".into()),
            },
            match options.get("--startup-audio-prefetch").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-audio-prefetch must be true or false".into()),
            },
            match options.get("--startup-readiness").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-readiness must be true or false".into()),
            },
            match options
                .get("--startup-resource-image-mode")
                .map(String::as_str)
            {
                None | Some("none") => "none",
                Some("prepare") => "prepare",
                Some("reuse") => "reuse",
                _ => {
                    return Err(
                        "startup-resource-image-mode must be none, prepare, or reuse".into(),
                    )
                }
            },
            options.get("--startup-resource-image").map(String::as_str),
            match options.get("--startup-work-scheduler").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-work-scheduler must be true or false".into()),
            },
            options.get("--native-asset-installation").map(Path::new),
            continuum_research_lab::flight::Request::parse(&options)?,
            match options.get("--startup-world-timing").map(String::as_str) {
                None | Some("false") => false,
                Some("true") => true,
                _ => return Err("startup-world-timing must be true or false".into()),
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
        "install-native-assets" => native_assets::install(
            Path::new(get("--instance")?),
            Path::new(get("--manifest")?),
            output,
        )?,
        "restore-native-assets" => native_assets::restore(
            Path::new(get("--instance")?),
            Path::new(get("--installation")?),
            output,
        )?,
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
    startup_profile: bool,
    startup_reuse_bundle_data: bool,
    startup_file_bundles: bool,
    startup_build_cache: bool,
    startup_cache: Option<&str>,
    startup_cached_bundles: bool,
    startup_native_admission: bool,
    graphics_api: &str,
    startup_prepared_bundles: bool,
    startup_catalog_mode: &str,
    startup_catalog: Option<&str>,
    startup_texture_prefetch: bool,
    startup_texture_observe: bool,
    startup_dds_mapped: bool,
    startup_audio_prefetch: bool,
    startup_readiness: bool,
    startup_resource_image_mode: &str,
    startup_resource_image: Option<&str>,
    startup_work_scheduler: bool,
    native_asset_installation: Option<&Path>,
    flight: Option<continuum_research_lab::flight::Request>,
    startup_world_timing: bool,
) -> Result<()> {
    let admission_started = Instant::now();
    if (startup_resource_image_mode != "none") != startup_resource_image.is_some() {
        return Err("resource image mode requires one private image path".into());
    }
    if let Some(path) = startup_resource_image {
        if !Path::new(path).is_absolute() {
            return Err("resource image path must be absolute".into());
        }
    }
    if startup_resource_image_mode != "none" && (startup_texture_prefetch || startup_dds_mapped) {
        return Err("resource image replaces the texture loader; select it independently of texture seam probes".into());
    }
    let catalog_preparation = startup_catalog_mode.starts_with("prepare");
    if flight.is_some()
        && (startup_build_cache || catalog_preparation || startup_resource_image_mode == "prepare")
    {
        return Err("flight capture cannot share a resource preparation run".into());
    }
    if (startup_catalog_mode != "none") != startup_catalog.is_some() {
        return Err("catalog prepare/reuse requires one private catalog path".into());
    }
    let catalog_before = match startup_catalog {
        Some(path) => {
            if !Path::new(path).is_absolute() {
                return Err("catalog path must be absolute".into());
            }
            if startup_catalog_mode == "reuse" {
                Some(hash(Path::new(path))?)
            } else if Path::new(path).exists() {
                return Err("catalog preparation output exists".into());
            } else {
                None
            }
        }
        None => None,
    };
    if (startup_build_cache || startup_cached_bundles) && startup_cache.is_none() {
        return Err("cache preparation requires a private --startup-cache directory".into());
    }
    if startup_build_cache && startup_cached_bundles {
        return Err("cache preparation and cached loading require separate runs".into());
    }
    if startup_native_admission && !startup_cached_bundles {
        return Err("native admission requires cached bundle loading".into());
    }
    if startup_prepared_bundles && !(startup_cached_bundles && startup_native_admission) {
        return Err("prepared bundle loading requires cached bundles and native admission".into());
    }
    if let Some(path) = startup_cache {
        if !Path::new(path).is_absolute() {
            return Err("startup cache requires an absolute private path".into());
        }
    }
    if timeout == 0 || timeout > 1800 {
        return Err("timeout must be 1..1800 seconds".into());
    }
    if output.exists() {
        return Err("run output already exists".into());
    }
    let instance = instance.canonicalize().map_err(|e| e.to_string())?;
    if instance
        .join("continuum-native-asset-transaction.local.json")
        .exists()
    {
        return Err(
            "native asset transaction is unfinished; restore or complete it before launch".into(),
        );
    }
    let native_pointer = instance.join("continuum-native-asset-installation.local.json");
    if native_pointer.exists() {
        let pointer: Value = read(&native_pointer)?;
        let installed = native_asset_installation
            .ok_or("this instance requires its native asset treatment receipt")?;
        if pointer["installationReceipt"].as_str() != installed.to_str()
            || pointer["installationSha256"].as_str() != Some(hash(installed)?.as_str())
        {
            return Err(
                "native asset treatment receipt differs from the installed checkpoint".into(),
            );
        }
    }
    let ownership: Value = read(&instance.join("continuum-lab.local.json"))?;
    if ownership["scope"] != "disposable-lab" {
        return Err("instance is not marked as a disposable laboratory copy".into());
    }
    let flight_inputs = flight
        .as_ref()
        .map(|request| request.snapshot(&instance))
        .transpose()?;
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
    let native_assets = native_asset_installation
        .map(|path| native_assets::Admission::admit(&instance, path))
        .transpose()?;
    let assembly = instance.join("KSP.app/Contents/Resources/Data/Managed/Assembly-CSharp.dll");
    let assembly_hash = hash(&assembly)?;
    let executable_hash = hash(&executable)?;
    let bundle_hashes = if startup_native_admission {
        expected_bundle_hashes(Path::new(startup_cache.unwrap()))?
    } else {
        startup_bundle_hashes(&instance)?
    };
    fs::create_dir(output).map_err(|e| e.to_string())?;
    if let Some(inputs) = &flight_inputs {
        write_new(&output.join("flight-inputs.json"), inputs)?;
    }
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
    let admission = if startup_native_admission {
        Some(continuum_research_lab::admission::admit(
            &instance,
            Path::new(startup_cache.unwrap()),
            &run_id,
            &bundle_hashes,
        )?)
    } else {
        None
    };
    let proof_path = output
        .canonicalize()
        .map_err(|e| e.to_string())?
        .join("cache-admission.json");
    let proof_hash = if let Some(admission) = &admission {
        write_new(&proof_path, &admission.proof)?;
        Some(hash(&proof_path)?)
    } else {
        None
    };
    let observer_directory = instance.join("GameData/KspContinuum/PluginData");
    let started = Instant::now();
    let admission_seconds = admission_started.elapsed().as_secs_f64();
    let mut child = Command::new(&executable)
        .current_dir(&instance)
        .args(if flight.is_none() {
            vec!["--continuum-native-boundary-bench".to_string()]
        } else {
            flight.as_ref().unwrap().arguments()
        })
        .args([
            "-screen-width",
            "1920",
            "-screen-height",
            "1080",
            "-logFile",
        ])
        .arg(&log_path)
        .arg(format!("--continuum-research-run={run_id}"))
        .args(if startup_world_timing {
            vec!["--continuum-startup-world-timing"]
        } else {
            vec![]
        })
        .args(match startup_catalog_mode {
            "prepare" => vec!["--continuum-startup-prepare-catalog"],
            "prepare-minimal" => vec![
                "--continuum-startup-prepare-catalog",
                "--continuum-startup-metadata-only-prepare",
            ],
            "reuse" => vec!["--continuum-startup-cached-catalog"],
            _ => vec![],
        })
        .args(startup_catalog.map(|path| format!("--continuum-startup-catalog={path}")))
        .args(if startup_texture_prefetch {
            vec!["--continuum-startup-texture-prefetch"]
        } else {
            vec![]
        })
        .args(if startup_texture_observe {
            vec!["--continuum-startup-texture-observe"]
        } else {
            vec![]
        })
        .args(if startup_dds_mapped {
            vec!["--continuum-startup-dds-mapped"]
        } else {
            vec![]
        })
        .args(if startup_audio_prefetch {
            vec!["--continuum-startup-audio-prefetch"]
        } else {
            vec![]
        })
        .args(if startup_readiness {
            vec!["--continuum-startup-readiness"]
        } else {
            vec![]
        })
        .args(if startup_resource_image_mode != "none" {
            vec![
                format!("--continuum-startup-resource-image-mode={startup_resource_image_mode}"),
                format!(
                    "--continuum-startup-resource-image={}",
                    startup_resource_image.unwrap()
                ),
            ]
        } else {
            vec![]
        })
        .args(if startup_work_scheduler {
            vec!["--continuum-startup-work-scheduler"]
        } else {
            vec![]
        })
        .args(if graphics_api == "metal" {
            vec!["-force-metal"]
        } else {
            vec![]
        })
        .args(if startup_prepared_bundles {
            vec!["--continuum-startup-prepared-bundles"]
        } else {
            vec![]
        })
        .args(if startup_unlimited {
            vec!["--continuum-startup-unlimited"]
        } else {
            vec![]
        })
        .args(if startup_profile {
            vec!["--continuum-startup-profile"]
        } else {
            vec![]
        })
        .args(if startup_reuse_bundle_data {
            vec!["--continuum-startup-reuse-bundle-data"]
        } else {
            vec![]
        })
        .args(if startup_file_bundles {
            vec!["--continuum-startup-file-bundles"]
        } else {
            vec![]
        })
        .args(if startup_build_cache {
            vec!["--continuum-startup-build-cache"]
        } else {
            vec![]
        })
        .args(startup_cache.map(|path| format!("--continuum-startup-cache={path}")))
        .args(if startup_cached_bundles {
            vec!["--continuum-startup-cached-bundles"]
        } else {
            vec![]
        })
        .args(if let Some(digest) = &proof_hash {
            vec![
                "--continuum-startup-native-admission".to_string(),
                format!("--continuum-startup-admission={}", proof_path.display()),
                format!("--continuum-startup-admission-sha256={digest}"),
            ]
        } else {
            vec![]
        })
        .stdout(Stdio::from(stdout))
        .stderr(Stdio::from(stderr))
        .spawn()
        .map_err(|e| e.to_string())?;
    let mut observation = None;
    let mut menu_elapsed = None;
    let mut native_sample = continuum_research_lab::flight::NativeSample::new(
        flight
            .as_ref()
            .map(|request| request.native_sample)
            .unwrap_or(false),
    );
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
        native_sample.poll(&observer_directory, &run_id, child.id(), output);
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
    let native_sample_receipt = native_sample.finish(child.id(), output);
    let generated_catalog_hash = if catalog_preparation {
        startup_catalog.and_then(|path| hash(Path::new(path)).ok())
    } else {
        None
    };
    let source_unchanged = hash(&assembly)? == assembly_hash
        && native_assets
            .as_ref()
            .map(|assets| assets.unchanged())
            .unwrap_or(true)
        && (!catalog_preparation || generated_catalog_hash.is_some())
        && catalog_before
            .as_ref()
            .map(|expected| {
                startup_catalog
                    .map(|path| {
                        hash(Path::new(path))
                            .map(|actual| actual == *expected)
                            .unwrap_or(false)
                    })
                    .unwrap_or(false)
            })
            .unwrap_or(true)
        && startup_bundle_hashes(&instance)? == bundle_hashes
        && admission
            .as_ref()
            .map(|value| value.unchanged())
            .unwrap_or(true)
        && proof_hash
            .as_ref()
            .map(|expected| {
                hash(&proof_path)
                    .map(|actual| actual == *expected)
                    .unwrap_or(false)
            })
            .unwrap_or(true);
    let mut flight_error = None;
    let mut flight_summary = None;
    let mut flight_entry = None;
    if let Some(request) = &flight {
        let result = (|| -> Result<Value> {
            flight_entry = continuum_research_lab::flight::retain_entry(
                &observer_directory,
                &run_id,
                child.id(),
                output,
            )?;
            let directory = continuum_research_lab::flight::capture_directory(
                &observer_directory,
                &run_id,
                child.id(),
            )?
            .ok_or_else(|| match &flight_entry {
                Some(entry) => format!(
                    "owned flight capture not found; entry status={} reason={}",
                    entry["report"]["status"], entry["report"]["reason"]
                ),
                None => "owned flight capture not found; no owned entry sidecar".to_string(),
            })?;
            let (start, end, report) = continuum_research_lab::flight::retain(&directory, output)?;
            let mut summary = continuum_research_lab::flight::analyze(
                request,
                &run_id,
                child.id(),
                &report,
                &start,
                &end,
            )?;
            continuum_research_lab::flight::validate_entry(
                request,
                &run_id,
                child.id(),
                flight_entry.as_ref(),
                &report,
                flight_inputs
                    .as_ref()
                    .ok_or("flight input snapshot absent")?,
            )?;
            continuum_research_lab::flight::validate_controls(
                request,
                &run_id,
                child.id(),
                output,
                &report,
            )?;
            for (name, key) in [
                ("diagnostic-controls.json", "diagnosticControls"),
                ("diagnostic-controls-end.json", "diagnosticControlsEnd"),
                ("perturbation.json", "perturbationEvidence"),
            ] {
                let path = output.join(name);
                if path.exists() {
                    summary[key] = json!({"sha256":hash(&path)?,"report":read::<Value>(&path)?});
                }
            }
            summary["entryEvidence"] = json!(flight_entry);
            summary["cameraReadbacks"] = json!({
                "requestedSeed":{"mode":"FREE","pitch":0.2,"heading":0.3,"distanceMeters":150.0,"inputLocked":true},
                "captureStart":summary["diagnosticControls"]["report"],
                "captureEnd":summary["diagnosticControlsEnd"]["report"],
                "scope":"Boundary readbacks only; terrain/vessel motion may adjust camera distance. Renderer absence is witnessed at admission/end, not every frame."});
            write_new(&output.join("flight-summary.json"), &summary)?;

            Ok(summary)
        })();
        match result {
            Ok(summary) => flight_summary = Some(summary),
            Err(error) => flight_error = Some(error),
        }
    }
    if flight
        .as_ref()
        .map(|request| request.native_sample)
        .unwrap_or(false)
        && (native_sample_receipt["status"] != "completed"
            || native_sample_receipt["outputSha256"].is_null())
    {
        flight_error.get_or_insert_with(|| {
            "requested owned native sample incomplete; retained diagnostic receipt".to_string()
        });
    }
    let flight_sources_unchanged = flight_inputs
        .as_ref()
        .map(continuum_research_lab::flight::unchanged)
        .unwrap_or(true);
    if let Some(value) = &observation {
        write_new(&output.join("runtime-observation.json"), value)?;
    }
    write_new(
        &output.join("receipt.json"),
        &json!({"schema":"continuum-lab-process-smoke/v1","scope":if flight.is_some() {"timed-diagnostic-flight"} else if catalog_preparation {"asset-catalog-preparation-process"} else {"menu-native-boundary-process-smoke"},
        "startedUnixSeconds":stamp,"ownedPid":child.id(),"reservationSha256":hash(reservation)?,"assemblySha256":assembly_hash,"playerSha256":executable_hash,
        "sourceUnchanged":source_unchanged,"flightSourcesUnchanged":flight_sources_unchanged,
        "flightRequest":flight,"flightCapture":flight_summary,"flightCaptureError":flight_error,"flightEntry":flight_entry,
        "flightNativeSample":native_sample_receipt,
        "captureCpuRss":"not-collected-by-runner; external-owned-process-sampling-required","wallSeconds":started.elapsed().as_secs_f64(),"exitCode":status.code(),
        "runId":run_id,"mainMenuStartObservedWallSeconds":menu_elapsed,"startupBoundary":"first-owned-addon-Start-in-main-menu","startupPollingResolutionSeconds":0.1,
        "admissionWallSeconds":admission_seconds,"admissionPlusMenuSeconds":menu_elapsed.map(|seconds| seconds+admission_seconds),
        "startupUnlimitedRequested":startup_unlimited,
        "graphicsApiRequested":graphics_api,
        "startupPreparedBundlesRequested":startup_prepared_bundles,
        "startupTexturePrefetchRequested":startup_texture_prefetch,
        "startupTextureObserveRequested":startup_texture_observe,
        "startupDdsMappedRequested":startup_dds_mapped,
        "startupAudioPrefetchRequested":startup_audio_prefetch,
        "startupReadinessRequested":startup_readiness,
        "startupResourceImageModeRequested":startup_resource_image_mode,"startupResourceImage":startup_resource_image,
        "startupWorkSchedulerRequested":startup_work_scheduler,
        "nativeAssetInstallation":native_assets.as_ref().map(|assets| assets.receipt()),
        "startupCatalogModeRequested":startup_catalog_mode,"startupCatalog":startup_catalog,"startupCatalogSha256":catalog_before,
        "generatedCatalogSha256":generated_catalog_hash,
        "startupProfileRequested":startup_profile,
        "startupReuseBundleDataRequested":startup_reuse_bundle_data,
        "startupFileBundlesRequested":startup_file_bundles,
        "startupBuildCacheRequested":startup_build_cache,"startupCache":startup_cache,"bundleSha256":bundle_hashes,
        "startupCachedBundlesRequested":startup_cached_bundles,
        "startupNativeAdmissionRequested":startup_native_admission,"cacheAdmissionSha256":proof_hash,"cacheAdmissionFiles":admission.as_ref().map(|value|&value.files),
        "status":if timed_out {"interrupted"} else if status.success()&&source_unchanged&&flight_sources_unchanged&&flight_error.is_none() {"passed"} else {"failed"},
        "qualification":if flight.is_some() {"diagnostic-window-only; behavior-and-gameplay-not-qualified"} else if catalog_preparation {"catalog-content-and-fresh-process-reuse-proof-required"} else {"process-exit-only-native-receipt-review-and-flight-proof-required"}}),
    )?;
    if timed_out
        || !status.success()
        || !source_unchanged
        || !flight_sources_unchanged
        || flight_error.is_some()
    {
        return Err("owned process smoke failed; retained logs and receipt".into());
    }
    Ok(())
}

fn expected_bundle_hashes(cache: &Path) -> Result<BTreeMap<String, String>> {
    let manifest_path = cache.join("manifest.json");
    if fs::metadata(&manifest_path)
        .map_err(|e| e.to_string())?
        .len()
        > 65536
    {
        return Err("cache manifest too large".into());
    }
    let manifest: Value = read(&manifest_path)?;
    let rows = manifest["bundles"].as_array().ok_or("cache rows absent")?;
    if rows.len() != 3 {
        return Err("three source rows required".into());
    }
    let mut hashes = BTreeMap::new();
    for relative in [
        "MakingHistory/AssetBundles/makinghistory_assets",
        "MakingHistory/AssetBundles/makinghistory_scene",
        "Serenity/AssetBundles/serenity_assets",
    ] {
        let matching: Vec<_> = rows
            .iter()
            .filter(|row| row["relativePath"] == relative)
            .collect();
        if matching.len() != 1 {
            return Err("source row missing or duplicate".into());
        }
        let digest = matching[0]["sourceSha256"]
            .as_str()
            .ok_or("source hash absent")?;
        if digest.len() != 64 || !digest.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err("source hash malformed".into());
        }
        hashes.insert(
            format!("GameData/SquadExpansion/{relative}"),
            digest.to_string(),
        );
    }
    Ok(hashes)
}
fn startup_bundle_hashes(instance: &Path) -> Result<BTreeMap<String, String>> {
    let mut hashes = BTreeMap::new();
    for relative in [
        "GameData/SquadExpansion/MakingHistory/AssetBundles/makinghistory_assets",
        "GameData/SquadExpansion/MakingHistory/AssetBundles/makinghistory_scene",
        "GameData/SquadExpansion/Serenity/AssetBundles/serenity_assets",
    ] {
        let path = instance.join(relative);
        if path.exists() {
            hashes.insert(relative.to_string(), hash(&path)?);
        }
    }
    Ok(hashes)
}
