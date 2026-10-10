use serde::Serialize;
use std::collections::{HashMap, VecDeque};
use std::ffi::{c_void, CStr};
use std::sync::{Mutex, OnceLock};
use std::thread::ThreadId;

const PAIRS: usize = 64;
const QUEUE_LIMIT: usize = 256;
const SAMPLE_LIMIT: usize = 512;
const MAX_DELAY_FRAMES: i32 = 12;
const INIT: i32 = 0;
const BEGIN: i32 = 1;
const END: i32 = 2;
const POLL: i32 = 3;
const CLEANUP: i32 = 4;

#[cfg(target_os = "macos")]
mod gl {
    use super::*;
    pub const TIMESTAMP: u32 = 0x8e28;
    pub const COUNTER_BITS: u32 = 0x8864;
    pub const RESULT: u32 = 0x8866;
    pub const AVAILABLE: u32 = 0x8867;
    pub const RENDERER: u32 = 0x1f01;
    pub const VERSION: u32 = 0x1f02;
    #[link(name = "OpenGL", kind = "framework")]
    extern "C" {
        pub fn CGLGetCurrentContext() -> *mut c_void;
        pub fn glGetString(name: u32) -> *const u8;
        pub fn glGetQueryiv(target: u32, pname: u32, value: *mut i32);
        pub fn glGenQueries(count: i32, queries: *mut u32);
        pub fn glDeleteQueries(count: i32, queries: *const u32);
        pub fn glQueryCounter(query: u32, target: u32);
        pub fn glGetQueryObjectiv(query: u32, pname: u32, value: *mut i32);
        pub fn glGetQueryObjectui64v(query: u32, pname: u32, value: *mut u64);
        pub fn glGetError() -> u32;
    }
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Sample {
    sequence: u64,
    camera_id: i32,
    frame: i32,
    status: String,
    reason: String,
    gpu_span_nanoseconds: Option<u64>,
    readback_delay_frames: i32,
}

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct Receipt {
    schema: &'static str,
    run_id: String,
    process_id: u32,
    status: String,
    initialization_status: String,
    admission_reason: String,
    renderer: String,
    version: String,
    counter_bits: i32,
    scope: &'static str,
    synchronization: &'static str,
    capacity: usize,
    max_delay_frames: i32,
    queued: usize,
    pending: usize,
    begin_count: u64,
    end_count: u64,
    poll_count: u64,
    valid_count: u64,
    invalid_count: u64,
    dropped_count: u64,
    unready_count: u64,
    retained_samples_overwritten: u64,
    cleanup_aborted: u64,
    queries_deleted: usize,
    outstanding_queries: usize,
    cleanup_pending: bool,
    queued_discarded_at_cleanup: u64,
    sample_capacity: usize,
    last_callback_frame: i32,
    samples: VecDeque<Sample>,
}

#[derive(Clone, Copy)]
struct Command {
    kind: i32,
    camera: i32,
    frame: i32,
}

struct Slot {
    queries: [u32; 2],
    pair: Option<(i32, i32)>,
    ended: bool,
}

struct State {
    receipt: Receipt,
    commands: HashMap<i32, Command>,
    token: i32,
    slots: Vec<Slot>,
    thread: Option<ThreadId>,
    context: usize,
    sequence: u64,
}

impl Default for State {
    fn default() -> Self {
        Self {
            receipt: Receipt {
                schema: "continuum-render-timing/v1",
                run_id: String::new(),
                process_id: std::process::id(),
                status: "idle".into(),
                initialization_status: "not-attempted".into(),
                admission_reason: String::new(),
                renderer: String::new(),
                version: String::new(),
                counter_bits: 0,
                scope: "Tagged forward-camera GPU timestamp spans, including queue bubbles; not exclusive GPU utilization. BeforeForwardOpaque excludes earlier shadows/depth and other cameras. No full-frame GPU duration claim.",
                synchronization: "Copied command tags; bounded mutex snapshots. Render-thread-only GL operations; availability queried before result reads; no glFinish, query wait or caller-data pointer retention.",
                capacity: PAIRS,
                max_delay_frames: MAX_DELAY_FRAMES,
                queued: 0,
                pending: 0,
                begin_count: 0,
                end_count: 0,
                poll_count: 0,
                valid_count: 0,
                invalid_count: 0,
                dropped_count: 0,
                unready_count: 0,
                retained_samples_overwritten: 0,
                cleanup_aborted: 0,
                queries_deleted: 0,
                outstanding_queries: 0,
                cleanup_pending: false,
                queued_discarded_at_cleanup: 0,
                sample_capacity: SAMPLE_LIMIT,
                last_callback_frame: -1,
                samples: VecDeque::new(),
            },
            commands: HashMap::new(),
            token: 0,
            slots: Vec::new(),
            thread: None,
            context: 0,
            sequence: 0,
        }
    }
}

static STATE: OnceLock<Mutex<State>> = OnceLock::new();
fn state() -> std::sync::MutexGuard<'static, State> {
    STATE
        .get_or_init(|| Mutex::new(State::default()))
        .lock()
        .unwrap_or_else(|e| e.into_inner())
}

