use serde_json::Value;
use std::collections::{BTreeMap, BTreeSet};
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output};

const DOTNET_TESTS: &[&str] = &[
    "tests/KspContinuum.OwnedRuntime.Tests",
    "tests/KspContinuum.Tests",
    "tests/KspContinuum.Worker.Tests",
    "tests/KspContinuum.Handoff.Tests",
    "tests/KspContinuum.Takeover.Tests",
    "tests/KspContinuum.Adapter.Tests",
    "tests/KspContinuum.Contact.Tests",
    "tests/KspContinuum.Coasting.Tests",
    "tests/KspContinuum.CoastingEvents.Tests",
    "tests/KspContinuum.Encounter.Tests",
    "tests/KspContinuum.Encounter.Oracle.Tests",
    "tests/KspContinuum.Profiling.Tests",
    "tests/KspContinuum.PlayerLoop.Tests",
    "tests/KspContinuum.LiveControl.Tests",
    "tests/KspContinuum.PlayableRigid.Tests",
    "tests/KspContinuum.RetainedIngress.Tests",
    "tests/KspContinuum.RetainedValidation.Tests",
    "tests/KspContinuum.OwnedFlightTimeline.Tests",
    "tests/KspContinuum.OwnedFlightWorker.Tests",
    "tests/KspContinuum.AdaptiveMechanics.Tests",
    "tests/KspContinuum.FlightEvents.Tests",
    "tests/KspContinuum.FlightInterpolation.Tests",
    "tests/KspContinuum.CausalTimeline.Tests",
    "tests/KspContinuum.ExecutionPlan.Tests",
    "tests/KspContinuum.Deadlines.Tests",
    "tests/KspContinuum.FlightForecast.Tests",
    "tests/KspContinuum.TopologyTransfer.Tests",
    "tests/KspContinuum.EmbeddedMechanics.Tests",
    "tests/KspContinuum.ScalarSupply.Tests",
    "tests/KspContinuum.SupplyIntegration.Tests",
    "tests/KspContinuum.OwnedClockShell.Tests",
    "tests/KspContinuum.PatchBytes.Tests",
    "tests/KspContinuum.ResourceRouting.Tests",
    "tests/KspContinuum.CheckpointStreaming.Tests",
    "tests/KspContinuum.SegmentedState.Tests",
    "tests/KspContinuum.StateStoreProof.Tests",
    "tests/KspContinuum.ThermalStorage.Tests",
    "tests/KspContinuum.CompiledThermal.Tests",
    "tests/KspContinuum.MassValidity.Tests",
    "tests/KspContinuum.CompiledSystemsProof.Tests",
    "tests/KspContinuum.ColumnarComparison.Tests",
    "tests/KspContinuum.CapacitySpatial.Tests",
    "tests/KspContinuum.ScheduleImages.Tests",
    "tests/KspContinuum.WriterCensus.Tests",
    "tests/KspContinuum.ForceObservation.Tests",
    "tests/KspContinuum.AeroCapture.Tests",
    "tests/KspContinuum.AeroBaseline.Tests",
    "tests/KspContinuum.AeroForceSubstitution.Tests",
    "tests/KspContinuum.LifecycleTrace.Tests",
    "tests/KspContinuum.LifecycleOrderQualification.Tests",
    "tests/KspContinuum.Shadow.Tests",
    "tests/KspContinuum.StructuralResponse.Tests",
    "tests/KspContinuum.RigidCluster.Tests",
    "tests/KspContinuum.Islands.Tests",
    "tests/KspContinuum.RigidCluster6Dof.Tests",
    "tests/KspContinuum.RigidAssembly.Tests",
    "tests/KspContinuum.NativeAssembly.Tests",
    "tests/KspContinuum.StructuralExperiment.Tests",
    "tests/KspContinuum.Tools.Tests",
    "tests/KspContinuum.Reconstruction.Tests",
    "src/KspContinuum.Mission/Tests/Mission.Tests.csproj",
];

type Result<T = ()> = std::result::Result<T, String>;

fn main() {
    if let Err(error) = run() {
        eprintln!("xtask: {error}");
        std::process::exit(1);
    }
}

