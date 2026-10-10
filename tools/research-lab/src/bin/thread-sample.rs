use serde_json::{json, Value};
use std::{collections::BTreeMap, env, fs};

#[derive(Debug)]
struct Node {
    depth: usize,
    count: u64,
    children: u64,
    symbol: String,
    category: &'static str,
}

fn category(symbol: &str, inherited: &'static str) -> &'static str {
    if [
        "semaphore_wait",
        "__psynch_cvwait",
        "__ulock_wait",
        "mach_msg",
        "kevent",
        "pthread_cond_wait",
    ]
    .iter()
    .any(|s| symbol.contains(s))
    {
        "blocked-or-event-wait"
    } else if [
        "AppleMetalOpenGLRenderer",
        "GLEngine",
        "(in OpenGL)",
        "(in Metal)",
        "(in IOGPU)",
    ]
    .iter()
    .any(|s| symbol.contains(s))
    {
        "graphics-driver-submission"
    } else if symbol.contains("(in UnityPlayer.dylib)") {
        "unity-native-unsymbolicated"
    } else if symbol.contains("(in libmono") {
        "mono-runtime"
    } else {
        inherited
    }
}

fn finish(
    stack: &mut Vec<Node>,
    counts: &mut BTreeMap<&'static str, u64>,
    leaves: &mut Vec<Value>,
    invalid: &mut usize,
) {
    let node = stack.pop().unwrap();
    if node.children > node.count {
        *invalid += 1;
    }
    let exclusive = node.count.saturating_sub(node.children);
    if exclusive > 0 {
        *counts.entry(node.category).or_default() += exclusive;
        leaves.push(json!({"samples":exclusive,"category":node.category,"symbol":node.symbol}));
    }
    if let Some(parent) = stack.last_mut() {
        parent.children += node.count;
    }
}

fn analyze(text: &str) -> Result<Value, String> {
    let mut active = false;
    let mut complete = false;
    let mut stack: Vec<Node> = Vec::new();
    let mut threads = Vec::new();
    let mut counts = BTreeMap::new();
    let mut leaves = Vec::new();
    let mut name = String::new();
    let mut total = 0;
    let mut invalid = 0;
    let flush = |stack: &mut Vec<Node>,
                 counts: &mut BTreeMap<&'static str, u64>,
                 leaves: &mut Vec<Value>,
                 threads: &mut Vec<Value>,
                 name: &str,
                 total: u64,
                 invalid: &mut usize| {
        while !stack.is_empty() {
            finish(stack, counts, leaves, invalid);
        }
        if !name.is_empty() {
            leaves.sort_by(|a, b| b["samples"].as_u64().cmp(&a["samples"].as_u64()));
            leaves.truncate(12);
            threads.push(json!({"thread":name,"samples":total,"exclusiveCategories":counts,"largestExclusivePaths":leaves}));
        }
        counts.clear();
        leaves.clear();
    };
    for line in text.lines() {
        if line == "Call graph:" {
            active = true;
            continue;
        }
        if !active {
            continue;
        }
        if line.starts_with("Total number in stack") {
            flush(
                &mut stack,
                &mut counts,
                &mut leaves,
                &mut threads,
                &name,
                total,
                &mut invalid,
            );
            complete = true;
            break;
        }
        if line.trim().is_empty() {
            continue;
        }
        let Some(depth) = line.find(|c: char| c.is_ascii_digit()) else {
            continue;
        };
        if !line[..depth]
            .chars()
            .all(|c| c.is_ascii_whitespace() || "+!|:".contains(c))
        {
            continue;
        }
        let rest = &line[depth..];
        let Some(space) = rest.find(' ') else {
            continue;
        };
        let Ok(count) = rest[..space].parse::<u64>() else {
            continue;
        };
        let symbol = rest[space + 1..].trim().to_owned();
        if depth == 4 && symbol.starts_with("Thread_") {
            flush(
                &mut stack,
                &mut counts,
                &mut leaves,
                &mut threads,
                &name,
                total,
                &mut invalid,
            );
            name = symbol;
            total = count;
            stack.push(Node {
                depth,
                count,
                children: 0,
                symbol: name.clone(),
                category: "unresolved-or-other",
            });
            continue;
        }
        if stack.is_empty() {
            return Err("Call tree lost its thread root".into());
        }
        while stack.last().is_some_and(|n| n.depth >= depth) {
            finish(&mut stack, &mut counts, &mut leaves, &mut invalid);
        }
        let inherited = stack
            .last()
            .map(|n| n.category)
            .unwrap_or("unresolved-or-other");
        stack.push(Node {
            depth,
            count,
            children: 0,
            category: category(&symbol, inherited),
            symbol,
        });
    }
    if threads.is_empty() || !complete {
        return Err("No complete supported macOS sample call tree found".into());
    }
    let balanced = threads.iter().all(|t| {
        t["exclusiveCategories"]
            .as_object()
            .unwrap()
            .values()
            .map(|v| v.as_u64().unwrap())
            .sum::<u64>()
            == t["samples"].as_u64().unwrap()
    });
    Ok(
        json!({"schema":"continuum-native-thread-sample-analysis/v1","validTreeAccounting":invalid==0 && balanced,"overfullNodes":invalid,
        "scope":"Call-tree sample occurrences, not CPU time, GPU execution, or a percent of whole-game work. Categories inherit nearest known ancestor for unsymbolicated descendants; blocked paths are separated. Threads sampled independently; do not sum their percentages. Raw stack text remains authority.","threads":threads}),
    )
}

fn main() -> Result<(), String> {
    let args: Vec<_> = env::args().collect();
    if args.len() != 2 {
        return Err("usage: thread-sample <native-flight-sample.txt>".into());
    }
    let input = fs::read_to_string(&args[1]).map_err(|e| e.to_string())?;
    let mut report = analyze(&input)?;
    report["inputSha256"] = json!(continuum_research_lab::hash(std::path::Path::new(
        &args[1]
    ))?);
    println!(
        "{}",
        serde_json::to_string_pretty(&report).map_err(|e| e.to_string())?
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn nested_driver_wait_is_not_double_counted() {
        let r=analyze("Call graph:\n    10 Thread_1: UnityGfxDeviceWorker\n    + 10 start (in UnityPlayer.dylib)\n    +   8 draw (in GLEngine)\n    +     3 semaphore_wait_trap (in libsystem_kernel.dylib)\n    +   2 ??? (in UnityPlayer.dylib)\nTotal number in stack:\n").unwrap();
        assert_eq!(r["validTreeAccounting"], true);
        assert_eq!(
            r["threads"][0]["exclusiveCategories"],
            json!({"graphics-driver-submission":5,"blocked-or-event-wait":3,"unity-native-unsymbolicated":2})
        );
        assert!(analyze("not a sample").is_err());
    }
}