impl State {
    fn sample(&mut self, command: Command, status: &str, reason: &str, duration: Option<u64>) {
        self.sequence += 1;
        if self.receipt.samples.len() == SAMPLE_LIMIT {
            self.receipt.samples.pop_front();
            self.receipt.retained_samples_overwritten += 1;
        }
        self.receipt.samples.push_back(Sample {
            sequence: self.sequence,
            camera_id: command.camera,
            frame: command.frame,
            status: status.into(),
            reason: reason.into(),
            gpu_span_nanoseconds: duration,
            readback_delay_frames: self
                .receipt
                .last_callback_frame
                .saturating_sub(command.frame)
                .max(0),
        });
        match status {
            "valid" => self.receipt.valid_count += 1,
            "dropped" => self.receipt.dropped_count += 1,
            _ => self.receipt.invalid_count += 1,
        }
    }
    fn reject(&mut self, reason: &str) {
        self.receipt.status = "rejected".into();
        self.receipt.admission_reason = reason.into();
    }
    fn counts(&mut self) {
        self.receipt.queued = self.commands.len();
        self.receipt.pending = self.slots.iter().filter(|s| s.pair.is_some()).count();
        self.receipt.outstanding_queries = self.slots.len() * 2;
        self.receipt.cleanup_pending = self.commands.values().any(|c| c.kind == CLEANUP);
    }
}

