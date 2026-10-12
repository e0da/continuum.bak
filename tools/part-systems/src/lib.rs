use continuum_research_lab::{hash, read, Result};
use hecs::{Entity, World};
use serde_json::{json, Value};
use std::{hint::black_box, path::Path, time::Instant};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Key {
    ordinal: usize,
    slot: u64,
    part: u64,
    body: i64,
}

#[derive(Clone, Copy, Debug)]
struct Input {
    x: [f32; 18],
    d: [f64; 4],
    flags: u64,
    initial_lift: f32,
}

impl Input {
    fn same_bits(&self, other: &Self) -> bool {
        self.flags == other.flags
            && self.initial_lift.to_bits() == other.initial_lift.to_bits()
            && self
                .x
                .iter()
                .zip(other.x)
                .all(|(a, b)| a.to_bits() == b.to_bits())
            && self
                .d
                .iter()
                .zip(other.d)
                .all(|(a, b)| a.to_bits() == b.to_bits())
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
struct Command {
    kind: u8,
    mode: u64,
    force: [f32; 3],
    point: [f32; 3],
    angular: f32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
struct Output {
    scalar: [f32; 7],
    pressure: f64,
    lift_written: bool,
    commands: [Command; 4],
    count: usize,
}

fn dot(a: &[f32], b: &[f32]) -> f32 {
    (a[0] * b[0] + a[1] * b[1]) + a[2] * b[2]
}

fn evaluate(input: &Input) -> Output {
    let x = &input.x;
    let square = dot(&x[..3], &x[..3]);
    let speed = (square as f64).sqrt() as f32;
    let direction = [x[0] / speed, x[1] / speed, x[2] / speed];
    let pressure = input.d[1] * (0.0005 * square as f64);
    let value = input.d[2] * input.d[3];
    let drag = if value.is_nan() || value == 0. {
        0.
    } else {
        (pressure * value * 1.) as f32 * x[14]
    };
    let mut out = Output {
        scalar: [
            square,
            speed,
            direction[0],
            direction[1],
            direction[2],
            drag,
            input.initial_lift,
        ],
        pressure,
        ..Output::default()
    };
    let mode = if input.flags & 64 != 0 { 5 } else { 0 };
    let mut push = |command| {
        out.commands[out.count] = command;
        out.count += 1;
    };
    if input.flags & 1 != 0 && input.flags & 32 != 0 {
        push(Command {
            kind: 0,
            angular: 0.,
            ..Command::default()
        });
        push(Command {
            kind: 0,
            angular: x[12] * (pressure * 0.009869232667160128).max(0.) as f32 * x[13],
            ..Command::default()
        });
    }
    if !value.is_nan() && value != 0. {
        push(Command {
            kind: 1,
            mode,
            force: [
                -direction[0] * drag,
                -direction[1] * drag,
                -direction[2] * drag,
            ],
            point: [x[3], x[4], x[5]],
            ..Command::default()
        });
    }
    if input.flags & 128 != 0 {
        out.lift_written = true;
        out.scalar[6] = pressure as f32 * x[15] * x[16] * x[17];
        if out.scalar[6] != 0. && input.flags & 256 != 0 {
            let normal = [-direction[0], -direction[1], -direction[2]];
            let norm = dot(&normal, &normal);
            let product = dot(&x[9..12], &normal);
            let mut projected = [x[9], x[10], x[11]];
            if norm >= f32::from_bits(1) {
                for i in 0..3 {
                    projected[i] -= normal[i] * product / norm;
                }
            }
            out.commands[out.count] = Command {
                kind: 2,
                mode,
                force: projected,
                point: [x[6], x[7], x[8]],
                ..Command::default()
            };
            out.count += 1;
        }
    }
    out
}

trait Owner {
    fn refresh(&mut self, changes: &[(usize, Input)]);
    fn compute(&mut self);
    fn gather(&mut self, output: &mut [Output]);
}

struct Dense {
    input: Vec<Input>,
    output: Vec<Output>,
}
impl Dense {
    fn new(input: &[Input]) -> Self {
        Self {
            input: input.to_vec(),
            output: vec![Output::default(); input.len()],
        }
    }
}
impl Owner for Dense {
    fn refresh(&mut self, changes: &[(usize, Input)]) {
        for &(i, x) in changes {
            self.input[i] = x;
        }
    }
    fn compute(&mut self) {
        for (x, y) in self.input.iter().zip(&mut self.output) {
            *y = evaluate(x);
        }
    }
    fn gather(&mut self, output: &mut [Output]) {
        output.copy_from_slice(&self.output);
    }
}

struct Ecs {
    world: World,
    entities: Vec<Entity>,
}
impl Ecs {
    fn new(keys: &[Key], input: &[Input]) -> Self {
        let mut world = World::new();
        world.reserve::<(Key, Input, Output)>(keys.len() as u32);
        let mut entities = Vec::with_capacity(keys.len());
        // Publication follows the captured ordinal, never the ECS query order.
        for i in (0..keys.len()).rev() {
            entities.push(world.spawn((keys[i], input[i], Output::default())));
        }
        entities.reverse();
        Self { world, entities }
    }
}
impl Owner for Ecs {
    fn refresh(&mut self, changes: &[(usize, Input)]) {
        for &(i, x) in changes {
            *self
                .world
                .query_one_mut::<&mut Input>(self.entities[i])
                .expect("retained entity") = x;
        }
    }
    fn compute(&mut self) {
        for (_, (x, y)) in self.world.query_mut::<(&Input, &mut Output)>() {
            *y = evaluate(x);
        }
    }
    fn gather(&mut self, output: &mut [Output]) {
        for (_, (key, value)) in self.world.query_mut::<(&Key, &Output)>() {
            output[key.ordinal] = *value;
        }
    }
}

fn word(v: &Value) -> Result<u64> {
    v.as_u64()
        .ok_or_else(|| "binary32/identity word absent".into())
}
fn f(v: &Value) -> Result<f32> {
    Ok(f32::from_bits(
        u32::try_from(word(v)?).map_err(|_| "binary32 overflow")?,
    ))
}
fn d(v: &Value) -> Result<f64> {
    Ok(f64::from_bits(
        v.as_i64().ok_or("binary64 word absent")? as u64
    ))
}
fn array<const N: usize, T: Copy>(
    values: &Value,
    decode: impl Fn(&Value) -> Result<T>,
    zero: T,
) -> Result<[T; N]> {
    let values = values
        .as_array()
        .filter(|a| a.len() == N)
        .ok_or("numeric field length differs")?;
    let mut result = [zero; N];
    for (i, value) in values.iter().enumerate() {
        result[i] = decode(value)?;
    }
    Ok(result)
}
fn near(a: f64, b: f64) -> Result<()> {
    let tolerance = (1e-6 + 2e-5 * b.abs()).min(2e-5 * b.abs().max(1.));
    if a.is_finite() && b.is_finite() && (a - b).abs() <= tolerance {
        Ok(())
    } else {
        Err("original dry-tail numerical bound exceeded".into())
    }
}

fn validate(keys: &[Key], actual: &[Output], root: &Value) -> Result<usize> {
    let rows = root["rows"].as_array().ok_or("rows absent")?;
    let commands = root["commands"].as_array().ok_or("commands absent")?;
    let mut tails = vec![Vec::new(); keys.len()];
    for command in commands {
        if command["phase"] != "queued-integration" {
            let ordinal =
                usize::try_from(word(&command["ordinal"])?).map_err(|_| "ordinal overflow")?;
            tails
                .get_mut(ordinal)
                .ok_or("foreign command ordinal")?
                .push(command);
        }
    }
    let mut count = 0;
    for ((key, out), row) in keys.iter().zip(actual).zip(rows) {
        let expected = array::<7, _>(&row["outputFloatBits"], f, 0.)?;
        for (a, b) in out.scalar.iter().zip(expected) {
            near(*a as f64, b as f64)?;
        }
        near(out.pressure, d(&row["dynamicPressureBits"])?)?;
        if row["bodyLiftScalarWritten"] != out.lift_written || tails[key.ordinal].len() != out.count
        {
            return Err("conditional write/command count differs".into());
        }
        if !out.lift_written && out.scalar[6].to_bits() != expected[6].to_bits() {
            return Err("unwritten lift bits differ".into());
        }
        for (command, original) in out.commands[..out.count].iter().zip(&tails[key.ordinal]) {
            let phase = ["aero-angular", "aero-drag", "aero-lift"][command.kind as usize];
            let kind = if command.kind == 0 {
                "set_angularDrag"
            } else {
                "AddForceAtPosition"
            };
            if original["phase"] != phase
                || original["kind"] != kind
                || original["partId"] != key.part
                || original["recipientBodyId"] != key.body
                || original["forceMode"] != command.mode
            {
                return Err("ordered original command identity/kind/mode differs".into());
            }
            if command.kind == 0 {
                near(
                    command.angular as f64,
                    f(&original["angularDragBits"])? as f64,
                )?;
            } else {
                let force = array::<3, _>(&original["forceBits"], f, 0.)?;
                let point = array::<3, _>(&original["pointBits"], f, 0.)?;
                for i in 0..3 {
                    near(command.force[i] as f64, force[i] as f64)?;
                    if command.point[i].to_bits() != point[i].to_bits() {
                        return Err("original application-point bits differ".into());
                    }
                }
            }
            count += 1;
        }
    }
    Ok(count)
}

fn timed(owner: &mut impl Owner, changes: &[(usize, Input)], output: &mut [Output]) -> Value {
    let start = Instant::now();
    owner.refresh(black_box(changes));
    let refreshed = Instant::now();
    owner.compute();
    let computed = Instant::now();
    owner.gather(black_box(output));
    let end = Instant::now();
    json!({"refreshNs":refreshed.duration_since(start).as_nanos() as u64,
        "computeNs":computed.duration_since(refreshed).as_nanos() as u64,
        "orderedGatherNs":end.duration_since(computed).as_nanos() as u64,
        "totalNs":end.duration_since(start).as_nanos() as u64,"changedRows":changes.len()})
}

pub fn compare(recording: &Path, probe_path: &Path) -> Result<Value> {
    let preparation = Instant::now();
    let recording_hash = hash(recording)?;
    let probe_hash = hash(probe_path)?;
    let probe: Value = read(probe_path)?;
    let pid = u32::try_from(word(&probe["orderedBodyAeroCapture"]["processId"])?)
        .map_err(|_| "process ID overflow")?;
    let independent = continuum_research_lab::ordered_body_aero::summarize(recording, &probe, pid)?;
    if independent["unsupportedRows"] != 0 {
        return Err("comparison requires complete supported dry rows".into());
    }
    if independent["recordingSha256"] != recording_hash {
        return Err("recording changed during admission".into());
    }
    let tape: Value = read(recording)?;
    let roots = tape["roots"].as_array().ok_or("roots absent")?;
    let mut keys = Vec::new();
    let mut frames = Vec::new();
    for (sequence, root) in roots.iter().enumerate() {
        let mut input = Vec::new();
        for (ordinal, row) in root["rows"]
            .as_array()
            .ok_or("rows absent")?
            .iter()
            .enumerate()
        {
            let key = Key {
                ordinal,
                slot: word(&row["partSlot"])?,
                part: word(&row["partId"])?,
                body: row["recipientBodyId"].as_i64().ok_or("body absent")?,
            };
            if sequence == 0 {
                keys.push(key);
            } else if keys[ordinal] != key {
                return Err("retained membership/body changed".into());
            }
            input.push(Input {
                x: array(&row["inputFloatBits"], f, 0.)?,
                d: array(&row["inputDoubleBits"], d, 0.)?,
                flags: word(&row["inputFlags"])?,
                initial_lift: f(&row["extraFloatBits"][0])?,
            });
        }
        frames.push(input);
    }
    let changes: Vec<_> = frames[1]
        .iter()
        .enumerate()
        .filter(|(i, x)| !x.same_bits(&frames[0][*i]))
        .map(|(i, x)| (i, *x))
        .collect();
    let decoded_ns = preparation.elapsed().as_nanos() as u64;
    let start = Instant::now();
    let mut dense = Dense::new(&frames[0]);
    let dense_prepare_ns = start.elapsed().as_nanos() as u64;
    let start = Instant::now();
    let mut ecs = Ecs::new(&keys, &frames[0]);
    let ecs_prepare_ns = start.elapsed().as_nanos() as u64;
    let mut dense_output = vec![Output::default(); keys.len()];
    let mut ecs_output = vec![Output::default(); keys.len()];
    dense.compute();
    dense.gather(&mut dense_output);
    ecs.compute();
    ecs.gather(&mut ecs_output);
    validate(&keys, &dense_output, &roots[0])?;
    validate(&keys, &ecs_output, &roots[0])?;
    let mut samples = Vec::new();
    let mut verified = 0;
    for sequence in 0..2 {
        let update = if sequence == 0 {
            &[][..]
        } else {
            changes.as_slice()
        };
        let (dense_sample, ecs_sample) = if sequence == 0 {
            (
                timed(&mut dense, update, &mut dense_output),
                timed(&mut ecs, update, &mut ecs_output),
            )
        } else {
            let e = timed(&mut ecs, update, &mut ecs_output);
            (timed(&mut dense, update, &mut dense_output), e)
        };
        verified += validate(&keys, &dense_output, &roots[sequence])?;
        validate(&keys, &ecs_output, &roots[sequence])?;
        if dense_output != ecs_output {
            return Err("backend representation outputs differ".into());
        }
        samples.push(json!({"root":sequence,"order":if sequence==0 {"dense-ecs"} else {"ecs-dense"},"dense":dense_sample,"hecs":ecs_sample}));
    }
    if hash(recording)? != recording_hash || hash(probe_path)? != probe_hash {
        return Err("recording/probe changed during comparison".into());
    }
    Ok(
        json!({"schema":"continuum-part-systems-comparison/v1","status":"passed","recordingSha256":recording_hash,"probeSha256":probe_hash,
        "runId":tape["runId"],"topologySha256":tape["topologySha256"],"providerEpoch":tape["providerEpoch"],"rowsPerRoot":keys.len(),"roots":2,"verifiedTailCommandsPerBackend":verified,
        "architecture":std::env::consts::ARCH,"os":std::env::consts::OS,"ecs":"hecs-0.10.5","denseStorage":"retained typed input/output arrays; AoS inside each input row",
        "coldPreparationNs":{"validateDecodeAndDelta":decoded_ns,"denseOwner":dense_prepare_ns,"ecsOwner":ecs_prepare_ns},"warmup":"one first-root compute/gather/validation per backend",
        "samples":samples,"allocationCounter":{"available":false,"scope":"owner storage retained; allocator calls not measured"},
        "scope":"Offline supported dry-aero arithmetic/storage trial; prepared dirty lists, refresh, compute and ordered output materialization. Two observations per backend, no confidence interval/FPS claim. JSON decode, independent original-tape checks and owner construction are cold preparation; validation is outside timed samples. No current live acquisition, provider/curve/queued-force replacement, scheduler comparison, IPC, Unity publication or rigid-body takeover."}),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    fn input() -> Input {
        let mut x = [0.; 18];
        x[0] = 10.;
        x[4] = 2.;
        x[10] = 3.;
        x[14] = 1.;
        x[15] = 1.;
        x[16] = 1.;
        x[17] = 1.;
        Input {
            x,
            d: [0., 1., 2., 1.],
            flags: 1 | 8 | 128 | 256 | 512,
            initial_lift: 7.,
        }
    }
    #[test]
    fn analytic_drag_and_projected_lift() {
        let out = evaluate(&input());
        assert_eq!(out.scalar, [100., 10., 1., 0., 0., 0.1, 0.05]);
        assert_eq!(out.pressure, 0.05);
        assert_eq!(out.count, 2);
        assert_eq!(out.commands[0].force, [-0.1, -0., -0.]);
        assert_eq!(out.commands[0].point, [0., 2., 0.]);
        assert_eq!(out.commands[1].force, [0., 3., 0.]);
    }
    #[test]
    fn retained_ecs_ordinals_refresh_and_unwritten_lift() {
        let keys: Vec<_> = (0..3)
            .map(|ordinal| Key {
                ordinal,
                slot: ordinal as u64,
                part: ordinal as u64 + 1,
                body: 9,
            })
            .collect();
        let inputs = vec![input(); 3];
        let mut dense = Dense::new(&inputs);
        let mut ecs = Ecs::new(&keys, &inputs);
        let mut a = vec![Output::default(); 3];
        let mut b = a.clone();
        let mut changed = input();
        changed.x[0] = 20.;
        changed.flags &= !128;
        let delta = [(1, changed)];
        dense.refresh(&delta);
        ecs.refresh(&delta);
        dense.compute();
        ecs.compute();
        dense.gather(&mut a);
        ecs.gather(&mut b);
        assert_eq!(a, b);
        assert_eq!(a[1].scalar[0], 400.);
        assert_eq!(a[0].scalar[0], 100.);
        assert_eq!(a[1].scalar[6], 7.);
        assert!(!a[1].lift_written);
        let mut signed = changed;
        signed.x[1] = -0.;
        assert!(!signed.same_bits(&changed));
    }
}
