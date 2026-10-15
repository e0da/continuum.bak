//! Read-only Unity 2019.4 serialized Texture2D metadata index.
//! Format references: AssetStudio SerializedFile.cs and Classes/Texture{,2D}.cs.
use serde::Serialize;
use std::{
    fs::{File, OpenOptions},
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
};

type Result<T> = std::result::Result<T, String>;
struct Reader {
    file: File,
    end: u64,
}
impl Reader {
    fn open(path: &Path) -> Result<Self> {
        let file = File::open(path).map_err(|e| e.to_string())?;
        let end = file.metadata().map_err(|e| e.to_string())?.len();
        Ok(Self { file, end })
    }
    fn position(&mut self) -> Result<u64> {
        self.file.stream_position().map_err(|e| e.to_string())
    }
    fn seek(&mut self, p: u64) -> Result<()> {
        if p > self.end {
            return Err("range outside file".into());
        }
        self.file
            .seek(SeekFrom::Start(p))
            .map_err(|e| e.to_string())?;
        Ok(())
    }
    fn bytes<const N: usize>(&mut self) -> Result<[u8; N]> {
        let p = self.position()?;
        if p.checked_add(N as u64).ok_or("range overflow")? > self.end {
            return Err("truncated metadata".into());
        }
        let mut b = [0; N];
        self.file.read_exact(&mut b).map_err(|e| e.to_string())?;
        Ok(b)
    }
    fn u8(&mut self) -> Result<u8> {
        Ok(self.bytes::<1>()?[0])
    }
    fn u32(&mut self) -> Result<u32> {
        Ok(u32::from_le_bytes(self.bytes()?))
    }
    fn i32(&mut self) -> Result<i32> {
        Ok(i32::from_le_bytes(self.bytes()?))
    }
    fn i64(&mut self) -> Result<i64> {
        Ok(i64::from_le_bytes(self.bytes()?))
    }
    fn skip(&mut self, n: u64) -> Result<()> {
        let p = self.position()?.checked_add(n).ok_or("range overflow")?;
        self.seek(p)
    }
    fn align(&mut self) -> Result<()> {
        let p = self.position()?;
        self.seek((p + 3) & !3)
    }
    fn count(&mut self) -> Result<usize> {
        let n = self.i32()?;
        if !(0..=1_000_000).contains(&n) {
            return Err("invalid metadata count".into());
        }
        Ok(n as usize)
    }
    fn zero_string(&mut self) -> Result<String> {
        let mut b = Vec::new();
        loop {
            let v = self.u8()?;
            if v == 0 {
                break;
            }
            if b.len() >= 4096 {
                return Err("oversize metadata string".into());
            }
            b.push(v)
        }
        String::from_utf8(b).map_err(|e| e.to_string())
    }
    fn string(&mut self) -> Result<String> {
        let n = self.count()?;
        if n > 4096 {
            return Err("oversize texture string".into());
        }
        let mut b = vec![0; n];
        self.file.read_exact(&mut b).map_err(|e| e.to_string())?;
        self.align()?;
        String::from_utf8(b).map_err(|e| e.to_string())
    }
}
#[derive(Serialize)]
struct Stream {
    path: String,
    offset: u64,
    size: u64,
    offset_field: u64,
    size_field: u64,
    path_field: u64,
    resolved_file: String,
    in_bounds: bool,
}
#[derive(Serialize)]
struct Texture {
    path_id: i64,
    class_id: i32,
    object_offset: u64,
    object_size: u32,
    object_offset_field: u64,
    object_size_field: u64,
    name: String,
    width: i32,
    height: i32,
    complete_image_size: i32,
    complete_image_size_field: u64,
    format: i32,
    format_field: u64,
    mip_count: i32,
    image_count: i32,
    texture_dimension: i32,
    readable: bool,
    ignore_master_limit: bool,
    streaming_mipmaps: bool,
    color_space: i32,
    inline_bytes: u64,
    inline_size_field: u64,
    inline_payload_offset: u64,
    stream: Option<Stream>,
}
#[derive(Serialize)]
struct Report {
    container: String,
    unity_version: String,
    serialized_version: u32,
    file_bytes: u64,
    object_count: usize,
    external_references: Vec<String>,
    textures: Vec<Texture>,
    bc7_count: usize,
    bc7_encoded_bytes: u64,
    bc7_rgba_mip_bytes: u64,
}
struct Object {
    id: i64,
    offset: u64,
    size: u32,
    offset_field: u64,
    size_field: u64,
    class: i32,
}
fn rgba_size(w: i32, h: i32, mips: i32) -> Result<u64> {
    if w <= 0 || h <= 0 || mips <= 0 || mips > 32 {
        return Err("invalid texture dimensions".into());
    }
    let (mut w, mut h) = (w as u64, h as u64);
    let mut n = 0u64;
    for _ in 0..mips {
        n = n
            .checked_add(
                w.checked_mul(h)
                    .and_then(|v| v.checked_mul(4))
                    .ok_or("texture size overflow")?,
            )
            .ok_or("texture size overflow")?;
        w = (w / 2).max(1);
        h = (h / 2).max(1)
    }
    Ok(n)
}
fn texture(r: &mut Reader, o: &Object, container: &Path) -> Result<Texture> {
    r.seek(o.offset)?;
    let file_end = r.end;
    r.end = o
        .offset
        .checked_add(o.size as u64)
        .ok_or("object overflow")?;
    let name = r.string()?;
    r.i32()?;
    r.u8()?;
    r.align()?;
    let width = r.i32()?;
    let height = r.i32()?;
    let complete_image_size_field = r.position()?;
    let complete_image_size = r.i32()?;
    let format_field = r.position()?;
    let format = r.i32()?;
    let mip_count = r.i32()?;
    let readable = r.u8()? != 0;
    let ignore_master_limit = r.u8()? != 0;
    let streaming_mipmaps = r.u8()? != 0;
    r.align()?;
    r.i32()?;
    let image_count = r.i32()?;
    let texture_dimension = r.i32()?;
    r.skip(24)?;
    r.i32()?;
    let color_space = r.i32()?;
    let inline_size_field = r.position()?;
    let data_size = r.i32()?;
    if data_size < 0 {
        return Err("negative inline image size".into());
    }
    let inline_payload_offset = r.position()?;
    let inline_bytes = data_size as u64;
    let stream = if data_size == 0 {
        let offset_field = r.position()?;
        let offset = r.u32()? as u64;
        let size_field = r.position()?;
        let size = r.u32()? as u64;
        let path_field = r.position()?;
        let path = r.string()?;
        if path.is_empty() {
            if offset != 0 || size != 0 {
                return Err("unnamed nonempty stream".into());
            }
            None
        } else {
            let leaf = Path::new(&path)
                .file_name()
                .ok_or("stream filename absent")?;
            let resolved = container
                .parent()
                .ok_or("container directory absent")?
                .join(leaf);
            let in_bounds = std::fs::metadata(&resolved)
                .ok()
                .map(|m| {
                    offset
                        .checked_add(size)
                        .map(|e| e <= m.len())
                        .unwrap_or(false)
                })
                .unwrap_or(false);
            Some(Stream {
                path,
                offset,
                size,
                offset_field,
                size_field,
                path_field,
                resolved_file: resolved.to_string_lossy().into_owned(),
                in_bounds,
            })
        }
    } else {
        r.skip(inline_bytes)?;
        if r.position()? < r.end {
            let offset = r.u32()?;
            let size = r.u32()?;
            let path = r.string()?;
            if offset != 0 || size != 0 || !path.is_empty() {
                return Err("nonempty streaming descriptor beside inline image".into());
            }
        }
        None
    };
    if r.position()? != r.end {
        return Err(format!("texture {} has unexplained trailing bytes: name={name} dimensions={width}x{height} format={format} inline={data_size} remaining={}",o.id,r.end-r.position()?));
    }
    r.end = file_end;
    Ok(Texture {
        path_id: o.id,
        class_id: 28,
        object_offset: o.offset,
        object_size: o.size,
        object_offset_field: o.offset_field,
        object_size_field: o.size_field,
        name,
        width,
        height,
        complete_image_size,
        complete_image_size_field,
        format,
        format_field,
        mip_count,
        image_count,
        texture_dimension,
        readable,
        ignore_master_limit,
        streaming_mipmaps,
        color_space,
        inline_bytes,
        inline_size_field,
        inline_payload_offset,
        stream,
    })
}
fn index(path: &Path) -> Result<Report> {
    let mut r = Reader::open(path)?;
    let header = r.bytes::<20>()?;
    let metadata_size = u32::from_be_bytes(header[0..4].try_into().unwrap()) as u64;
    let declared = u32::from_be_bytes(header[4..8].try_into().unwrap()) as u64;
    let version = u32::from_be_bytes(header[8..12].try_into().unwrap());
    let data_offset = u32::from_be_bytes(header[12..16].try_into().unwrap()) as u64;
    if version != 21
        || header[16] != 0
        || declared != r.end
        || metadata_size > data_offset
        || data_offset > r.end
    {
        return Err("requires valid little-endian serialized version21".into());
    }
    let unity = r.zero_string()?;
    if !unity.starts_with("2019.4.") {
        return Err("requires Unity2019.4 texture schema".into());
    }
    let platform = r.i32()?;
    if platform == -2 {
        return Err("editor objects not supported".into());
    }
    if r.u8()? != 0 {
        return Err("stripped type trees required".into());
    }
    let count = r.count()?;
    let mut types = Vec::new();
    for _ in 0..count {
        let class = r.i32()?;
        r.u8()?;
        r.skip(2)?;
        if class == 114 {
            r.skip(16)?
        }
        r.skip(16)?;
        types.push(class)
    }
    let object_count = r.count()?;
    let mut objects = Vec::new();
    for _ in 0..object_count {
        r.align()?;
        let id = r.i64()?;
        let offset_field = r.position()?;
        let offset = (r.u32()? as u64)
            .checked_add(data_offset)
            .ok_or("object offset overflow")?;
        let size_field = r.position()?;
        let size = r.u32()?;
        let ty = r.i32()?;
        let class = *types
            .get(usize::try_from(ty).map_err(|_| "negative type index")?)
            .ok_or("type index absent")?;
        if offset.checked_add(size as u64).ok_or("object overflow")? > r.end {
            return Err("object outside container".into());
        }
        objects.push(Object {
            id,
            offset,
            size,
            offset_field,
            size_field,
            class,
        })
    }
    let scripts = r.count()?;
    for _ in 0..scripts {
        r.i32()?;
        r.align()?;
        r.skip(8)?
    }
    let external_count = r.count()?;
    let mut external_references = Vec::new();
    for _ in 0..external_count {
        r.zero_string()?;
        r.skip(20)?;
        external_references.push(r.zero_string()?)
    }
    if r.count()? != 0 {
        return Err("reference types unsupported in stripped player".into());
    }
    r.zero_string()?;
    if r.position()? > data_offset {
        return Err("metadata overlaps object data".into());
    }
    let mut textures = Vec::new();
    for o in &objects {
        if o.class == 28 {
            textures.push(
                texture(&mut r, o, path)
                    .map_err(|e| format!("{} pathID{}: {e}", path.display(), o.id))?,
            )
        }
    }
    let bc7_count = textures.iter().filter(|t| t.format == 25).count();
    let mut bc7_encoded_bytes = 0;
    let mut bc7_rgba_mip_bytes = 0;
    for t in &textures {
        if t.format == 25 {
            bc7_encoded_bytes += t.stream.as_ref().map(|s| s.size).unwrap_or(t.inline_bytes);
            bc7_rgba_mip_bytes += rgba_size(t.width, t.height, t.mip_count)?
        }
    }
    Ok(Report {
        container: path.to_string_lossy().into_owned(),
        unity_version: unity,
        serialized_version: version,
        file_bytes: declared,
        object_count,
        external_references,
        textures,
        bc7_count,
        bc7_encoded_bytes,
        bc7_rgba_mip_bytes,
    })
}
fn main() {
    if let Err(e) = run() {
        eprintln!("serialized texture index: {e}");
        std::process::exit(1)
    }
}
pub fn index_value(path: &Path) -> Result<serde_json::Value> {
    serde_json::to_value(index(path)?).map_err(|e| e.to_string())
}
fn run() -> Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() < 3 || args[0] != "--output" {
        return Err("usage: serialized-textures --output NEW_JSON CONTAINER...".into());
    }
    let output = PathBuf::from(&args[1]);
    let mut reports = Vec::new();
    for path in &args[2..] {
        let report = index(Path::new(path))?;
        println!(
            "{}: {} Texture2D, {} BC7, {} encoded bytes, {} RGBA mip bytes",
            path,
            report.textures.len(),
            report.bc7_count,
            report.bc7_encoded_bytes,
            report.bc7_rgba_mip_bytes
        );
        reports.push(report)
    }
    let bytes=serde_json::to_vec_pretty(&serde_json::json!({"schema":"continuum-unity2019-texture-index/v1","scope":"read-only-serialized-metadata-no-runtime-cost-attribution","formatReferences":["https://github.com/Perfare/AssetStudio/blob/master/AssetStudio/SerializedFile.cs","https://github.com/Perfare/AssetStudio/blob/master/AssetStudio/Classes/Texture2D.cs"],"containers":reports})).map_err(|e|e.to_string())?;
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(output)
        .map_err(|e| e.to_string())?;
    file.write_all(&bytes).map_err(|e| e.to_string())?;
    Ok(())
}
