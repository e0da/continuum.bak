use std::{fs::OpenOptions, io::Write, path::Path, time::Instant};

fn main() {
    if let Err(error) = run() {
        eprintln!("bundle audit: {error}");
        std::process::exit(1);
    }
}
fn run() -> Result<(), String> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() != 4 || args[0] != "--manifest" || args[2] != "--output" {
        return Err("usage: continuum-bundle-audit --manifest FILE --output NEW_JSON".into());
    }
    if Path::new(&args[3]).exists() {
        return Err("audit output must not exist".into());
    }
    let started = Instant::now();
    let mut report = continuum_bundle_audit::audit_manifest(Path::new(&args[1]))?;
    report["auditWallSeconds"] = serde_json::json!(started.elapsed().as_secs_f64());
    report["auditorBinarySha256"] = serde_json::json!(continuum_bundle_audit::file_hash(
        &std::env::current_exe().map_err(|e| e.to_string())?
    )?);
    let bytes = serde_json::to_vec_pretty(&report).map_err(|e| e.to_string())?;
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&args[3])
        .map_err(|e| e.to_string())?;
    output.write_all(&bytes).map_err(|e| e.to_string())?;
    println!(
        "Independent decoded payload and node directory equivalence passed for three bundles."
    );
    Ok(())
}
