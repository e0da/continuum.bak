use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeSet,
    fs::File,
    io::{self, BufReader, Cursor, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
};

pub type Result<T> = std::result::Result<T, String>;
const MAX_METADATA: u32 = 16 * 1024 * 1024;
const MAX_ENTRIES: u32 = 65536;
const MAX_DECODED: u64 = 2 * 1024 * 1024 * 1024;
const MAX_LZ4_BLOCK: u32 = 16 * 1024 * 1024;

fn io<T>(value: io::Result<T>) -> Result<T> {
    value.map_err(|e| e.to_string())
}
fn u16be(r: &mut impl Read) -> Result<u16> {
    let mut b = [0; 2];
    io(r.read_exact(&mut b))?;
    Ok(u16::from_be_bytes(b))
}
fn u32be(r: &mut impl Read) -> Result<u32> {
    let mut b = [0; 4];
    io(r.read_exact(&mut b))?;
    Ok(u32::from_be_bytes(b))
}
fn u64be(r: &mut impl Read) -> Result<u64> {
    let mut b = [0; 8];
    io(r.read_exact(&mut b))?;
    Ok(u64::from_be_bytes(b))
}
fn string(r: &mut impl Read, limit: usize) -> Result<String> {
    let mut result = Vec::new();
    for _ in 0..limit {
        let mut b = [0];
        io(r.read_exact(&mut b))?;
        if b[0] == 0 {
            return String::from_utf8(result).map_err(|e| e.to_string());
        }
        result.push(b[0]);
    }
    Err("unterminated or oversized UnityFS string".into())
}
fn align16(position: u64) -> Result<u64> {
    Ok(position.checked_add(15).ok_or("offset overflow")? & !15)
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq, PartialOrd, Ord)]
pub struct Node {
    pub name: String,
    pub offset: u64,
    pub length: u64,
    pub flags: u32,
}
#[derive(Debug)]
struct Block {
    expanded: u32,
    compressed: u32,
    flags: u16,
}
#[derive(Debug, Serialize)]
pub struct Audit {
    pub file_sha256: String,
    pub file_length: u64,
    pub engine: String,
    pub compatibility: String,
    pub decoded_sha256: String,
    pub decoded_length: u64,
    pub block_count: usize,
    pub compression_kinds: Vec<u16>,
    pub nodes: Vec<Node>,
}