fn run() -> Result {
    let root = repository_root()?;
    let arguments: Vec<String> = env::args().skip(1).collect();
    match arguments.first().map(String::as_str) {
        Some("lab") => {
            let mut command = Command::new("cargo");
            command.current_dir(&root).args(["run", "--release", "--manifest-path", "tools/research-lab/Cargo.toml", "--"]).args(&arguments[1..]);
            let status = command.status().map_err(|e| e.to_string())?;
            if status.success() { Ok(()) } else { Err(format!("laboratory exited with {status}")) }
        }
        Some("portable") => portable(&root),
        Some("field") => field(&root),
        Some("structural") => structural(&root),
        Some("execution-bench") if arguments.len() == 2 => {
            execution_bench(&root, &arguments[1])
        }
        Some("persistent-execution-bench") if arguments.len() == 2 => {
            persistent_execution_bench(&root, &arguments[1])
        }
        Some("native-cpu-throughput-bench") if arguments.len() == 2 => {
            native_cpu_throughput_bench(&root, &arguments[1])
        }
        Some("all") => {
            portable(&root)?;
            field(&root)?;
            structural(&root)
        }
        Some("verify-structural-report") if arguments.len() == 2 => {
            verify_structural_report(&root, Path::new(&arguments[1]))
        }
        Some("verify-checkpoint-report") if arguments.len() == 2 => {
            verify_checkpoint_report(&root, Path::new(&arguments[1]))
        }
        _ => Err("usage: cargo run --manifest-path tools/xtask/Cargo.toml -- {portable|field|structural|all|execution-bench NEW_REPORT.json|persistent-execution-bench NEW_REPORT.json|native-cpu-throughput-bench NEW_REPORT.json|verify-structural-report BINARY|verify-checkpoint-report BINARY}".into()),
    }
}

fn execution_bench(root: &Path, output: &str) -> Result {
    checked(
        root,
        "cargo",
        &[
            "run",
            "--release",
            "--manifest-path",
            "tools/numerics/Cargo.toml",
            "--bin",
            "execution-view-bench",
            "--",
            "--output",
            output,
        ],
    )
}

fn persistent_execution_bench(root: &Path, output: &str) -> Result {
    checked(
        root,
        "cargo",
        &[
            "run",
            "--release",
            "--manifest-path",
            "tools/numerics/Cargo.toml",
            "--bin",
            "persistent-execution-view-bench",
            "--",
            "--output",
            output,
        ],
    )
}

fn native_cpu_throughput_bench(root: &Path, output: &str) -> Result {
    if let Some(parent) = Path::new(output).parent() {
        fs::create_dir_all(root.join(parent))
            .map_err(|error| format!("cannot create benchmark output directory: {error}"))?;
    }
    let arguments = ["run", "--release", "--manifest-path", "tools/numerics/Cargo.toml", "--bin",
        "native-cpu-throughput-bench", "--", "--output", output];
    eprintln!("+ RUSTFLAGS=-C target-cpu=native cargo {}", arguments.join(" "));
    let status = Command::new("cargo").args(arguments).env("RUSTFLAGS", "-C target-cpu=native")
        .current_dir(root).status().map_err(|e| format!("failed to start cargo: {e}"))?;
    if status.success() { Ok(()) } else { Err(format!("cargo exited with {status}")) }
}

fn field(root: &Path) -> Result {
    checked(root, "cargo", &["test", "--manifest-path", "tools/research-lab/Cargo.toml"])?;
    checked(
        root,
        "cargo",
        &[
            "test",
            "--manifest-path",
            "tools/live-control-client/Cargo.toml",
        ],
    )?;
    checked(
        root,
        "cargo",
        &["test", "--manifest-path", "tools/numerics/Cargo.toml"],
    )?;
    checked(
        root,
        "cargo",
        &[
            "test",
            "--manifest-path",
            "tools/native-boundary/Cargo.toml",
        ],
    )
}

fn repository_root() -> Result<PathBuf> {
    let manifest = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    manifest
        .parent()
        .and_then(Path::parent)
        .map(Path::to_path_buf)
        .ok_or_else(|| "cannot resolve repository root".into())
}