#[cfg(target_os = "macos")]
impl State {
    unsafe fn initialize(&mut self) {
        if self.receipt.status == "admitted" {
            if !self.context_matches() {
                self.reject("render-thread-or-context-mismatch");
            }
            return;
        }
        if self.receipt.status == "rejected" {
            return;
        }
        if self.receipt.status != "configured" {
            self.reject("initialization-order-invalid");
            return;
        }
        let context = gl::CGLGetCurrentContext() as usize;
        if context == 0 {
            self.reject("no-current-cgl-context");
            return;
        }
        self.thread = Some(std::thread::current().id());
        self.context = context;
        let renderer = gl::glGetString(gl::RENDERER);
        let version = gl::glGetString(gl::VERSION);
        if renderer.is_null() || version.is_null() {
            self.reject("renderer-or-version-unavailable");
            return;
        }
        self.receipt.renderer = CStr::from_ptr(renderer.cast())
            .to_string_lossy()
            .into_owned();
        self.receipt.version = CStr::from_ptr(version.cast())
            .to_string_lossy()
            .into_owned();
        if gl::glGetError() != 0 {
            self.reject("preexisting-gl-error-at-admission");
            return;
        }
        gl::glGetQueryiv(
            gl::TIMESTAMP,
            gl::COUNTER_BITS,
            &mut self.receipt.counter_bits,
        );
        if gl::glGetError() != 0 || !(1..=64).contains(&self.receipt.counter_bits) {
            self.reject("timestamp-counter-unavailable");
            return;
        }
        let mut names = [0; PAIRS * 2];
        gl::glGenQueries(names.len() as i32, names.as_mut_ptr());
        if gl::glGetError() != 0 || names.contains(&0) {
            let allocated: Vec<_> = names.into_iter().filter(|&n| n != 0).collect();
            gl::glDeleteQueries(allocated.len() as i32, allocated.as_ptr());
            self.receipt.queries_deleted += allocated.len();
            self.reject("query-allocation-failed");
            return;
        }
        self.slots = names
            .chunks_exact(2)
            .map(|n| Slot {
                queries: [n[0], n[1]],
                pair: None,
                ended: false,
            })
            .collect();
        self.receipt.status = "admitted".into();
        self.receipt.admission_reason = "current-cgl-renderer-and-nonzero-timestamp-counter".into();
    }
    unsafe fn context_matches(&self) -> bool {
        self.thread == Some(std::thread::current().id())
            && self.context != 0
            && gl::CGLGetCurrentContext() as usize == self.context
    }
    unsafe fn poll(&mut self) {
        self.receipt.poll_count += 1;
        for index in 0..self.slots.len() {
            let Some((camera, frame)) = self.slots[index].pair else {
                continue;
            };
            let command = Command {
                kind: POLL,
                camera,
                frame,
            };
            let age = self.receipt.last_callback_frame.saturating_sub(frame);
            if !self.slots[index].ended {
                if age > MAX_DELAY_FRAMES {
                    self.slots[index].pair = None;
                    self.sample(command, "invalid", "end-event-missing", None);
                }
                continue;
            }
            let mut available = [0; 2];
            for (q, result) in self.slots[index].queries.iter().zip(available.iter_mut()) {
                gl::glGetQueryObjectiv(*q, gl::AVAILABLE, result);
            }
            if gl::glGetError() != 0 {
                self.slots[index].pair = None;
                self.sample(command, "invalid", "query-availability-gl-error", None);
                continue;
            }
            if available.contains(&0) {
                self.receipt.unready_count += 1;
                if age > MAX_DELAY_FRAMES {
                    self.slots[index].pair = None;
                    self.sample(command, "dropped", "readback-delay-limit", None);
                }
                continue;
            }
            let mut values = [0; 2];
            for (q, result) in self.slots[index].queries.iter().zip(values.iter_mut()) {
                gl::glGetQueryObjectui64v(*q, gl::RESULT, result);
            }
            self.slots[index].pair = None;
            if gl::glGetError() != 0 {
                self.sample(command, "invalid", "query-result-gl-error", None);
            } else {
                let duration =
                    timestamp_delta(values[0], values[1], self.receipt.counter_bits as u32);
                self.sample(
                    command,
                    if duration.is_some() {
                        "valid"
                    } else {
                        "invalid"
                    },
                    if duration.is_some() {
                        "timestamp-span"
                    } else {
                        "zero-timestamp-span"
                    },
                    duration,
                );
            }
        }
    }
    unsafe fn callback(&mut self, command: Command) {
        self.receipt.last_callback_frame = self.receipt.last_callback_frame.max(command.frame);
        if command.kind == INIT {
            self.initialize();
            self.receipt.initialization_status = self.receipt.status.clone();
            return;
        }
        if command.kind == CLEANUP && self.slots.is_empty() {
            self.receipt.queued_discarded_at_cleanup += self.commands.len() as u64;
            self.commands.clear();
            self.receipt.status = "cleaned".into();
            return;
        }
        if !self.context_matches() {
            self.sample(
                command,
                "invalid",
                "render-thread-or-context-mismatch",
                None,
            );
            self.reject("render-thread-or-context-mismatch");
            return;
        }
        if command.kind == CLEANUP {
            self.poll();
            let abandoned: Vec<_> = self.slots.iter().filter_map(|s| s.pair).collect();
            for (camera, frame) in abandoned {
                self.receipt.cleanup_aborted += 1;
                self.sample(
                    Command {
                        kind: CLEANUP,
                        camera,
                        frame,
                    },
                    "invalid",
                    "cleanup-before-readback",
                    None,
                );
            }
            let names: Vec<_> = self.slots.iter().flat_map(|s| s.queries).collect();
            gl::glDeleteQueries(names.len() as i32, names.as_ptr());
            self.receipt.queries_deleted += names.len();
            self.slots.clear();
            self.receipt.queued_discarded_at_cleanup += self.commands.len() as u64;
            self.commands.clear();
            if gl::glGetError() == 0 {
                self.receipt.status = "cleaned".into();
            } else {
                self.reject("query-deletion-gl-error");
            }
            return;
        }
        if self.receipt.status != "admitted" {
            self.sample(command, "invalid", "not-admitted", None);
            return;
        }
        match command.kind {
            POLL => self.poll(),
            BEGIN => {
                self.receipt.begin_count += 1;
                if self
                    .slots
                    .iter()
                    .any(|s| s.pair == Some((command.camera, command.frame)))
                {
                    self.sample(command, "invalid", "duplicate-begin", None);
                    return;
                }
                let Some(slot) = self.slots.iter_mut().find(|s| s.pair.is_none()) else {
                    self.sample(command, "dropped", "pending-ring-full", None);
                    return;
                };
                gl::glQueryCounter(slot.queries[0], gl::TIMESTAMP);
                if gl::glGetError() != 0 {
                    self.sample(command, "invalid", "begin-timestamp-gl-error", None);
                    return;
                }
                slot.pair = Some((command.camera, command.frame));
                slot.ended = false;
            }
            END => {
                self.receipt.end_count += 1;
                let Some(slot) = self
                    .slots
                    .iter_mut()
                    .find(|s| s.pair == Some((command.camera, command.frame)))
                else {
                    self.sample(command, "invalid", "end-without-begin", None);
                    return;
                };
                if slot.ended {
                    self.sample(command, "invalid", "duplicate-end", None);
                    return;
                }
                gl::glQueryCounter(slot.queries[1], gl::TIMESTAMP);
                if gl::glGetError() != 0 {
                    slot.pair = None;
                    self.sample(command, "invalid", "end-timestamp-gl-error", None);
                    return;
                }
                slot.ended = true;
            }
            _ => self.sample(command, "invalid", "event-kind-invalid", None),
        }
    }
}