struct HashOutput {
    digest: Sha256,
    written: u64,
    limit: u64,
}
impl Write for HashOutput {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        let next = self
            .written
            .checked_add(bytes.len() as u64)
            .ok_or_else(|| io::Error::other("decoded length overflow"))?;
        if next > self.limit {
            return Err(io::Error::other("decoded payload exceeds declared length"));
        }
        self.digest.update(bytes);
        self.written = next;
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

pub fn file_hash(path: &Path) -> Result<String> {
    let mut input = io(File::open(path))?;
    let mut digest = Sha256::new();
    let mut buffer = [0; 65536];
    loop {
        let n = io(input.read(&mut buffer))?;
        if n == 0 {
            break;
        }
        digest.update(&buffer[..n]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

fn decode_lzma(
    input: &mut impl Read,
    compressed: u32,
    expanded: u32,
    output: &mut impl Write,
) -> Result<()> {
    let mut limited = BufReader::new(input.take(compressed as u64));
    let options = lzma_rs::decompress::Options {
        unpacked_size: lzma_rs::decompress::UnpackedSize::UseProvided(None),
        memlimit: Some(64 * 1024 * 1024),
        allow_incomplete: false,
    };
    struct Bounded<'a, W> {
        target: &'a mut W,
        written: u64,
        limit: u64,
    }
    impl<W: Write> Write for Bounded<'_, W> {
        fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
            if self.written + bytes.len() as u64 > self.limit {
                return Err(io::Error::other("LZMA output exceeds declared size"));
            }
            let count = self.target.write(bytes)?;
            self.written += count as u64;
            Ok(count)
        }
        fn flush(&mut self) -> io::Result<()> {
            self.target.flush()
        }
    }
    let mut bounded = Bounded {
        target: output,
        written: 0,
        limit: expanded as u64,
    };
    lzma_rs::lzma_decompress_with_options(&mut limited, &mut bounded, &options)
        .map_err(|e| e.to_string())?;
    if bounded.written != expanded as u64 {
        return Err("LZMA decoded size differs".into());
    }
    if !limited.buffer().is_empty() || limited.get_ref().limit() != 0 {
        return Err("LZMA block has unconsumed compressed bytes".into());
    }
    Ok(())
}

fn decode_block(input: &mut impl Read, block: &Block, output: &mut impl Write) -> Result<()> {
    match block.flags & 0x3f {
        0 => {
            if block.compressed != block.expanded {
                return Err("uncompressed block sizes differ".into());
            }
            let copied = io(io::copy(&mut input.take(block.compressed as u64), output))?;
            if copied != block.expanded as u64 {
                return Err("truncated uncompressed block".into());
            }
        }
        1 => decode_lzma(input, block.compressed, block.expanded, output)?,
        2 | 3 => {
            if block.expanded > MAX_LZ4_BLOCK || block.compressed > MAX_LZ4_BLOCK {
                return Err("LZ4 block exceeds memory bound".into());
            }
            let mut encoded = vec![0; block.compressed as usize];
            io(input.read_exact(&mut encoded))?;
            let decoded = lz4_flex::block::decompress(&encoded, block.expanded as usize)
                .map_err(|e| e.to_string())?;
            if decoded.len() != block.expanded as usize {
                return Err("LZ4 decoded length differs".into());
            }
            io(output.write_all(&decoded))?;
        }
        _ => return Err("unsupported UnityFS compression".into()),
    }
    Ok(())
}

fn parse_and_decode(
    r: &mut (impl Read + Seek),
    file_length: u64,
    file_sha256: String,
) -> Result<Audit> {
    if string(r, 8)? != "UnityFS" || u32be(r)? != 7 {
        return Err("only UnityFS version 7 is admitted".into());
    }
    let compatibility = string(r, 128)?;
    let engine = string(r, 128)?;
    if u64be(r)? != file_length {
        return Err("UnityFS declared file size differs".into());
    }
    let metadata_compressed = u32be(r)?;
    let metadata_expanded = u32be(r)?;
    let flags = u32be(r)?;
    if flags & !0x2ff != 0 || flags & 0x40 == 0 {
        return Err("unsupported UnityFS archive flags".into());
    }
    if metadata_compressed == 0
        || metadata_compressed > MAX_METADATA
        || metadata_expanded == 0
        || metadata_expanded > MAX_METADATA
    {
        return Err("metadata size exceeds bounds".into());
    }
    let header_end = align16(io(r.stream_position())?)?;
    let at_end = flags & 0x80 != 0;
    let metadata_offset = if at_end {
        file_length
            .checked_sub(metadata_compressed as u64)
            .ok_or("metadata exceeds file")?
    } else {
        header_end
    };
    if metadata_offset < header_end {
        return Err("metadata overlaps header".into());
    }
    io(r.seek(SeekFrom::Start(metadata_offset)))?;
    let mut metadata = Vec::new();
    decode_block(
        r,
        &Block {
            expanded: metadata_expanded,
            compressed: metadata_compressed,
            flags: (flags & 0x3f) as u16,
        },
        &mut metadata,
    )?;
    if metadata.len() != metadata_expanded as usize {
        return Err("metadata decoded length differs".into());
    }
    let mut m = Cursor::new(metadata);
    let mut stored_digest = [0; 16];
    io(m.read_exact(&mut stored_digest))?;
    let count = u32be(&mut m)?;
    if count == 0 || count > MAX_ENTRIES {
        return Err("block count exceeds bounds".into());
    }
    let mut blocks = Vec::new();
    let mut decoded_length = 0u64;
    let mut compressed_length = 0u64;
    for _ in 0..count {
        let block = Block {
            expanded: u32be(&mut m)?,
            compressed: u32be(&mut m)?,
            flags: u16be(&mut m)?,
        };
        if block.flags & !0x7f != 0 || block.expanded == 0 || block.compressed == 0 {
            return Err("unsupported or empty payload block".into());
        }
        decoded_length = decoded_length
            .checked_add(block.expanded as u64)
            .ok_or("payload overflow")?;
        compressed_length = compressed_length
            .checked_add(block.compressed as u64)
            .ok_or("payload overflow")?;
        blocks.push(block);
    }
    if decoded_length > MAX_DECODED {
        return Err("decoded payload exceeds bound".into());
    }
    let node_count = u32be(&mut m)?;
    if node_count == 0 || node_count > MAX_ENTRIES {
        return Err("node count exceeds bounds".into());
    }
    let mut nodes = Vec::new();
    let mut names = BTreeSet::new();
    for _ in 0..node_count {
        let offset = u64be(&mut m)?;
        let length = u64be(&mut m)?;
        let flags = u32be(&mut m)?;
        let name = string(&mut m, 4096)?;
        if name.is_empty()
            || !names.insert(name.clone())
            || offset.checked_add(length).ok_or("node range overflow")? > decoded_length
        {
            return Err("invalid or duplicate archive node".into());
        }
        nodes.push(Node {
            name,
            offset,
            length,
            flags,
        });
    }
    if m.position() != m.get_ref().len() as u64 {
        return Err("metadata has trailing data".into());
    }
    nodes.sort();
    let mut data_start = if at_end {
        header_end
    } else {
        metadata_offset
            .checked_add(metadata_compressed as u64)
            .ok_or("metadata offset overflow")?
    };
    if flags & 0x200 != 0 {
        data_start = align16(data_start)?;
    }
    let data_end = data_start
        .checked_add(compressed_length)
        .ok_or("payload offset overflow")?;
    if data_end != if at_end { metadata_offset } else { file_length } {
        return Err("payload does not exactly fill declared region".into());
    }
    io(r.seek(SeekFrom::Start(data_start)))?;
    let mut output = HashOutput {
        digest: Sha256::new(),
        written: 0,
        limit: decoded_length,
    };
    for block in &blocks {
        let previous = output.written;
        decode_block(r, block, &mut output)?;
        if output.written - previous != block.expanded as u64 {
            return Err("decoded block size differs".into());
        }
    }
    if output.written != decoded_length || io(r.stream_position())? != data_end {
        return Err("payload completion differs".into());
    }
    let compression_kinds = blocks
        .iter()
        .map(|b| b.flags & 0x3f)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();
    Ok(Audit {
        file_sha256,
        file_length,
        compatibility,
        engine,
        decoded_sha256: format!("{:x}", output.digest.finalize()),
        decoded_length,
        block_count: blocks.len(),
        compression_kinds,
        nodes,
    })
}

pub fn audit(path: &Path) -> Result<Audit> {
    if !io(std::fs::metadata(path))?.is_file() {
        return Err("audit input must be a regular file".into());
    }
    let digest = file_hash(path)?;
    let mut file = io(File::open(path))?;
    let length = io(file.metadata())?.len();
    let result = parse_and_decode(&mut file, length, digest.clone())?;
    if file_hash(path)? != digest {
        return Err("bundle changed during audit".into());
    }
    Ok(result)
}
pub fn require_equal(original: &Audit, derived: &Audit) -> Result<()> {
    if original.engine != derived.engine
        || original.compatibility != derived.compatibility
        || original.decoded_length != derived.decoded_length
        || original.decoded_sha256 != derived.decoded_sha256
        || original.nodes != derived.nodes
    {
        return Err("decoded payload or normalized node directory differs".into());
    }
    Ok(())
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Manifest {
    schema: String,
    status: String,
    engine: String,
    compression: String,
    bundles: Vec<Row>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Row {
    relative_path: String,
    original_path: PathBuf,
    derived_path: PathBuf,
    source_sha256: String,
    derived_sha256: String,
    source_length: u64,
    derived_length: u64,
}

pub fn audit_manifest(path: &Path) -> Result<serde_json::Value> {
    if io(std::fs::metadata(path))?.len() > 65536 {
        return Err("manifest exceeds size bound".into());
    }
    let manifest_sha256 = file_hash(path)?;
    let manifest: Manifest =
        serde_json::from_slice(&io(std::fs::read(path))?).map_err(|e| e.to_string())?;
    if manifest.schema != "continuum-bundle-cache/v1"
        || manifest.status != "complete"
        || manifest.compression != "LZ4Runtime"
        || manifest.bundles.len() != 3
    {
        return Err("manifest configuration differs".into());
    }
    let mut names = BTreeSet::new();
    let mut results = Vec::new();
    for row in &manifest.bundles {
        if !names.insert(&row.relative_path)
            || !row.original_path.is_absolute()
            || !row.derived_path.is_absolute()
        {
            return Err("manifest paths differ".into());
        }
        let source = audit(&row.original_path)?;
        let derived = audit(&row.derived_path)?;
        if source.file_sha256 != row.source_sha256
            || derived.file_sha256 != row.derived_sha256
            || source.file_length != row.source_length
            || derived.file_length != row.derived_length
            || source.engine != manifest.engine
        {
            return Err("artifact manifest binding differs".into());
        }
        require_equal(&source, &derived)?;
        results.push(serde_json::json!({"relativePath":row.relative_path,"original":source,"derived":derived}));
    }
    if file_hash(path)? != manifest_sha256 {
        return Err("manifest changed during audit".into());
    }
    for row in &manifest.bundles {
        if file_hash(&row.original_path)? != row.source_sha256
            || file_hash(&row.derived_path)? != row.derived_sha256
        {
            return Err("artifact changed after pair audit".into());
        }
    }
    Ok(
        serde_json::json!({"schema":"continuum-bundle-content-equivalence/v1","status":"passed","manifestSha256":manifest_sha256,
        "qualification":"independent-decoded-payload-and-node-directory-equivalence-not-flight-fidelity","decoder":"lzma-rs-0.3/lz4-flex-0.11",
        "bundles":results}),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture(
        payload: &[u8],
        kind: u16,
        chunks: usize,
        at_end: bool,
        nodes: &[Node],
        compressed_metadata: bool,
    ) -> Vec<u8> {
        let mut encoded_blocks = Vec::new();
        for part in payload.chunks(payload.len().div_ceil(chunks)) {
            let encoded = match kind {
                0 => part.to_vec(),
                1 => {
                    let mut bytes = Vec::new();
                    let options = lzma_rs::compress::Options {
                        unpacked_size: lzma_rs::compress::UnpackedSize::WriteToHeader(Some(
                            part.len() as u64,
                        )),
                    };
                    lzma_rs::lzma_compress_with_options(
                        &mut Cursor::new(part),
                        &mut bytes,
                        &options,
                    )
                    .unwrap();
                    bytes.drain(5..13);
                    bytes
                }
                2 | 3 => lz4_flex::block::compress(part),
                _ => unreachable!(),
            };
            encoded_blocks.push((part.len(), encoded));
        }
        let mut metadata = vec![0; 16];
        metadata.extend_from_slice(&(encoded_blocks.len() as u32).to_be_bytes());
        for (expanded, bytes) in &encoded_blocks {
            metadata.extend_from_slice(&(*expanded as u32).to_be_bytes());
            metadata.extend_from_slice(&(bytes.len() as u32).to_be_bytes());
            metadata.extend_from_slice(&(kind | 0x40).to_be_bytes());
        }
        metadata.extend_from_slice(&(nodes.len() as u32).to_be_bytes());
        for node in nodes {
            metadata.extend_from_slice(&node.offset.to_be_bytes());
            metadata.extend_from_slice(&node.length.to_be_bytes());
            metadata.extend_from_slice(&node.flags.to_be_bytes());
            metadata.extend_from_slice(node.name.as_bytes());
            metadata.push(0);
        }
        let metadata_expanded = metadata.len();
        if compressed_metadata {
            metadata = lz4_flex::block::compress(&metadata);
        }
        let mut result = b"UnityFS\0".to_vec();
        result.extend_from_slice(&7u32.to_be_bytes());
        result.extend_from_slice(b"5.x.x\0");
        result.extend_from_slice(b"2019.4.18f1\0");
        let size_offset = result.len();
        result.extend_from_slice(&0u64.to_be_bytes());
        result.extend_from_slice(&(metadata.len() as u32).to_be_bytes());
        result.extend_from_slice(&(metadata_expanded as u32).to_be_bytes());
        result.extend_from_slice(
            &(0x40u32 | if at_end { 0x80 } else { 0 } | if compressed_metadata { 3 } else { 0 })
                .to_be_bytes(),
        );
        result.resize((result.len() + 15) & !15, 0);
        if !at_end {
            result.extend_from_slice(&metadata);
        }
        for (_, bytes) in encoded_blocks {
            result.extend_from_slice(&bytes);
        }
        if at_end {
            result.extend_from_slice(&metadata);
        }
        let length = result.len() as u64;
        result[size_offset..size_offset + 8].copy_from_slice(&length.to_be_bytes());
        result
    }
    fn node(length: u64) -> Node {
        Node {
            name: "CAB-owned-fixture".into(),
            offset: 0,
            length,
            flags: 4,
        }
    }
    fn parse(bytes: &[u8]) -> Result<Audit> {
        parse_and_decode(
            &mut Cursor::new(bytes),
            bytes.len() as u64,
            "fixture".into(),
        )
    }

    #[test]
    fn lzma_and_chunked_lz4_match_known_payload_digest_and_directory() {
        let nodes = [node(3)];
        let original = parse(&fixture(b"abc", 1, 1, false, &nodes, true)).unwrap();
        let derived = parse(&fixture(b"abc", 2, 3, true, &nodes, false)).unwrap();
        assert_eq!(
            original.decoded_sha256,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(original.decoded_length, 3);
        assert_eq!(original.block_count, 1);
        assert_eq!(derived.block_count, 3);
        require_equal(&original, &derived).unwrap();
    }
    #[test]
    fn rejects_changed_payload_or_directory_even_when_both_decode() {
        let original = parse(&fixture(b"abc", 0, 1, false, &[node(3)], false)).unwrap();
        let changed = parse(&fixture(b"abd", 2, 1, true, &[node(3)], false)).unwrap();
        assert!(require_equal(&original, &changed).is_err());
        let mut renamed = node(3);
        renamed.name = "CAB-other".into();
        let directory_changed = parse(&fixture(b"abc", 2, 1, true, &[renamed], false)).unwrap();
        assert!(require_equal(&original, &directory_changed).is_err());
    }
    #[test]
    fn rejects_truncation_duplicate_nodes_and_out_of_range_nodes() {
        let good = fixture(b"abc", 2, 1, true, &[node(3)], true);
        for length in [0, 7, 20, good.len() - 1] {
            assert!(parse(&good[..length]).is_err());
        }
        assert!(parse(&fixture(b"abc", 0, 1, false, &[node(3), node(3)], false)).is_err());
        assert!(parse(&fixture(b"abc", 0, 1, false, &[node(4)], false)).is_err());
    }
    #[test]
    fn rejects_oversized_metadata_unknown_flags_and_trailing_bytes() {
        let good = fixture(b"abc", 0, 1, false, &[node(3)], false);
        let mut huge = good.clone();
        huge[42..46].copy_from_slice(&(MAX_METADATA + 1).to_be_bytes());
        assert!(parse(&huge).is_err());
        let mut flags = good.clone();
        flags[46..50].copy_from_slice(&0x144u32.to_be_bytes());
        assert!(parse(&flags).is_err());
        let mut trailing = good.clone();
        trailing.push(0);
        let length = trailing.len() as u64;
        trailing[30..38].copy_from_slice(&length.to_be_bytes());
        assert!(parse(&trailing).is_err());
    }
    #[test]
    fn normalizes_node_order_without_ignoring_node_ranges() {
        let a = Node {
            name: "a".into(),
            offset: 0,
            length: 1,
            flags: 0,
        };
        let b = Node {
            name: "b".into(),
            offset: 1,
            length: 2,
            flags: 4,
        };
        let original = parse(&fixture(
            b"abc",
            0,
            1,
            false,
            &[a.clone(), b.clone()],
            false,
        ))
        .unwrap();
        let derived = parse(&fixture(b"abc", 2, 2, true, &[b, a], true)).unwrap();
        require_equal(&original, &derived).unwrap();
    }
    #[test]
    fn file_backed_audit_binds_raw_hash_and_rejects_decoded_corruption() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("owned.bundle");
        let good = fixture(b"abc", 0, 1, false, &[node(3)], false);
        std::fs::write(&path, &good).unwrap();
        let audited = audit(&path).unwrap();
        assert_eq!(audited.file_sha256, format!("{:x}", Sha256::digest(&good)));
        let mut bad = fixture(b"abc", 2, 1, false, &[node(3)], false);
        let n = bad.len();
        bad[n - 1] ^= 0x40;
        std::fs::write(&path, &bad).unwrap();
        match audit(&path) {
            Ok(changed) => assert!(require_equal(&audited, &changed).is_err()),
            Err(_) => {}
        }
    }
    #[test]
    fn rejects_unsupported_payload_codec_and_lzma_truncation() {
        let mut unknown = fixture(b"abc", 0, 1, false, &[node(3)], false);
        unknown[92..94].copy_from_slice(&0x45u16.to_be_bytes());
        assert!(parse(&unknown).is_err());
        let mut truncated = fixture(b"abc", 1, 1, false, &[node(3)], false);
        truncated.pop();
        let length = truncated.len() as u64;
        truncated[30..38].copy_from_slice(&length.to_be_bytes());
        assert!(parse(&truncated).is_err());
    }
    #[test]
    fn manifest_proof_requires_every_artifact_hash_and_size_binding() {
        let directory = tempfile::tempdir().unwrap();
        let mut rows = Vec::new();
        for index in 0..3 {
            let source = directory.path().join(format!("source-{index}"));
            let derived = directory.path().join(format!("cache-{index}"));
            std::fs::write(&source, fixture(b"abc", 0, 1, false, &[node(3)], false)).unwrap();
            std::fs::write(&derived, fixture(b"abc", 2, 3, true, &[node(3)], false)).unwrap();
            rows.push(serde_json::json!({"relativePath":format!("owned-{index}"),"originalPath":source,"derivedPath":derived,
                "sourceSha256":file_hash(&source).unwrap(),"derivedSha256":file_hash(&derived).unwrap(),
                "sourceLength":std::fs::metadata(&source).unwrap().len(),"derivedLength":std::fs::metadata(&derived).unwrap().len()}));
        }
        let path = directory.path().join("manifest.json");
        let mut manifest = serde_json::json!({"schema":"continuum-bundle-cache/v1","status":"complete","compression":"LZ4Runtime","engine":"2019.4.18f1","bundles":rows});
        std::fs::write(&path, serde_json::to_vec(&manifest).unwrap()).unwrap();
        assert_eq!(audit_manifest(&path).unwrap()["status"], "passed");
        manifest["bundles"][1]["derivedSha256"] = serde_json::json!("0".repeat(64));
        std::fs::write(&path, serde_json::to_vec(&manifest).unwrap()).unwrap();
        assert!(audit_manifest(&path).is_err());
    }
}
