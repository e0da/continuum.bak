//! Private, read-only-source BC7 -> RGBA32 Unity 2019.4 derivative cooker.
#[allow(dead_code)]
#[path = "serialized-textures.rs"]
mod serialized;
use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use std::{
    collections::VecDeque,
    fs::{self, File, OpenOptions},
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    thread,
};
type Result<T> = std::result::Result<T, String>;
fn number(v: &Value, k: &str) -> Result<u64> {
    v[k].as_u64().ok_or_else(|| format!("missing positive {k}"))
}
fn text<'a>(v: &'a Value, k: &str) -> Result<&'a str> {
    v[k].as_str().ok_or_else(|| format!("missing text {k}"))
}
fn read_range(path: &Path, offset: u64, length: u64) -> Result<Vec<u8>> {
    let mut f = File::open(path).map_err(|e| e.to_string())?;
    if offset.checked_add(length).ok_or("range overflow")?
        > f.metadata().map_err(|e| e.to_string())?.len()
    {
        return Err("payload outside source".into());
    }
    let mut bytes = vec![0; usize::try_from(length).map_err(|_| "allocation overflow")?];
    f.seek(SeekFrom::Start(offset)).map_err(|e| e.to_string())?;
    f.read_exact(&mut bytes).map_err(|e| e.to_string())?;
    Ok(bytes)
}
fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn hash(path: &Path) -> Result<String> {
    continuum_bundle_audit::file_hash(path)
}
fn writable_private_copy(path: &Path) -> Result<()> {
    let mut permissions = fs::metadata(path).map_err(|e| e.to_string())?.permissions();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        permissions.set_mode(0o600);
    }
    #[cfg(not(unix))]
    permissions.set_readonly(false);
    fs::set_permissions(path, permissions).map_err(|e| e.to_string())
}
fn decode(bytes: &[u8], width: u64, height: u64, mips: u64) -> Result<Vec<u8>> {
    if width == 0 || height == 0 || width > 32768 || height > 32768 || mips == 0 || mips > 16 {
        return Err("texture dimensions outside bounded cook".into());
    }
    let (mut w, mut h) = (width as usize, height as usize);
    let (mut needed, mut rgba) = (0usize, 0usize);
    for _ in 0..mips {
        needed = needed
            .checked_add(w.div_ceil(4) * h.div_ceil(4) * 16)
            .ok_or("encoded layout overflow")?;
        rgba = rgba
            .checked_add(w * h * 4)
            .ok_or("decoded layout overflow")?;
        w = (w / 2).max(1);
        h = (h / 2).max(1)
    }
    if bytes.len() != needed || rgba > 512 * 1024 * 1024 {
        return Err(format!(
            "BC7 mip-chain layout differs: encoded={} expected={needed}",
            bytes.len()
        ));
    }
    let mut out = vec![0; rgba];
    let (mut source, mut target) = (0, 0);
    w = width as usize;
    h = height as usize;
    for _ in 0..mips {
        for by in 0..h.div_ceil(4) {
            for bx in 0..w.div_ceil(4) {
                let mut block = [0u8; 64];
                bcdec_rs::bc7(&bytes[source..source + 16], &mut block, 16);
                source += 16;
                for y in 0..4 {
                    for x in 0..4 {
                        if bx * 4 + x < w && by * 4 + y < h {
                            let dst = target + ((by * 4 + y) * w + bx * 4 + x) * 4;
                            let src = (y * 4 + x) * 4;
                            out[dst..dst + 4].copy_from_slice(&block[src..src + 4])
                        }
                    }
                }
            }
        }
        target += w * h * 4;
        w = (w / 2).max(1);
        h = (h / 2).max(1)
    }
    Ok(out)
}
struct Cooked {
    row: Value,
    payload: Vec<u8>,
    source_sha: String,
    decoded_sha: String,
}
fn cook(row: Value, assets: PathBuf) -> Result<Cooked> {
    let stream = &row["stream"];
    let (path, offset, size) = if stream.is_object() {
        let name = Path::new(text(stream, "path")?)
            .file_name()
            .ok_or("stream basename absent")?;
        (
            assets.parent().ok_or("source parent absent")?.join(name),
            number(stream, "offset")?,
            number(stream, "size")?,
        )
    } else {
        (
            assets,
            number(&row, "inline_payload_offset")?,
            number(&row, "inline_bytes")?,
        )
    };
    let encoded = read_range(&path, offset, size)?;
    let source_sha = digest(&encoded);
    let payload = decode(
        &encoded,
        number(&row, "width")?,
        number(&row, "height")?,
        number(&row, "mip_count")?,
    )?;
    let decoded_sha = digest(&payload);
    Ok(Cooked {
        row,
        payload,
        source_sha,
        decoded_sha,
    })
}
fn put32(file: &mut File, offset: u64, value: u64, allowed: &mut Vec<(u64, u64)>) -> Result<()> {
    let v = u32::try_from(value).map_err(|_| "Unity2019 uint32 field overflow")?;
    file.seek(SeekFrom::Start(offset))
        .map_err(|e| e.to_string())?;
    file.write_all(&v.to_le_bytes())
        .map_err(|e| e.to_string())?;
    allowed.push((offset, offset + 4));
    Ok(())
}
fn same_original_bytes(source: &Path, derived: &Path, allowed: &[(u64, u64)]) -> Result<u64> {
    let (mut a, mut b) = (
        File::open(source).map_err(|e| e.to_string())?,
        File::open(derived).map_err(|e| e.to_string())?,
    );
    let (mut aa, mut bb) = ([0u8; 65536], [0u8; 65536]);
    let mut pos = 0;
    loop {
        let n = a.read(&mut aa).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        b.read_exact(&mut bb[..n]).map_err(|e| e.to_string())?;
        for i in 0..n {
            let at = pos + i as u64;
            if aa[i] != bb[i] && !allowed.iter().any(|(start, end)| at >= *start && at < *end) {
                return Err(format!("undeclared changed byte at {at}"));
            }
        }
        pos += n as u64
    }
    Ok(pos)
}
fn same_metadata(original: &Value, derived: &Value) -> Result<()> {
    for field in [
        "unity_version",
        "serialized_version",
        "object_count",
        "external_references",
    ] {
        if original[field] != derived[field] {
            return Err(format!("container metadata changed:{field}"));
        }
    }
    let a = original["textures"]
        .as_array()
        .ok_or("original textures absent")?;
    let b = derived["textures"]
        .as_array()
        .ok_or("derived textures absent")?;
    if a.len() != b.len() {
        return Err("texture count changed".into());
    }
    for (old, new) in a.iter().zip(b) {
        for key in [
            "path_id",
            "class_id",
            "name",
            "width",
            "height",
            "mip_count",
            "readable",
            "ignore_master_limit",
            "streaming_mipmaps",
            "color_space",
        ] {
            if old[key] != new[key] {
                return Err(format!("texture metadata changed:{}:{key}", old["path_id"]));
            }
        }
        if old["format"] == 25 && old["stream"].is_object() {
            if new["format"] != 4 {
                return Err("BC7 not converted toRGBA32".into());
            }
        } else {
            let (mut old, mut new) = (old.clone(), new.clone());
            if old["stream"].is_object() {
                old["stream"]["resolved_file"] = Value::Null;
            }
            if new["stream"].is_object() {
                new["stream"]["resolved_file"] = Value::Null;
            }
            if old != new {
                return Err("untargeted texture index changed".into());
            }
        }
        if new["stream"].is_object() && new["stream"]["in_bounds"] != true {
            return Err("derived stream outside file".into());
        }
    }
    Ok(())
}
fn create_json(path: &Path, value: &Value) -> Result<()> {
    let mut f = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|e| e.to_string())?;
    f.write_all(&serde_json::to_vec_pretty(value).map_err(|e| e.to_string())?)
        .map_err(|e| e.to_string())?;
    f.sync_all().map_err(|e| e.to_string())
}
fn run() -> Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() != 4 || args[0] != "--input" || args[2] != "--output" {
        return Err("usage: bc7-derive --input ASSETS --output NEW_PRIVATE_DIR".into());
    }
    let source = fs::canonicalize(&args[1]).map_err(|e| e.to_string())?;
    let output = PathBuf::from(&args[3]);
    if !output.is_absolute() || output.exists() {
        return Err("fresh absolute private output directory required".into());
    }
    let output_parent = fs::canonicalize(output.parent().ok_or("output parent absent")?)
        .map_err(|e| e.to_string())?;
    if output_parent.starts_with(source.parent().ok_or("input parent absent")?) {
        return Err("derivative must be outside source installation".into());
    }
    let index = serialized::index_value(&source)?;
    let name = source
        .file_name()
        .ok_or("source basename absent")?
        .to_string_lossy()
        .into_owned();
    let stream_name = format!("{name}.resS");
    let source_stream = source.parent().unwrap().join(&stream_name);
    let rows: Vec<Value> = index["textures"]
        .as_array()
        .ok_or("textures absent")?
        .iter()
        .filter(|t| t["format"] == 25 && t["stream"].is_object())
        .cloned()
        .collect();
    if rows.is_empty() {
        return Err("no BC7 textures to derive".into());
    }
    for row in &rows {
        if row["image_count"] != 1 || row["texture_dimension"] != 2 {
            return Err("derivation admits only one 2D image per texture".into());
        }
        if row["stream"].is_object()
            && (text(&row["stream"], "path")? != stream_name || row["stream"]["in_bounds"] != true)
        {
            return Err("only direct same-container valid resS streams admitted".into());
        }
    }
    let assets_sha = hash(&source)?;
    let stream_sha = hash(&source_stream)?;
    let original_assets_len = fs::metadata(&source).map_err(|e| e.to_string())?.len();
    let original_stream_len = fs::metadata(&source_stream)
        .map_err(|e| e.to_string())?
        .len();
    fs::create_dir(&output).map_err(|e| e.to_string())?;
    create_json(
        &output.join("owner.json"),
        &json!({"schema":"continuum-native-asset-derivative-owner/v1","scope":"private-derived-native-assets"}),
    )?;
    let derived = output.join(&name);
    let derived_stream = output.join(&stream_name);
    fs::copy(&source, &derived).map_err(|e| e.to_string())?;
    fs::copy(&source_stream, &derived_stream).map_err(|e| e.to_string())?;
    writable_private_copy(&derived)?;
    writable_private_copy(&derived_stream)?;
    let mut assets = OpenOptions::new()
        .read(true)
        .write(true)
        .open(&derived)
        .map_err(|e| e.to_string())?;
    let mut stream = OpenOptions::new()
        .read(true)
        .write(true)
        .open(&derived_stream)
        .map_err(|e| e.to_string())?;
    let mut pending = VecDeque::new();
    let mut next = 0;
    let mut patches = Vec::new();
    let mut payload_proofs = Vec::new();
    while next < rows.len() || !pending.is_empty() {
        while next < rows.len() && pending.len() < 4 {
            let row = rows[next].clone();
            let input = source.clone();
            pending.push_back(thread::spawn(move || cook(row, input)));
            next += 1
        }
        let Cooked {
            row,
            payload,
            source_sha,
            decoded_sha,
        } = pending
            .pop_front()
            .unwrap()
            .join()
            .map_err(|_| "decode worker panicked")??;
        let len = payload.len() as u64;
        if row["stream"].is_object() {
            let at = stream.seek(SeekFrom::End(0)).map_err(|e| e.to_string())?;
            u32::try_from(at.checked_add(len).ok_or("stream range overflow")?)
                .map_err(|_| "derived stream exceeds Unity2019 uint32 offsets")?;
            stream.write_all(&payload).map_err(|e| e.to_string())?;
            put32(&mut assets, number(&row, "format_field")?, 4, &mut patches)?;
            put32(
                &mut assets,
                number(&row, "complete_image_size_field")?,
                len,
                &mut patches,
            )?;
            put32(
                &mut assets,
                number(&row["stream"], "offset_field")?,
                at,
                &mut patches,
            )?;
            put32(
                &mut assets,
                number(&row["stream"], "size_field")?,
                len,
                &mut patches,
            )?;
            payload_proofs.push(json!({"pathId":row["path_id"],"name":row["name"],"relativePath":stream_name,"offset":at,"bytes":len,"encodedSha256":source_sha,"decodedSha256":decoded_sha}));
        }
    }
    let assets_len = assets.seek(SeekFrom::End(0)).map_err(|e| e.to_string())?;
    if assets_len != original_assets_len {
        return Err("serialized object layout changed".into());
    }
    assets.sync_all().map_err(|e| e.to_string())?;
    stream.sync_all().map_err(|e| e.to_string())?;
    drop(assets);
    drop(stream);
    let derived_index = serialized::index_value(&derived)?;
    same_metadata(&index, &derived_index)?;
    let remaining = number(&index, "bc7_count")? - rows.len() as u64;
    if derived_index["bc7_count"] != remaining {
        return Err("unexpected remaining BC7 textures".into());
    }
    let original_bytes_checked = same_original_bytes(&source, &derived, &patches)?;
    let stream_prefix_checked = same_original_bytes(&source_stream, &derived_stream, &[])?;
    for proof in &payload_proofs {
        let actual = read_range(
            &output.join(text(proof, "relativePath")?),
            number(proof, "offset")?,
            number(proof, "bytes")?,
        )?;
        if digest(&actual) != text(proof, "decodedSha256")? {
            return Err("stored decoded payload proof differs".into());
        }
    }
    if hash(&source)? != assets_sha || hash(&source_stream)? != stream_sha {
        return Err("original source changed during cook".into());
    }
    create_json(&output.join("source-index.json"), &index)?;
    create_json(&output.join("derived-index.json"), &derived_index)?;
    let files = json!([{"relativePath":name,"sourceSha256":assets_sha,"derivedSha256":hash(&derived)?,"sourceBytes":original_assets_len,"derivedBytes":fs::metadata(&derived).map_err(|e|e.to_string())?.len()},{"relativePath":stream_name,"sourceSha256":stream_sha,"derivedSha256":hash(&derived_stream)?,"sourceBytes":original_stream_len,"derivedBytes":fs::metadata(&derived_stream).map_err(|e|e.to_string())?.len()}]);
    let manifest = json!({"schema":"continuum-native-asset-derivative/v1","status":"complete","treatment":"streamed-BC7-to-RGBA32-authored-mips","decoder":"bcdec_rs=0.2.0","decoderSemantics":"RGBA8-no-gamma-conversion-no-flip-no-swizzle-authored-mips-block-edge-clipping","workers":4,"memoryContract":"at-most-four-asset-jobs-each-encoded-plus-decoded-payload-max512MiB-decoded","qualification":"offline-layout-and-preservation-proved-engine-pixel-and-flight-equivalence-required","files":files,"convertedTextures":rows.len(),"remainingBc7":remaining,"inlineBc7Treatment":"unchanged-stock-fallback","originalContainerBytesChecked":original_bytes_checked,"originalStreamPrefixBytesChecked":stream_prefix_checked,"allowedContainerPatchRanges":patches,"payloads":payload_proofs});
    create_json(&output.join("manifest.json"), &manifest)?;
    println!("Derived {} textures; all original bytes outside declared patches unchanged; {remaining} BC7 remains as stock fallback. Manifest: {}",rows.len(),output.join("manifest.json").display());
    Ok(())
}
fn main() {
    if let Err(error) = run() {
        eprintln!("BC7 derivative: {error}");
        std::process::exit(1)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn constant(red: u8, green: u8, blue: u8, alpha: u8) -> [u8; 16] {
        let mut bytes = [0; 16];
        let mut bit = 0;
        let mut put = |value: u32, n: usize| {
            for i in 0..n {
                bytes[bit / 8] |= (((value >> i) & 1) as u8) << (bit % 8);
                bit += 1
            }
        };
        put(64, 7);
        for component in [red, green, blue, alpha] {
            put(component as u32, 7);
            put(component as u32, 7)
        }
        put(0, 2);
        put(0, 63);
        bytes
    }
    #[test]
    fn authored_mips_and_edge_clipping() {
        let colors = [
            [10, 20, 30, 127],
            [40, 50, 60, 127],
            [70, 80, 90, 127],
            [100, 110, 120, 127],
            [2, 4, 6, 127],
            [8, 10, 12, 127],
        ];
        let mut bytes = Vec::new();
        for c in colors {
            bytes.extend_from_slice(&constant(c[0], c[1], c[2], c[3]))
        }
        let rgba = decode(&bytes, 7, 5, 3).unwrap();
        for y in 0..5 {
            for x in 0..7 {
                let c = colors[(y / 4) * 2 + x / 4];
                assert_eq!(
                    &rgba[(y * 7 + x) * 4..(y * 7 + x + 1) * 4],
                    &[c[0] * 2, c[1] * 2, c[2] * 2, 254]
                )
            }
        }
        assert!(rgba[7 * 5 * 4..(7 * 5 + 3 * 2) * 4]
            .chunks_exact(4)
            .all(|p| p == [4, 8, 12, 254]));
        assert_eq!(&rgba[(7 * 5 + 3 * 2) * 4..], &[16, 20, 24, 254]);
    }
    #[test]
    fn rejects_incomplete_or_extra_mips() {
        let b = constant(10, 20, 30, 127);
        assert!(decode(&b[..15], 4, 4, 1).is_err());
        assert!(decode(&[b, b].concat(), 4, 4, 1).is_err());
    }
}