fn portable(root: &Path) -> Result {
    checked(root, "cargo", &["test", "--locked", "--manifest-path", "tools/part-systems/Cargo.toml"])?;
    checked(
        root,
        "dotnet",
        &["build", "tools/KspContinuum.FlightFixture", "-c", "Release"],
    )?;
    for project in ["tools/KspContinuum.RuntimeMap", "tools/KspContinuum.SourceAtlas", "tools/KspContinuum.OwnedFlightLab", "tools/KspContinuum.OwnedFlightTimelineLab", "tools/KspContinuum.CausalFlightLab"] {
        checked(root, "dotnet", &["build", project, "-c", "Release"])?;
    }
    checked(root, "dotnet", &["run", "--project", "tools/KspContinuum.SourceAtlas", "-c", "Release", "--", "self-test"])?;
    for project in DOTNET_TESTS {
        checked(
            root,
            "dotnet",
            &["run", "--project", project, "-c", "Release"],
        )?;
    }
    verify_encounter(root)?;
    verify_handoff(root)?;
    verify_islands(root)?;
    verify_fleet(root)?;
    verify_layout(root)?;
    verify_program_branch(root)?;
    verify_worker(root)?;
    verify_worldline(root)?;
    Ok(())
}

fn structural(root: &Path) -> Result {
    checked(
        root,
        "cmake",
        &[
            "-S",
            "tools/structural-bench",
            "-B",
            "artifacts/structural-ci",
            "-DCMAKE_BUILD_TYPE=Release",
        ],
    )?;
    checked(
        root,
        "cmake",
        &["--build", "artifacts/structural-ci", "--parallel", "2"],
    )?;
    checked(
        root,
        "ctest",
        &[
            "--test-dir",
            "artifacts/structural-ci",
            "--output-on-failure",
        ],
    )
}

fn checked(root: &Path, program: &str, arguments: &[&str]) -> Result {
    eprintln!("+ {program} {}", arguments.join(" "));
    let status = Command::new(program)
        .args(arguments)
        .current_dir(root)
        .status()
        .map_err(|e| format!("failed to start {program}: {e}"))?;
    if status.success() {
        Ok(())
    } else {
        Err(format!("{program} exited with {status}"))
    }
}

fn captured(root: &Path, program: &str, arguments: &[&str]) -> Result<Output> {
    Command::new(program)
        .args(arguments)
        .current_dir(root)
        .output()
        .map_err(|e| format!("failed to start {program}: {e}"))
}

