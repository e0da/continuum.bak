use std::{env, path::Path, process::ExitCode};

fn run() -> continuum_research_lab::Result<()> {
    let args: Vec<_> = env::args().skip(1).collect();
    if args.len() != 6 || args[0] != "--recording" || args[2] != "--probe" || args[4] != "--output"
    {
        return Err(
            "usage: continuum-part-systems --recording FILE --probe FILE --output NEW-FILE".into(),
        );
    }
    let result = continuum_part_systems::compare(Path::new(&args[1]), Path::new(&args[3]))?;
    continuum_research_lab::write_new(Path::new(&args[5]), &result)?;
    println!(
        "PASS retained dense/hecs original-tape comparison: {} rows/root; {} commands/backend",
        result["rowsPerRoot"], result["verifiedTailCommandsPerBackend"]
    );
    Ok(())
}
fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("{error}");
            ExitCode::FAILURE
        }
    }
}