fn timestamp_delta(start: u64, end: u64, bits: u32) -> Option<u64> {
    let mask = if bits == 64 {
        u64::MAX
    } else {
        (1u64 << bits) - 1
    };
    let delta = end.wrapping_sub(start) & mask;
    (delta != 0).then_some(delta)
}

/// The caller supplies valid UTF-8 memory for `length` bytes only for this call.
#[no_mangle]
pub unsafe extern "C" fn crt_begin_run(run_id: *const u8, length: i32) -> i32 {
    if run_id.is_null() || !(1..=128).contains(&length) {
        return 1;
    }
    let Ok(run_id) = std::str::from_utf8(std::slice::from_raw_parts(run_id, length as usize))
    else {
        return 2;
    };
    if run_id.contains('\0') {
        return 2;
    }
    let mut state = state();
    if !state.slots.is_empty()
        || !state.commands.is_empty()
        || !matches!(
            state.receipt.status.as_str(),
            "idle" | "cleaned" | "rejected"
        )
    {
        return 3;
    }
    let last_token = state.token;
    *state = State::default();
    state.token = last_token;
    state.receipt.run_id = run_id.to_owned();
    state.receipt.status = "configured".into();
    0
}

#[no_mangle]
pub extern "C" fn crt_enqueue(kind: i32, camera_id: i32, frame: i32) -> i32 {
    let mut state = state();
    let command = Command {
        kind,
        camera: camera_id,
        frame,
    };
    if !(INIT..=CLEANUP).contains(&kind)
        || frame < 0
        || state.receipt.status == "idle"
        || state.receipt.status == "cleaned"
    {
        state.sample(command, "invalid", "command-admission-invalid", None);
        return 0;
    }
    if state.commands.len() == QUEUE_LIMIT || state.token == i32::MAX {
        state.sample(command, "dropped", "command-queue-or-token-limit", None);
        return 0;
    }
    state.token += 1;
    let token = state.token;
    state.commands.insert(token, command);
    token
}

unsafe extern "C" fn render_event(token: i32) {
    let mut state = state();
    if let Some(command) = state.commands.remove(&token) {
        #[cfg(target_os = "macos")]
        state.callback(command);
        #[cfg(not(target_os = "macos"))]
        {
            state.reject("macos-opengl-required");
            state.sample(command, "invalid", "macos-opengl-required", None);
        }
    } else {
        let frame = state.receipt.last_callback_frame;
        state.sample(
            Command {
                kind: -1,
                camera: 0,
                frame,
            },
            "invalid",
            "unknown-or-replayed-event-token",
            None,
        );
    }
}

#[no_mangle]
pub extern "C" fn crt_get_render_event_func() -> *const c_void {
    render_event as *const c_void
}

/// Returns required UTF-8 bytes including NUL. Copy occurs only if capacity fits.
/// Caller owns a writable buffer of `capacity` bytes; null/zero is a size query.
#[no_mangle]
pub unsafe extern "C" fn crt_json(buffer: *mut u8, capacity: i32) -> i32 {
    let receipt = {
        let mut state = state();
        state.counts();
        state.receipt.clone()
    };
    let Ok(json) = serde_json::to_vec(&receipt) else {
        return -1;
    };
    let required = json.len() + 1;
    if !buffer.is_null() && capacity > 0 && capacity as usize >= required {
        std::ptr::copy_nonoverlapping(json.as_ptr(), buffer, json.len());
        *buffer.add(json.len()) = 0;
    }
    required as i32
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn timestamps_preserve_wrap_and_reject_zero() {
        assert_eq!(timestamp_delta(250, 4, 8), Some(10));
        assert_eq!(timestamp_delta(u64::MAX - 2, 2, 64), Some(5));
        assert_eq!(timestamp_delta(9, 9, 64), None);
    }
    #[test]
    fn journal_has_bounded_retention_and_explicit_loss() {
        let mut state = State::default();
        for frame in 0..SAMPLE_LIMIT as i32 + 20 {
            state.sample(
                Command {
                    kind: BEGIN,
                    camera: -7,
                    frame,
                },
                "dropped",
                "test",
                None,
            );
        }
        assert_eq!(state.receipt.samples.len(), SAMPLE_LIMIT);
        assert_eq!(state.receipt.retained_samples_overwritten, 20);
        assert_eq!(state.receipt.dropped_count, SAMPLE_LIMIT as u64 + 20);
    }
    #[cfg(target_os = "macos")]
    #[test]
    fn missing_render_context_is_negative_admission_without_queries() {
        let mut state = State::default();
        state.receipt.status = "configured".into();
        unsafe {
            assert!(gl::CGLGetCurrentContext().is_null());
            state.initialize();
        }
        assert_eq!(state.receipt.status, "rejected");
        assert_eq!(state.receipt.admission_reason, "no-current-cgl-context");
        assert!(state.slots.is_empty());
    }
}
