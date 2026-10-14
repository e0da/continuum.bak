use serde::{Deserialize, Serialize};
use std::collections::VecDeque;
use std::env;
use std::fs::{self, OpenOptions};
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::path::PathBuf;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const PROTOCOL: &str = "1.0.0";
const CLOCK_PROTOCOL: &str = "1.4.0";
const MAX_SAMPLES: usize = 10_000;

#[derive(Debug)]
struct Options {
    address: String,
    samples: usize,
    output: Option<PathBuf>,
    ready: Option<PathBuf>,
    timeout: Duration,
    sweep: bool,
    quit: bool,
    rigid: Option<String>,
    clock: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Reply {
    status: String,
    reason: Option<String>,
    request_id: Option<String>,
    capabilities: Option<Vec<String>>,
    identity: Option<Identity>,
    snapshot: Option<serde_json::Value>,
    observer_nanoseconds: Option<u64>,
    sweep: Option<SweepStatus>,
    rigid: Option<serde_json::Value>,
    clock: Option<serde_json::Value>,
    protocol_version: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SweepStatus {
    state: String,
    reason: Option<String>,
    directory: Option<String>,
    window: Option<String>,
    window_index: i32,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Identity {
    session_id: String,
    epoch: u64,
    render_frame: u64,
    observed_fixed_callbacks: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Sample {
    sequence: usize,
    render_frame: u64,
    observed_fixed_callbacks: u64,
    round_trip_nanoseconds: u64,
    observer_nanoseconds: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ReadyReceipt<'a> {
    schema: &'static str,
    status: &'static str,
    session_id: &'a str,
    epoch: u64,
    accepted_snapshot_sequence: usize,
    render_frame: u64,
    observed_fixed_callbacks: u64,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Receipt {
    schema: &'static str,
    protocol_version: &'static str,
    address: String,
    requested_samples: usize,
    completed_samples: usize,
    started_unix_milliseconds: u128,
    elapsed_nanoseconds: u64,
    status: &'static str,
    session_id: String,
    epoch: u64,
    samples: Vec<Sample>,
}

fn main() {
    if let Err(error) = run(env::args().skip(1).collect()) {
        eprintln!("live-control-client: {error}");
        std::process::exit(1);
    }
}

fn run(arguments: Vec<String>) -> Result<(), String> {
    let options = parse_options(&arguments)?;
    if let Some(action) = &options.rigid { return run_rigid(&options, action); }
    if let Some(action) = &options.clock { return run_clock(&options, action); }
    if options.quit {
        return run_quit(&options);
    }
    if options.sweep {
        return run_sweep(&options);
    }
    let started_wall = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|_| "system clock precedes Unix epoch".to_string())?
        .as_millis();
    let started = Instant::now();
    let mut stream = connect(&options)?;
    let mut reader = BufReader::new(stream.try_clone().map_err(|e| e.to_string())?);
    let mut observations = VecDeque::new();
    let hello = exchange(
        &mut stream,
        &mut reader,
        &mut observations,
        "hello 1.0.0 qualification-hello",
    )?;
    require_ok(&hello, "qualification-hello", false)?;
    let identity = hello.identity.ok_or("hello reply omitted identity")?;
    let mut samples = Vec::with_capacity(options.samples);
    for sequence in 0..options.samples {
        let request_id = format!("qualification-{sequence}");
        let command = format!(
            "snapshot {PROTOCOL} {request_id} {} {} 0",
            identity.session_id, identity.epoch
        );
        let before = Instant::now();
        let reply = exchange(&mut stream, &mut reader, &mut observations, &command)?;
        let round_trip = nanos(before.elapsed())?;
        require_ok(&reply, &request_id, true)?;
        let observed = reply
            .identity
            .as_ref()
            .ok_or("snapshot reply omitted identity")?;
        if observed.session_id != identity.session_id || observed.epoch != identity.epoch {
            return Err(format!("request {request_id} changed identity"));
        }
        if sequence == 0 {
            write_new(
                options.ready.as_ref().ok_or("missing readiness path")?,
                &ReadyReceipt {
                    schema: "ksp-continuum-live-control-ready/v1",
                    status: "ready",
                    session_id: &identity.session_id,
                    epoch: identity.epoch,
                    accepted_snapshot_sequence: sequence,
                    render_frame: observed.render_frame,
                    observed_fixed_callbacks: observed.observed_fixed_callbacks,
                },
                "readiness marker",
            )?;
        }
        samples.push(Sample {
            sequence,
            render_frame: observed.render_frame,
            observed_fixed_callbacks: observed.observed_fixed_callbacks,
            round_trip_nanoseconds: round_trip,
            observer_nanoseconds: reply
                .observer_nanoseconds
                .ok_or("snapshot reply omitted observerNanoseconds")?,
        });
    }
    let receipt = Receipt {
        schema: "ksp-continuum-live-control-qualification/v1",
        protocol_version: PROTOCOL,
        address: options.address,
        requested_samples: options.samples,
        completed_samples: samples.len(),
        started_unix_milliseconds: started_wall,
        elapsed_nanoseconds: nanos(started.elapsed())?,
        status: "complete",
        session_id: identity.session_id,
        epoch: identity.epoch,
        samples,
    };
    let output = options.output.as_ref().ok_or("missing output path")?;
    write_new(output, &receipt, "receipt")?;
    println!("{}", output.display());
    Ok(())
}

fn run_rigid(options: &Options, action: &str) -> Result<(), String> {
    let mut stream = connect(options)?;
    let mut reader = BufReader::new(stream.try_clone().map_err(|error| error.to_string())?);
    let mut observations = VecDeque::new();
    let hello = exchange(&mut stream, &mut reader, &mut observations, "hello 1.3.0 rigid-hello")?;
    require_ok(&hello, "rigid-hello", false)?;
    if !hello.capabilities.as_ref().is_some_and(|capabilities| capabilities.iter().any(|value| value == "playable-rigid-control")) {
        return Err("server does not support playable rigid control".into());
    }
    let identity = hello.identity.ok_or("rigid hello omitted identity")?;
    let command = format!("rigid-{action} 1.3.0 rigid-command {} {}", identity.session_id, identity.epoch);
    let reply = exchange(&mut stream, &mut reader, &mut observations, &command)?;
    require_ok(&reply, "rigid-command", false)?;
    let status = reply.rigid.ok_or("rigid reply omitted status")?;
    let receipt = serde_json::json!({"schema":"continuum-rigid-control-observation/v1","operation":action,
        "sessionId":identity.session_id,"epoch":identity.epoch,"status":status});
    let text = serde_json::to_string_pretty(&receipt).map_err(|error| error.to_string())?;
    if let Some(path) = &options.output {
        let mut file = OpenOptions::new().write(true).create_new(true).open(path).map_err(|error| error.to_string())?;
        writeln!(file, "{text}").map_err(|error| error.to_string())?;
    }
    println!("{text}");
    Ok(())
}

fn require_clock_identity(reply: &Reply, request_id: &str, expected: Option<&Identity>) -> Result<(), String> {
    require_ok(reply, request_id, false)?;
    if reply.protocol_version.as_deref() != Some(CLOCK_PROTOCOL) {
        return Err("clock reply did not use protocol 1.4.0".into());
    }
    let observed = reply.identity.as_ref().ok_or("clock reply omitted identity")?;
    if observed.session_id.is_empty() || observed.epoch == 0 {
        return Err("clock reply has an invalid identity".into());
    }
    if let Some(identity) = expected {
        if observed.session_id != identity.session_id || observed.epoch != identity.epoch {
            return Err("clock reply changed session or epoch".into());
        }
    }
    Ok(())
}

fn run_clock(options: &Options, action: &str) -> Result<(), String> {
    if options.output.as_ref().is_some_and(|path| path.exists()) {
        return Err("clock output already exists; no command sent".into());
    }
    let mut stream = connect(options)?;
    let mut reader = BufReader::new(stream.try_clone().map_err(|error| error.to_string())?);
    let mut observations = VecDeque::new();
    let hello = exchange(&mut stream, &mut reader, &mut observations, "hello 1.4.0 clock-hello")?;
    require_clock_identity(&hello, "clock-hello", None)?;
    if !hello.capabilities.as_ref().is_some_and(|capabilities| capabilities.iter().any(|value| value == "owned-clock-control")) {
        return Err("server does not support owned clock control".into());
    }
    let identity = hello.identity.as_ref().ok_or("clock hello omitted identity")?;
    let command = format!("clock-{action} {CLOCK_PROTOCOL} clock-command {} {}", identity.session_id, identity.epoch);
    let reply = exchange(&mut stream, &mut reader, &mut observations, &command)?;
    require_clock_identity(&reply, "clock-command", Some(identity))?;
    let status = reply.clock.as_ref().filter(|value| value.is_object()).ok_or("clock reply omitted its status object")?;
    if action == "lighting" && !status.get("lightingPath").and_then(serde_json::Value::as_str).is_some_and(|value| std::path::Path::new(value).is_absolute()) {
        return Err("successful clock lighting reply omitted its absolute saved path".into());
    }
    let receipt = serde_json::json!({"schema":"continuum-clock-control-observation/v1","protocolVersion":CLOCK_PROTOCOL,"operation":action,
        "requestId":"clock-command","sessionId":identity.session_id,"epoch":identity.epoch,"status":"ok","clock":status});
    if let Some(path) = &options.output { write_new(path, &receipt, "clock receipt")?; }
    println!("{}", serde_json::to_string_pretty(&receipt).map_err(|error| error.to_string())?);
    Ok(())
}

fn run_quit(options: &Options) -> Result<(), String> {
    let mut stream = connect(options)?;
    let mut reader = BufReader::new(stream.try_clone().map_err(|e| e.to_string())?);
    let mut observations = VecDeque::new();
    let hello = exchange(
        &mut stream,
        &mut reader,
        &mut observations,
        "hello 1.1.0 quit-hello",
    )?;
    require_ok(&hello, "quit-hello", false)?;
    let identity = hello.identity.ok_or("hello reply omitted identity")?;
    let command = format!(
        "quit-when-idle 1.1.0 quit-request {} {}",
        identity.session_id, identity.epoch
    );
    let reply = exchange(&mut stream, &mut reader, &mut observations, &command)?;
    require_ok(&reply, "quit-request", false)
}

fn run_sweep(options: &Options) -> Result<(), String> {
    let mut stream = connect(options)?;
    let mut reader = BufReader::new(stream.try_clone().map_err(|e| e.to_string())?);
    let mut observations = VecDeque::new();
    let hello = exchange(
        &mut stream,
        &mut reader,
        &mut observations,
        "hello 1.1.0 sweep-hello",
    )?;
    require_ok(&hello, "sweep-hello", false)?;
    if !hello.capabilities.as_ref().is_some_and(|capabilities| {
        capabilities
            .iter()
            .any(|capability| capability == "dry-buoyancy-sweep-push")
    }) {
        return Err("server does not support pushed dry-buoyancy sweeps".into());
    }
    let identity = hello.identity.ok_or("hello reply omitted identity")?;
    let start = format!(
        "sweep-start 1.1.0 sweep-start {} {} dry-buoyancy",
        identity.session_id, identity.epoch
    );
    let reply = exchange(&mut stream, &mut reader, &mut observations, &start)?;
    require_ok(&reply, "sweep-start", false)?;
    let started = require_sweep_started(&reply)?;
    print_sweep(started);
    let subscribe = format!(
        "sweep-subscribe 1.2.0 sweep-subscribe {} {} dry-buoyancy",
        identity.session_id, identity.epoch
    );
    let reply = exchange(&mut stream, &mut reader, &mut observations, &subscribe)?;
    require_ok(&reply, "sweep-subscribe", false)?;
    let subscribed = reply
        .sweep
        .as_ref()
        .ok_or("subscribe reply omitted sweep")?;
    let mut previous = sweep_key(subscribed);
    if previous != sweep_key(started) {
        print_sweep(subscribed);
    }
    if terminal_sweep(subscribed)? {
        return Ok(());
    }
    loop {
        let reply = match observations.pop_front() {
            Some(observation) => observation,
            None => read_reply(&mut reader)?,
        };
        if reply.request_id.is_some() || reply.status != "observation" {
            return Err("expected pushed sweep observation".into());
        }
        let observed = reply
            .identity
            .as_ref()
            .ok_or("observation omitted identity")?;
        if observed.session_id != identity.session_id || observed.epoch != identity.epoch {
            return Err("sweep observation changed identity".into());
        }
        let sweep = reply.sweep.as_ref().ok_or("observation omitted sweep")?;
        let current = sweep_key(sweep);
        if current != previous {
            print_sweep(sweep);
            previous = current;
        }
        if terminal_sweep(sweep)? {
            return Ok(());
        }
    }
}

fn sweep_key(sweep: &SweepStatus) -> String {
    format!(
        "{}:{}:{}",
        sweep.state,
        sweep.window_index,
        sweep.window.as_deref().unwrap_or("")
    )
}

fn terminal_sweep(sweep: &SweepStatus) -> Result<bool, String> {
    match sweep.state.as_str() {
        "complete" => Ok(true),
        "invalid" | "error" | "interrupted" | "unavailable" => Err(format!(
            "sweep ended {}: {}",
            sweep.state,
            sweep.reason.as_deref().unwrap_or("unspecified")
        )),
        "waiting-for-orbit" | "running" | "idle" => Ok(false),
        other => Err(format!("unknown sweep state {other}")),
    }
}

fn require_sweep_started(reply: &Reply) -> Result<&SweepStatus, String> {
    let sweep = reply.sweep.as_ref().ok_or("start reply omitted sweep")?;
    if let Some(reason) = &sweep.reason {
        return Err(format!("sweep start rejected: {reason}"));
    }
    Ok(sweep)
}

fn print_sweep(sweep: &SweepStatus) {
    println!(
        "state={} windowIndex={} window={} directory={} reason={}",
        sweep.state,
        sweep.window_index,
        sweep.window.as_deref().unwrap_or("-"),
        sweep.directory.as_deref().unwrap_or("-"),
        sweep.reason.as_deref().unwrap_or("-")
    );
}

fn write_new<T: Serialize>(path: &PathBuf, value: &T, kind: &str) -> Result<(), String> {
    let encoded = serde_json::to_vec_pretty(value).map_err(|e| e.to_string())?;
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let mut output = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|e| format!("cannot create {kind}: {e}"))?;
    output
        .write_all(&encoded)
        .map_err(|e| format!("cannot write {kind}: {e}"))?;
    Ok(())
}

fn connect(options: &Options) -> Result<TcpStream, String> {
    let address = options
        .address
        .to_socket_addrs()
        .map_err(|e| format!("invalid address: {e}"))?
        .next()
        .ok_or("address resolved to no endpoints")?;
    if !address.ip().is_loopback() {
        return Err("address must resolve to loopback".into());
    }
    let stream = TcpStream::connect_timeout(&address, options.timeout)
        .map_err(|e| format!("connect failed: {e}"))?;
    stream
        .set_read_timeout(Some(options.timeout))
        .map_err(|e| e.to_string())?;
    stream
        .set_write_timeout(Some(options.timeout))
        .map_err(|e| e.to_string())?;
    Ok(stream)
}

fn exchange(
    stream: &mut TcpStream,
    reader: &mut BufReader<TcpStream>,
    observations: &mut VecDeque<Reply>,
    command: &str,
) -> Result<Reply, String> {
    stream
        .write_all(format!("{command}\n").as_bytes())
        .map_err(|e| format!("write failed: {e}"))?;
    let request_id = command
        .split(' ')
        .nth(2)
        .ok_or("command omitted request ID")?;
    loop {
        let reply = read_reply(reader)?;
        if reply.status == "observation" && reply.request_id.is_none() {
            if observations.len() == 8 {
                return Err("too many observations arrived before command reply".into());
            }
            observations.push_back(reply);
            continue;
        }
        if reply.request_id.as_deref() != Some(request_id) {
            return Err("reply requestId did not match request".into());
        }
        return Ok(reply);
    }
}

fn read_reply(reader: &mut BufReader<TcpStream>) -> Result<Reply, String> {
    let mut line = String::new();
    let count = reader
        .read_line(&mut line)
        .map_err(|e| format!("read failed: {e}"))?;
    if count == 0 {
        return Err("server closed before reply".into());
    }
    serde_json::from_str(&line).map_err(|e| format!("invalid reply JSON: {e}"))
}

fn require_ok(reply: &Reply, request_id: &str, snapshot: bool) -> Result<(), String> {
    if reply.request_id.as_deref() != Some(request_id) {
        return Err("reply requestId did not match request".into());
    }
    if reply.status != "ok" {
        return Err(format!(
            "request {request_id} returned {}: {}",
            reply.status,
            reply.reason.as_deref().unwrap_or("unspecified")
        ));
    }
    if snapshot && reply.snapshot.is_none() {
        return Err(format!("request {request_id} omitted snapshot"));
    }
    Ok(())
}

fn parse_options(arguments: &[String]) -> Result<Options, String> {
    let mut address = "127.0.0.1:47771".to_string();
    let mut samples = 300usize;
    let mut output = None;
    let mut ready = None;
    let mut timeout_ms = 5_000u64;
    let mut timeout_explicit = false;
    let mut sweep = false;
    let mut quit = false;
    let mut rigid = None;
    let mut clock = None;
    let mut qualification_selected = false;
    let mut index = 0;
    while index < arguments.len() {
        if arguments[index] == "--dry-buoyancy-sweep" {
            sweep = true;
            index += 1;
            continue;
        }
        if arguments[index] == "--quit-when-idle" {
            quit = true;
            index += 1;
            continue;
        }
        let value = arguments.get(index + 1).ok_or_else(usage)?;
        if matches!(arguments[index].as_str(), "--samples" | "--ready") {
            qualification_selected = true;
        }
        match arguments[index].as_str() {
            "--address" => address = value.clone(),
            "--samples" => samples = value.parse().map_err(|_| "invalid --samples")?,
            "--output" => output = Some(PathBuf::from(value)),
            "--ready" => ready = Some(PathBuf::from(value)),
            "--timeout-ms" => { timeout_ms = value.parse().map_err(|_| "invalid --timeout-ms")?; timeout_explicit = true; },
            "--rigid" if matches!(value.as_str(), "status" | "enable" | "disable" | "retry" | "warp4" | "normal" | "save" | "load" | "capture") => rigid = Some(value.clone()),
            "--clock" if matches!(value.as_str(), "status" | "enable" | "disable" | "normal" | "warp32" | "warp128" | "pause" | "resume" | "save" | "restore" | "lighting") => {
                if clock.is_some() { return Err("choose one clock command".into()); }
                clock = Some(value.clone());
            },
            _ => return Err(usage()),
        }
        index += 2;
    }
    if samples == 0 || samples > MAX_SAMPLES {
        return Err(format!("--samples must be between 1 and {MAX_SAMPLES}"));
    }
    if !timeout_explicit && clock.as_deref().is_some_and(|action| matches!(action, "enable" | "disable" | "save" | "restore")) {
        timeout_ms = 65_000;
    }
    let maximum_timeout = if clock.is_some() { 65_000 } else { 60_000 };
    if timeout_ms == 0 || timeout_ms > maximum_timeout {
        return Err(format!("--timeout-ms must be between 1 and {maximum_timeout}"));
    }
    if (usize::from(sweep) + usize::from(quit) + usize::from(rigid.is_some()) + usize::from(clock.is_some())) > 1 {
        return Err("choose one live command".into());
    }
    if clock.is_some() && qualification_selected {
        return Err("choose one live command; clock and snapshot qualification cannot mix".into());
    }
    if !sweep && !quit && rigid.is_none() && clock.is_none() && (output.is_none() || ready.is_none()) {
        return Err(usage());
    }
    if output.is_some() && output == ready {
        return Err("--ready and --output must be different paths".into());
    }
    Ok(Options {
        address,
        samples,
        output,
        ready,
        timeout: Duration::from_millis(timeout_ms),
        sweep,
        quit,
        rigid,
        clock,
    })
}

fn nanos(duration: Duration) -> Result<u64, String> {
    duration
        .as_nanos()
        .try_into()
        .map_err(|_| "duration exceeds receipt range".into())
}

fn usage() -> String {
    "usage: continuum-live-control-client [--address HOST:PORT] [--timeout-ms 1..60000 (clock up to 65000)] (--clock status|enable|disable|normal|warp32|warp128|pause|resume|save|restore|lighting [--output RECEIPT.json] | --rigid status|enable|disable|retry|warp4|normal|save|load|capture [--output RECEIPT.json] | --dry-buoyancy-sweep | --quit-when-idle | [--samples 1..10000] --ready READY.json --output RECEIPT.json). Clock enable/disable/save/restore default to 65000 ms; other defaults remain 5000 ms.".into()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;
    use std::thread;

    #[test]
    fn rejects_unbounded_sample_count() {
        let args = vec![
            "--samples".into(),
            "10001".into(),
            "--output".into(),
            "x".into(),
            "--ready".into(),
            "r".into(),
        ];
        assert!(parse_options(&args).unwrap_err().contains("between 1 and"));
    }

    #[test]
    fn live_commands_do_not_require_receipt_paths() {
        let sweep = parse_options(&["--dry-buoyancy-sweep".into()]).unwrap();
        assert!(sweep.sweep && !sweep.quit && sweep.output.is_none());
        let quit = parse_options(&["--quit-when-idle".into()]).unwrap();
        assert!(quit.quit && !quit.sweep && quit.ready.is_none());
        assert!(
            parse_options(&["--dry-buoyancy-sweep".into(), "--quit-when-idle".into()])
                .unwrap_err()
                .contains("choose one")
        );
    }

    #[test]
    fn clock_actions_are_bounded_and_keep_old_defaults() {
        for action in ["status", "enable", "disable", "normal", "warp32", "warp128", "pause", "resume", "save", "restore", "lighting"] {
            let options = parse_options(&["--clock".into(), action.into()]).unwrap();
            assert_eq!(options.clock.as_deref(), Some(action));
            assert!(!options.sweep && !options.quit && options.rigid.is_none());
            let timeout = if matches!(action, "enable" | "disable" | "save" | "restore") { 65_000 } else { 5_000 };
            assert_eq!(options.timeout, Duration::from_millis(timeout));
        }
        assert_eq!(parse_options(&["--rigid".into(), "status".into()]).unwrap().timeout, Duration::from_millis(5_000));
        assert!(parse_options(&["--clock".into(), "warp4".into()]).is_err());
        assert!(parse_options(&["--clock".into(), "status".into(), "--timeout-ms".into(), "65001".into()]).is_err());
    }

    #[test]
    fn clock_cannot_mix_with_other_live_actions() {
        for command in [vec!["--rigid", "status"], vec!["--dry-buoyancy-sweep"], vec!["--quit-when-idle"], vec!["--clock", "pause"], vec!["--samples", "1"], vec!["--ready", "ready.json"]] {
            let mut arguments = vec!["--clock".to_string(), "status".to_string()];
            arguments.extend(command.into_iter().map(str::to_string));
            assert!(parse_options(&arguments).unwrap_err().contains("choose one"));
        }
    }

    #[test]
    fn clock_contract_is_version_capability_and_identity_bound() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let mut reader = BufReader::new(socket.try_clone().unwrap());
            for expected in ["hello 1.4.0 clock-hello", "clock-lighting 1.4.0 clock-command 00000000-0000-0000-0000-000000000001 7"] {
                let mut line = String::new(); reader.read_line(&mut line).unwrap(); assert_eq!(line.trim_end(), expected);
                let reply = if expected.starts_with("hello") {
                    r#"{"status":"ok","protocolVersion":"1.4.0","requestId":"clock-hello","capabilities":["owned-clock-control"],"identity":{"sessionId":"00000000-0000-0000-0000-000000000001","epoch":7,"renderFrame":10,"observedFixedCallbacks":4}}"#
                } else {
                    r#"{"status":"ok","protocolVersion":"1.4.0","requestId":"clock-command","identity":{"sessionId":"00000000-0000-0000-0000-000000000001","epoch":7,"renderFrame":11,"observedFixedCallbacks":4},"clock":{"state":"owned","owned":true,"enabled":true,"lightingPath":"/runtime/lighting-facts.json"}}"#
                };
                writeln!(socket, "{reply}").unwrap();
            }
        });
        let options = parse_options(&["--clock".into(), "lighting".into(), "--address".into(), address.to_string(), "--timeout-ms".into(), "2000".into()]).unwrap();
        run_clock(&options, "lighting").unwrap(); server.join().unwrap();
        let expected = Identity { session_id: "session".into(), epoch: 7, render_frame: 1, observed_fixed_callbacks: 1 };
        let foreign: Reply = serde_json::from_str(r#"{"status":"ok","protocolVersion":"1.4.0","requestId":"clock-command","identity":{"sessionId":"session","epoch":8,"renderFrame":2,"observedFixedCallbacks":1},"clock":{"state":"owned"}}"#).unwrap();
        assert!(require_clock_identity(&foreign, "clock-command", Some(&expected)).unwrap_err().contains("changed session or epoch"));
        let old: Reply = serde_json::from_str(r#"{"status":"ok","protocolVersion":"1.3.0","requestId":"clock-hello","identity":{"sessionId":"session","epoch":7,"renderFrame":2,"observedFixedCallbacks":1}}"#).unwrap();
        assert!(require_clock_identity(&old, "clock-hello", None).unwrap_err().contains("protocol 1.4.0"));
    }

    #[test]
    fn rejected_sweep_start_does_not_attach_to_an_existing_run() {
        let reply = Reply {
            rigid: None,
            clock: None,
            protocol_version: None,
            status: "ok".into(),
            reason: None,
            request_id: Some("sweep-start".into()),
            capabilities: None,
            identity: None,
            snapshot: None,
            observer_nanoseconds: None,
            sweep: Some(SweepStatus {
                state: "running".into(),
                reason: Some("qualification-already-active".into()),
                directory: None,
                window: Some("stock-01".into()),
                window_index: 0,
            }),
        };
        assert!(
            require_sweep_started(&reply)
                .unwrap_err()
                .contains("qualification-already-active")
        );
    }

    #[test]
    fn readiness_evidence_cannot_overwrite_existing_file() {
        let path = env::temp_dir().join(format!(
            "continuum-live-control-ready-{}-{}.json",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let marker = ReadyReceipt {
            schema: "ksp-continuum-live-control-ready/v1",
            status: "ready",
            session_id: "session",
            epoch: 1,
            accepted_snapshot_sequence: 0,
            render_frame: 10,
            observed_fixed_callbacks: 4,
        };
        write_new(&path, &marker, "readiness marker").unwrap();
        assert!(write_new(&path, &marker, "readiness marker").is_err());
        let encoded = fs::read_to_string(&path).unwrap();
        assert!(encoded.contains(r#""renderFrame": 10"#));
        fs::remove_file(path).unwrap();
    }

    #[test]
    fn persistent_exchange_is_correlated() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let mut reader = BufReader::new(socket.try_clone().unwrap());
            for expected in [
                "hello 1.0.0 qualification-hello",
                "snapshot 1.0.0 qualification-0 session 7 0",
            ] {
                let mut line = String::new();
                reader.read_line(&mut line).unwrap();
                assert_eq!(line.trim_end(), expected);
                let reply = if expected.starts_with("hello") {
                    r#"{"status":"ok","requestId":"qualification-hello","identity":{"sessionId":"session","epoch":7,"renderFrame":10,"observedFixedCallbacks":4}}"#
                } else {
                    writeln!(socket, r#"{{"status":"observation","sweep":{{"state":"complete","windowIndex":5}}}}"#).unwrap();
                    r#"{"status":"ok","requestId":"qualification-0","identity":{"sessionId":"session","epoch":7,"renderFrame":11,"observedFixedCallbacks":5},"snapshot":{"parts":3},"observerNanoseconds":123}"#
                };
                writeln!(socket, "{reply}").unwrap();
            }
        });
        let mut stream = TcpStream::connect(address).unwrap();
        let mut reader = BufReader::new(stream.try_clone().unwrap());
        let mut observations = VecDeque::new();
        let hello = exchange(
            &mut stream,
            &mut reader,
            &mut observations,
            "hello 1.0.0 qualification-hello",
        )
        .unwrap();
        require_ok(&hello, "qualification-hello", false).unwrap();
        let reply = exchange(
            &mut stream,
            &mut reader,
            &mut observations,
            "snapshot 1.0.0 qualification-0 session 7 0",
        )
        .unwrap();
        require_ok(&reply, "qualification-0", true).unwrap();
        assert_eq!(reply.observer_nanoseconds, Some(123));
        assert_eq!(
            observations.pop_front().unwrap().sweep.unwrap().state,
            "complete"
        );
        server.join().unwrap();
    }

    #[test]
    fn sweep_uses_pushed_terminal_status_without_polling() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let mut reader = BufReader::new(socket.try_clone().unwrap());
            for expected in [
                "hello 1.1.0 sweep-hello",
                "sweep-start 1.1.0 sweep-start",
                "sweep-subscribe 1.2.0 sweep-subscribe",
            ] {
                let mut line = String::new();
                reader.read_line(&mut line).unwrap();
                assert!(line.starts_with(expected), "unexpected command {line}");
                let reply = if expected.starts_with("hello") {
                    r#"{"status":"ok","requestId":"sweep-hello","capabilities":["dry-buoyancy-sweep-push"],"identity":{"sessionId":"00000000-0000-0000-0000-000000000001","epoch":1,"renderFrame":10,"observedFixedCallbacks":4}}"#
                } else if expected.starts_with("sweep-start") {
                    r#"{"status":"ok","requestId":"sweep-start","identity":{"sessionId":"00000000-0000-0000-0000-000000000001","epoch":1,"renderFrame":11,"observedFixedCallbacks":5},"sweep":{"state":"running","window":"stock-01","windowIndex":0}}"#
                } else {
                    r#"{"status":"ok","requestId":"sweep-subscribe","identity":{"sessionId":"00000000-0000-0000-0000-000000000001","epoch":1,"renderFrame":11,"observedFixedCallbacks":5},"sweep":{"state":"running","window":"stock-01","windowIndex":0}}"#
                };
                writeln!(socket, "{reply}").unwrap();
            }
            writeln!(socket, r#"{{"status":"observation","identity":{{"sessionId":"00000000-0000-0000-0000-000000000001","epoch":1,"renderFrame":20,"observedFixedCallbacks":12}},"sweep":{{"state":"complete","directory":"/runtime/report","window":"full-publication-02","windowIndex":5}}}}"#).unwrap();
            socket
                .set_read_timeout(Some(Duration::from_millis(100)))
                .unwrap();
            let mut unexpected = String::new();
            let read = reader.read_line(&mut unexpected);
            assert!(
                matches!(read, Err(_) | Ok(0)),
                "client polled after subscribing: {unexpected}"
            );
        });
        let options = Options {
            address: address.to_string(),
            samples: 1,
            output: None,
            ready: None,
            timeout: Duration::from_secs(2),
            sweep: true,
            quit: false,
            rigid: None,
            clock: None,
        };
        run_sweep(&options).unwrap();
        server.join().unwrap();
    }

    #[test]
    fn old_server_is_rejected_before_sweep_start() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let mut reader = BufReader::new(socket.try_clone().unwrap());
            let mut hello = String::new();
            reader.read_line(&mut hello).unwrap();
            assert_eq!(hello.trim_end(), "hello 1.1.0 sweep-hello");
            writeln!(socket, r#"{{"status":"ok","requestId":"sweep-hello","capabilities":["dry-buoyancy-sweep"],"identity":{{"sessionId":"00000000-0000-0000-0000-000000000001","epoch":1,"renderFrame":10,"observedFixedCallbacks":4}}}}"#).unwrap();
            socket
                .set_read_timeout(Some(Duration::from_millis(100)))
                .unwrap();
            let mut unexpected = String::new();
            assert!(
                matches!(reader.read_line(&mut unexpected), Err(_) | Ok(0)),
                "old server received side-effecting command: {unexpected}"
            );
        });
        let options = Options {
            address: address.to_string(),
            samples: 1,
            output: None,
            ready: None,
            timeout: Duration::from_secs(2),
            sweep: true,
            quit: false,
            rigid: None,
            clock: None,
        };
        assert!(
            run_sweep(&options)
                .unwrap_err()
                .contains("does not support pushed")
        );
        server.join().unwrap();
    }
}