fn dotnet_json(root: &Path, project: &str, arguments: &[&str]) -> Result<Value> {
    let mut args = vec!["run", "--project", project, "-c", "Release", "--"];
    args.extend_from_slice(arguments);
    let output = captured(root, "dotnet", &args)?;
    require(
        output.status.success(),
        &format!(
            "{project} failed: {}{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        ),
    )?;
    serde_json::from_slice(&output.stdout)
        .map_err(|e| format!("{project} emitted invalid JSON: {e}"))
}

fn receipt(
    root: &Path,
    project: &str,
    arguments: &[&str],
) -> Result<(Value, tempfile::TempDir, PathBuf)> {
    let directory = tempfile::tempdir().map_err(|e| e.to_string())?;
    let output_path = directory.path().join("receipt.json");
    let output_string = output_path.to_string_lossy().into_owned();
    let mut args = arguments.to_vec();
    args.extend(["--output", &output_string]);
    let mut command = vec!["run", "--project", project, "-c", "Release", "--"];
    command.extend(args);
    let output = captured(root, "dotnet", &command)?;
    require(
        output.status.success(),
        &format!(
            "{project} failed: {}{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        ),
    )?;
    let bytes = fs::read(&output_path).map_err(|e| e.to_string())?;
    let json = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
    Ok((json, directory, output_path))
}

fn verify_encounter(root: &Path) -> Result {
    let (r, _directory, _) = receipt(
        root,
        "tools/KspContinuum.EncounterBench",
        &["--quiet", "32", "--samples", "2"],
    )?;
    eq_str(&r, "schema", "ksp-continuum-encounter-bench/v1")?;
    truth(&r, "qualified")?;
    truth(&r, "permutationStable")?;
    truth(&r, "stalePlanRejected")?;
    truth(&r, "foreignPlanRejected")?;
    falsity(&r, "physicsIntegrated")?;
    falsity(&r, "parallelExecutionQualified")?;
    let cases = named_rows(&r["cases"])?;
    require(
        cases["mixed-fast-crossing"]["refinedBodies"] == 2,
        "encounter refinement changed",
    )?;
    require(
        cases["dense-budget-exhaustion"]["status"] == "budget-exhausted",
        "dense workload did not exhaust its budget",
    )?;
    for name in [
        "tangent",
        "near-miss",
        "acceleration-uncertainty",
        "zero-lookahead",
        "curved-interior-crossing",
    ] {
        require(
            cases.contains_key(name),
            &format!("missing encounter case {name}"),
        )?;
    }
    Ok(())
}

fn verify_handoff(root: &Path) -> Result {
    let (r, _directory, _) = receipt(root, "tools/KspContinuum.HandoffBench", &[])?;
    eq_str(&r, "schema", "ksp-continuum-handoff-bench/v1")?;
    truth(&r, "qualified")?;
    falsity(&r, "gameIntegrated")?;
    falsity(&r, "asynchronousIslandsQualified")?;
    let runs = array(&r, "runs")?;
    require(runs.len() == 3, "handoff benchmark must emit three runs")?;
    for row in runs {
        require(row["status"] == "Committed", "handoff did not commit")?;
        require(row["contactSeconds"] == 9, "handoff contact time changed")?;
        require(row["replayEquivalent"] == true, "handoff replay diverged")?;
    }
    Ok(())
}

fn verify_islands(root: &Path) -> Result {
    let r = dotnet_json(
        root,
        "tools/KspContinuum.IslandBench",
        &["--repetitions", "2", "--parallelism", "4"],
    )?;
    eq_str(&r, "schema", "ksp-continuum-island-bench/v1")?;
    let rows = named_by(&r["workloads"], "workload")?;
    let names: BTreeSet<_> = rows.keys().copied().collect();
    require(
        names == BTreeSet::from(["long-chain", "many-tiny-islands", "mixed-sizes"]),
        "island workload set changed",
    )?;
    require(
        rows["many-tiny-islands"]["islands"] == 4096,
        "tiny-island count changed",
    )?;
    for row in rows.values() {
        require(
            row["exactOutputMatch"] == true,
            "parallel island output diverged",
        )?;
    }
    truth(&r["safety"], "cancellationObserved")?;
    truth(&r["safety"], "failureObserved")
}

fn verify_fleet(root: &Path) -> Result {
    let output = Command::new("dotnet").args(["run", "--project", "tools/KspContinuum.FleetBench", "-c", "Release", "--",
        "--quick", "--samples", "3"]).env("DOTNET_TieredCompilation", "0").current_dir(root).output()
        .map_err(|error| format!("fleet benchmark failed to start: {error}"))?;
    require(output.status.success(), &format!("fleet benchmark failed: {}{}",
        String::from_utf8_lossy(&output.stdout), String::from_utf8_lossy(&output.stderr)))?;
    let r: Value = serde_json::from_slice(&output.stdout).map_err(|error| error.to_string())?;
    eq_str(&r, "schema", "ksp-continuum-fleet-bench/v1")?;
    truth(&r, "quick")?;
    truth(&r, "tieredCompilationDisabled")?;
    let rows = array(&r, "rows")?;
    require(!rows.is_empty(), "fleet benchmark emitted no rows")?;
    for row in rows {
        require(row["deterministicOrder"] == true, "fleet worker output order diverged")?;
        require(row["medianMilliseconds"].as_f64().unwrap_or(0.0) > 0.0, "fleet timing missing")?;
        require(row["medianAllocatedBytes"].as_i64().unwrap_or(-1) >= 0, "fleet allocation missing")?;
    }
    Ok(())
}

fn verify_layout(root: &Path) -> Result {
    let r = dotnet_json(
        root,
        "tools/KspContinuum.LayoutBench",
        &["--bodies", "32", "--samples", "3", "--seed", "73"],
    )?;
    eq_str(&r, "schema", "ksp-continuum-layout-bench/v2")?;
    truth(&r, "immutableLogicalCaptures")?;
    falsity(&r, "poolingUsed")?;
    falsity(&r, "stockPhysicsSpeedupMeasured")?;
    let results = array(&r["cases"][0], "results")?;
    require(results.len() == 6, "layout matrix must contain six results")?;
    for row in results {
        require(
            row["maxPositionError"].as_f64().unwrap_or(f64::INFINITY) <= 1e-12,
            "layout position error exceeded tolerance",
        )?;
        require(
            row["maxVelocityError"].as_f64().unwrap_or(f64::INFINITY) <= 1e-12,
            "layout velocity error exceeded tolerance",
        )?;
        let observation = &row["observation"];
        eq_str(observation, "schema", "continuum-performance-observation/v1")?;
        require(observation["workload"]["items"] == 32, "performance workload identity changed")?;
        require(observation["workload"]["stepSeconds"] == 0.02,
            "performance workload timestep changed")?;
        require(observation["workload"]["configurationSha256"] == r["configurationSha256"],
            "performance configuration identity changed")?;
        require(observation["environmentSha256"] == r["environmentSha256"],
            "performance environment identity changed")?;
        require(observation["strategy"] == row["strategy"], "performance strategy identity changed")?;
        for phase in ["capture", "pack", "compute", "synchronize", "publish", "total"] {
            require(array(&observation[phase], "milliseconds")?.len() == 3,
                &format!("performance phase {phase} sample count changed"))?;
            require(observation[phase]["allocations"]["available"] == true,
                &format!("performance phase {phase} allocation availability changed"))?;
            require(array(&observation[phase]["allocations"], "bytes")?.len() == 3,
                &format!("performance phase {phase} allocation count changed"))?;
        }
    }
    Ok(())
}

fn verify_program_branch(root: &Path) -> Result {
    let first = dotnet_json(root, "tools/KspContinuum.ProgramBranchToy", &["20260921"])?;
    let second = dotnet_json(root, "tools/KspContinuum.ProgramBranchToy", &["20260921"])?;
    require(
        first == second,
        "program branch receipt is not deterministic",
    )?;
    eq_str(&first, "schema", "ksp-continuum-program-branch-receipt/v1")?;
    truth(&first, "serialParallelEqual")?;
    truth(&first, "staleRejected")?;
    require(
        first["samples"] == 256,
        "program branch sample count changed",
    )
}

fn verify_worker(root: &Path) -> Result {
    let r = dotnet_json(
        root,
        "tools/KspContinuum.WorkerBench",
        &["--bodies", "32", "--samples", "4"],
    )?;
    eq_str(&r, "schema", "ksp-continuum-worker-bench/v1")?;
    falsity(&r, "stockPhysicsSpeedupMeasured")?;
    require(
        array(&r, "results")?.len() == 3,
        "worker strategy count changed",
    )?;
    let h = dotnet_json(
        root,
        "tools/KspContinuum.WorkerBench",
        &[
            "--mode",
            "handoff-layout",
            "--bodies",
            "32",
            "--samples",
            "4",
        ],
    )?;
    eq_str(&h, "schema", "ksp-continuum-handoff-layout/v1")?;
    require(
        array(&h, "results")?.len() == 2,
        "handoff layout count changed",
    )
}

fn verify_worldline(root: &Path) -> Result {
    let (r, _directory, _) = receipt(root, "tools/KspContinuum.WorldlineTubeToy", &[])?;
    eq_str(&r, "schema", "ksp-continuum-worldline-tube-toy/v1")?;
    truth(&r, "qualified")?;
    require(
        r["falseNegatives"] == 0,
        "worldline screen introduced a false negative",
    )?;
    let rows = named_rows(&r["cases"])?;
    for name in [
        "accelerated-crossing",
        "crossing",
        "tangent",
        "uncertain-burn",
    ] {
        require(
            rows[name]["oracleContact"] == true && rows[name]["status"] == "candidate",
            &format!("worldline case {name} was not retained"),
        )?;
    }
    Ok(())
}

fn verify_structural_report(root: &Path, binary: &Path) -> Result {
    let r = binary_json(root, binary)?;
    eq_str(&r, "schema", "ksp-continuum-structural/v1")?;
    require(
        r["joltCommit"] == "e77f175595e64cb44218cc9d9d56fc365ad0e36a",
        "Jolt revision changed",
    )?;
    falsity(&r, "stockPhysicsSpeedupMeasured")?;
    truth(&r, "doublePrecisionPositions")?;
    let runs = array(&r, "oscillatorRuns")?;
    require(
        runs.len() == 15,
        "structural benchmark must emit fifteen oscillator runs",
    )?;
    for row in runs {
        require(
            array(row, "samples")?.len() == 251,
            "oscillator sample count changed",
        )?;
        if row["collisionSteps"] == 16 {
            require(
                row["qualified"] == true,
                "finest structural run did not qualify",
            )?;
        }
    }
    require(
        array(&r["drop"], "samples")?.len() == 501,
        "drop sample count changed",
    )?;
    truth(&r["drop"], "qualified")
}

fn verify_checkpoint_report(root: &Path, binary: &Path) -> Result {
    let r = binary_json(root, binary)?;
    eq_str(&r, "schema", "ksp-continuum-checkpoint/v2")?;
    for field in [
        "qualified",
        "savedExact",
        "restoredBytesEqual",
        "checkpointContact",
    ] {
        truth(&r, field)?;
    }
    for field in [
        "callbackReplayQualified",
        "physicalAccuracyQualified",
        "crossPlatformReplayQualified",
        "gameIntegrated",
    ] {
        falsity(&r, field)?;
    }
    require(
        r["originalBodyIds"] == r["coldBodyIds"],
        "checkpoint body identifiers changed",
    )?;
    require(
        array(&r, "incompatible")?.len() == 4,
        "checkpoint incompatibility matrix changed",
    )?;
    let runs = array(&r, "runs")?;
    require(runs.len() == 4, "checkpoint benchmark must emit four runs")?;
    for row in runs {
        require(
            array(row, "samples")?.len() == 121,
            "checkpoint sample count changed",
        )?;
    }
    Ok(())
}

fn binary_json(root: &Path, binary: &Path) -> Result<Value> {
    let path = if binary.is_absolute() {
        binary.to_path_buf()
    } else {
        root.join(binary)
    };
    let output = Command::new(&path)
        .current_dir(root)
        .output()
        .map_err(|e| format!("failed to start {}: {e}", path.display()))?;
    require(
        output.status.success(),
        &format!(
            "{} failed: {}",
            path.display(),
            String::from_utf8_lossy(&output.stderr)
        ),
    )?;
    serde_json::from_slice(&output.stdout)
        .map_err(|e| format!("{} emitted invalid JSON: {e}", path.display()))
}

fn array<'a>(value: &'a Value, field: &str) -> Result<&'a Vec<Value>> {
    value[field]
        .as_array()
        .ok_or_else(|| format!("{field} is not an array"))
}

fn named_rows(value: &Value) -> Result<BTreeMap<&str, &Value>> {
    named_by(value, "name")
}

fn named_by<'a>(value: &'a Value, key: &str) -> Result<BTreeMap<&'a str, &'a Value>> {
    value
        .as_array()
        .ok_or_else(|| "expected array".to_string())?
        .iter()
        .map(|row| {
            row[key]
                .as_str()
                .map(|name| (name, row))
                .ok_or_else(|| format!("row has no string {key}"))
        })
        .collect()
}

fn eq_str(value: &Value, field: &str, expected: &str) -> Result {
    require(
        value[field] == expected,
        &format!("{field} must be {expected}"),
    )
}

fn truth(value: &Value, field: &str) -> Result {
    require(value[field] == true, &format!("{field} must be true"))
}
fn falsity(value: &Value, field: &str) -> Result {
    require(value[field] == false, &format!("{field} must be false"))
}
fn require(condition: bool, message: &str) -> Result {
    if condition {
        Ok(())
    } else {
        Err(message.into())
    }
}
