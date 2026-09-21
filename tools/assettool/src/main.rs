use crc32fast::Hasher;
use flate2::{write::ZlibEncoder, Compression};
use gltf::Gltf;
use pof::Parser;
use std::{
    collections::BTreeMap,
    env,
    error::Error,
    fs::{self, File},
    io::{Cursor, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
};

type Result<T> = std::result::Result<T, Box<dyn Error>>;

const MODEL_ENTRY: &str = "data/models/fighter06.pof";
const TEXTURE_ENTRY: &str = "data/maps/fighter06-01a.pcx";

#[derive(Clone)]
struct VpFile {
    path: String,
    offset: u64,
    size: u64,
}

struct MeshChunk {
    positions: Vec<[f32; 3]>,
    normals: Vec<[f32; 3]>,
    uvs: Vec<[f32; 2]>,
    indices: Vec<u32>,
}

fn main() {
    if let Err(error) = run() {
        eprintln!("FS2Hercules asset extraction failed: {error}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let args: Vec<String> = env::args().collect();
    if args.iter().any(|arg| arg == "--help" || arg == "-h") {
        print_help();
        return Ok(());
    }
    let freespace = required_arg(&args, "--freespace")?;
    let output =
        PathBuf::from(optional_arg(&args, "--output").unwrap_or_else(|| "assets".to_string()));
    fs::create_dir_all(&output)?;

    let archives = find_archives(Path::new(&freespace))?;
    let model_bytes = read_vp_entry(&archives, MODEL_ENTRY)?;
    let texture_bytes = read_vp_entry(&archives, TEXTURE_ENTRY)?;
    let mesh = convert_model(&model_bytes)?;
    write_nomesh(&output.join("hercules.nomesh"), &mesh)?;

    let (width, height, albedo) = decode_pcx(&texture_bytes)?;
    write_png(&output.join("HercPBR.png"), width, height, &albedo)?;
    write_png(
        &output.join("HercPBR-normal.png"),
        width,
        height,
        &vec![128, 128, 255, 255]
            .into_iter()
            .cycle()
            .take(width as usize * height as usize * 4)
            .collect::<Vec<_>>(),
    )?;
    write_png(
        &output.join("HercPBR-glow.png"),
        width,
        height,
        &vec![0, 0, 0, 255]
            .into_iter()
            .cycle()
            .take(width as usize * height as usize * 4)
            .collect::<Vec<_>>(),
    )?;

    println!("Generated FS2Hercules assets in {}", output.display());
    println!("The original VP, POF, and PCX data was read in memory and was not copied.");
    Ok(())
}

fn print_help() {
    println!("FS2Hercules asset extractor");
    println!();
    println!("Usage:");
    println!("  FS2Hercules.AssetTool.exe --freespace <FS2 folder or VP file> [--output <assets folder>]");
}

fn required_arg(args: &[String], name: &str) -> Result<String> {
    optional_arg(args, name).ok_or_else(|| format!("missing {name}; use --help for usage").into())
}

fn optional_arg(args: &[String], name: &str) -> Option<String> {
    args.windows(2)
        .find(|pair| pair[0].eq_ignore_ascii_case(name))
        .map(|pair| pair[1].clone())
}

fn find_archives(root: &Path) -> Result<Vec<PathBuf>> {
    if root.is_file() {
        if root
            .extension()
            .and_then(|ext| ext.to_str())
            .unwrap_or_default()
            .eq_ignore_ascii_case("vp")
        {
            return Ok(vec![root.to_path_buf()]);
        }
        return Err(format!("not a VP archive: {}", root.display()).into());
    }
    if !root.is_dir() {
        return Err(format!("FreeSpace 2 path does not exist: {}", root.display()).into());
    }
    let mut archives = Vec::new();
    collect_archives(root, &mut archives)?;
    archives.sort_by_key(|path| {
        (
            !path
                .file_name()
                .and_then(|name| name.to_str())
                .unwrap_or_default()
                .eq_ignore_ascii_case("sparky_fs2.vp"),
            path.to_string_lossy().to_ascii_lowercase(),
        )
    });
    if archives.is_empty() {
        return Err(format!("no .vp archives found under {}", root.display()).into());
    }
    Ok(archives)
}

fn collect_archives(root: &Path, archives: &mut Vec<PathBuf>) -> Result<()> {
    for entry in fs::read_dir(root)? {
        let path = entry?.path();
        if path.is_dir() {
            collect_archives(&path, archives)?;
        } else if path
            .extension()
            .and_then(|ext| ext.to_str())
            .unwrap_or_default()
            .eq_ignore_ascii_case("vp")
        {
            archives.push(path);
        }
    }
    Ok(())
}

fn read_vp_entry(archives: &[PathBuf], wanted: &str) -> Result<Vec<u8>> {
    for archive in archives {
        let mut file = File::open(archive)?;
        for entry in vp_entries(&mut file)? {
            if entry.path.eq_ignore_ascii_case(wanted) {
                file.seek(SeekFrom::Start(entry.offset))?;
                let mut bytes = vec![0; entry.size as usize];
                file.read_exact(&mut bytes)?;
                println!("Read {wanted} from {}", archive.display());
                return Ok(bytes);
            }
        }
    }
    Err(format!("could not find {wanted} in the FreeSpace 2 VP archives").into())
}

fn vp_entries(file: &mut File) -> Result<Vec<VpFile>> {
    let mut header = [0; 16];
    file.read_exact(&mut header)?;
    if &header[0..4] != b"VPVP" {
        return Err("invalid VP archive header".into());
    }
    let directory_offset = i32::from_le_bytes(header[8..12].try_into()?) as u64;
    let count = i32::from_le_bytes(header[12..16].try_into()?) as usize;
    file.seek(SeekFrom::Start(directory_offset))?;
    let mut stack: Vec<String> = Vec::new();
    let mut entries = Vec::new();
    for _ in 0..count {
        let mut record = [0; 44];
        file.read_exact(&mut record)?;
        let offset = i32::from_le_bytes(record[0..4].try_into()?) as u64;
        let size = i32::from_le_bytes(record[4..8].try_into()?) as u64;
        let name_end = record[8..40]
            .iter()
            .position(|byte| *byte == 0)
            .unwrap_or(32);
        let name = String::from_utf8_lossy(&record[8..8 + name_end]).to_string();
        if name == ".." {
            stack.pop();
        } else if size == 0 {
            stack.push(name);
        } else {
            let path = if stack.is_empty() {
                name
            } else {
                format!("{}/{}", stack.join("/"), name)
            };
            entries.push(VpFile { path, offset, size });
        }
    }
    Ok(entries)
}

fn convert_model(bytes: &[u8]) -> Result<BTreeMap<String, Vec<MeshChunk>>> {
    let model = Parser::new(Cursor::new(bytes.to_vec()))?.parse(PathBuf::from("fighter06.pof"))?;
    let mut glb = Vec::new();
    model.write_gltf(&mut glb, true)?;
    let document = Gltf::from_slice(&glb)?;
    let blob = document
        .blob
        .as_deref()
        .ok_or("converted model has no GLB buffer")?;
    let root = document
        .nodes()
        .find(|node| node.name() == Some("fighter06a"))
        .ok_or("converted model has no fighter06a root node")?;
    let mut by_material = BTreeMap::new();
    walk_node(root, [0.0; 3], blob, &mut by_material)?;
    by_material.retain(|material, _| material == "fighter06-01a");
    if by_material.is_empty() {
        return Err("fighter06-01a hull material was not found".into());
    }
    Ok(by_material)
}

fn walk_node(
    node: gltf::Node,
    parent_translation: [f32; 3],
    blob: &[u8],
    by_material: &mut BTreeMap<String, Vec<MeshChunk>>,
) -> Result<()> {
    let local_translation = node.transform().decomposed().0;
    let translation = [
        parent_translation[0] + local_translation[0],
        parent_translation[1] + local_translation[1],
        parent_translation[2] + local_translation[2],
    ];
    if !node.name().unwrap_or_default().starts_with("insignia") {
        if let Some(mesh) = node.mesh() {
            for primitive in mesh.primitives() {
                let material = primitive.material().name().unwrap_or("none").to_string();
                let reader = primitive.reader(|buffer| (buffer.index() == 0).then_some(blob));
                let positions = reader
                    .read_positions()
                    .ok_or("hull primitive has no positions")?
                    .map(|position| {
                        [
                            position[0] + translation[0],
                            position[1] + translation[1],
                            position[2] + translation[2],
                        ]
                    })
                    .collect::<Vec<_>>();
                let normals = reader
                    .read_normals()
                    .ok_or("hull primitive has no normals")?
                    .collect::<Vec<_>>();
                let uvs = reader
                    .read_tex_coords(0)
                    .ok_or("hull primitive has no texture coordinates")?
                    .into_f32()
                    .collect::<Vec<_>>();
                let indices = reader
                    .read_indices()
                    .map(|values| values.into_u32().collect())
                    .unwrap_or_else(|| (0..positions.len() as u32).collect());
                by_material.entry(material).or_default().push(MeshChunk {
                    positions,
                    normals,
                    uvs,
                    indices,
                });
            }
        }
    }
    for child in node.children() {
        walk_node(child, translation, blob, by_material)?;
    }
    Ok(())
}

fn write_nomesh(path: &Path, materials: &BTreeMap<String, Vec<MeshChunk>>) -> Result<()> {
    let mut output = Vec::new();
    output.extend_from_slice(&(materials.len() as i32).to_le_bytes());
    for (_material, chunks) in materials {
        let material_bytes = b"HercPBR";
        output.extend_from_slice(&(material_bytes.len() as i32).to_le_bytes());
        output.extend_from_slice(material_bytes);
        let vertex_count = chunks
            .iter()
            .map(|chunk| chunk.positions.len())
            .sum::<usize>();
        let index_count = chunks
            .iter()
            .map(|chunk| chunk.indices.len())
            .sum::<usize>();
        output.extend_from_slice(&(vertex_count as i32).to_le_bytes());
        for chunk in chunks {
            for index in 0..chunk.positions.len() {
                let position = chunk.positions[index];
                let normal = chunk.normals[index];
                let uv = chunk.uvs[index];
                for value in [
                    position[0],
                    position[1],
                    -position[2],
                    normal[0],
                    normal[1],
                    -normal[2],
                    uv[0],
                    1.0 - uv[1],
                ] {
                    output.extend_from_slice(&value.to_le_bytes());
                }
            }
        }
        output.extend_from_slice(&(index_count as i32).to_le_bytes());
        let mut vertex_base = 0;
        for chunk in chunks {
            for triangle in chunk.indices.chunks_exact(3) {
                for index in [triangle[0], triangle[2], triangle[1]] {
                    output.extend_from_slice(&(index + vertex_base).to_le_bytes());
                }
            }
            vertex_base += chunk.positions.len() as u32;
        }
    }
    fs::write(path, output)?;
    println!(
        "Wrote {} hull material from {}",
        path.display(),
        materials.keys().next().unwrap_or(&String::new())
    );
    Ok(())
}

fn decode_pcx(data: &[u8]) -> Result<(u32, u32, Vec<u8>)> {
    if data.len() < 897 || data[0] != 0x0a || data[2] != 1 || data[3] != 8 || data[65] != 1 {
        return Err("unsupported PCX format; expected an indexed 8-bit image".into());
    }
    let x_min = u16::from_le_bytes(data[4..6].try_into()?) as u32;
    let y_min = u16::from_le_bytes(data[6..8].try_into()?) as u32;
    let x_max = u16::from_le_bytes(data[8..10].try_into()?) as u32;
    let y_max = u16::from_le_bytes(data[10..12].try_into()?) as u32;
    let width = x_max - x_min + 1;
    let height = y_max - y_min + 1;
    let bytes_per_line = u16::from_le_bytes(data[66..68].try_into()?) as usize;
    if data[data.len() - 769] != 0x0c {
        return Err("PCX has no 256-colour palette".into());
    }
    let palette = &data[data.len() - 768..];
    let mut pixels = Vec::with_capacity(width as usize * height as usize);
    let mut cursor = 128;
    for _ in 0..height {
        let mut row = Vec::new();
        while row.len() < bytes_per_line {
            let value = *data.get(cursor).ok_or("truncated PCX image")?;
            cursor += 1;
            if value >= 0xc0 {
                let count = (value & 0x3f) as usize;
                let pixel = *data.get(cursor).ok_or("truncated PCX run")?;
                cursor += 1;
                row.extend(std::iter::repeat(pixel).take(count));
            } else {
                row.push(value);
            }
        }
        pixels.extend_from_slice(&row[..width as usize]);
    }
    let mut rgba = Vec::with_capacity(pixels.len() * 4);
    for pixel in pixels {
        let start = pixel as usize * 3;
        rgba.extend_from_slice(&palette[start..start + 3]);
        rgba.push(255);
    }
    Ok((width, height, rgba))
}

fn write_png(path: &Path, width: u32, height: u32, rgba: &[u8]) -> Result<()> {
    let mut raw = Vec::with_capacity((width as usize * 4 + 1) * height as usize);
    for row in rgba.chunks_exact(width as usize * 4) {
        raw.push(0);
        raw.extend_from_slice(row);
    }
    let mut compressed = ZlibEncoder::new(Vec::new(), Compression::best());
    compressed.write_all(&raw)?;
    let compressed = compressed.finish()?;
    let mut png = b"\x89PNG\r\n\x1a\n".to_vec();
    png_chunk(
        &mut png,
        b"IHDR",
        &[
            (width >> 24) as u8,
            (width >> 16) as u8,
            (width >> 8) as u8,
            width as u8,
            (height >> 24) as u8,
            (height >> 16) as u8,
            (height >> 8) as u8,
            height as u8,
            8,
            6,
            0,
            0,
            0,
        ],
    );
    png_chunk(&mut png, b"IDAT", &compressed);
    png_chunk(&mut png, b"IEND", &[]);
    fs::write(path, png)?;
    Ok(())
}

fn png_chunk(png: &mut Vec<u8>, kind: &[u8; 4], data: &[u8]) {
    png.extend_from_slice(&(data.len() as u32).to_be_bytes());
    png.extend_from_slice(kind);
    png.extend_from_slice(data);
    let mut hasher = Hasher::new();
    hasher.update(kind);
    hasher.update(data);
    png.extend_from_slice(&hasher.finalize().to_be_bytes());
}
