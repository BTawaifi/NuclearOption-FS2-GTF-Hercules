"""Extract the Hercules visual from a user-owned FreeSpace 2 installation.

This deliberately produces only the runtime assets needed by FS2Hercules.
It does not copy a VP archive, POF, or PCX into the project or release output.
"""
import argparse
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib
from pathlib import Path

from vp import entries


MODEL_NAME = "data/models/fighter06.pof"
TEXTURE_NAME = "data/maps/fighter06-01a.pcx"


def find_vp_files(freespace):
    root = Path(freespace)
    if root.is_file() and root.suffix.lower() == ".vp":
        return [root]
    if not root.is_dir():
        raise SystemExit("FreeSpace 2 path does not exist: " + str(root))
    archives = list(root.rglob("*.vp"))
    if not archives:
        raise SystemExit("No .vp archives found under " + str(root))
    return sorted(archives, key=lambda path: (path.name.lower() != "sparky_fs2.vp", str(path).lower()))


def find_entry(archives, wanted):
    wanted = wanted.lower()
    for archive in archives:
        for name, offset, size in entries(str(archive)):
            if name.lower() == wanted:
                return archive, name, offset, size
    raise SystemExit("Could not find {} in the FreeSpace 2 VP archives".format(wanted))


def extract_entry(entry, destination):
    archive, name, offset, size = entry
    with open(archive, "rb") as source:
        source.seek(offset)
        data = source.read(size)
    destination.write_bytes(data)
    print("read {} from {} ({} bytes)".format(name, archive.name, size))


def decode_pcx(path):
    data = path.read_bytes()
    if len(data) < 128 + 769 or data[0] != 0x0A or data[2] != 1 or data[3] != 8:
        raise SystemExit("Unsupported PCX format: " + str(path))
    x_min, y_min, x_max, y_max = struct.unpack_from("<HHHH", data, 4)
    width = x_max - x_min + 1
    height = y_max - y_min + 1
    planes = data[65]
    bytes_per_line = struct.unpack_from("<H", data, 66)[0]
    if planes != 1:
        raise SystemExit("Expected an indexed single-plane PCX: " + str(path))

    pixels = bytearray()
    cursor = 128
    for _ in range(height):
        row = bytearray()
        while len(row) < bytes_per_line:
            if cursor >= len(data):
                raise SystemExit("Truncated PCX image: " + str(path))
            value = data[cursor]
            cursor += 1
            if value >= 0xC0:
                count = value & 0x3F
                if cursor >= len(data):
                    raise SystemExit("Truncated PCX run: " + str(path))
                row.extend([data[cursor]] * count)
                cursor += 1
            else:
                row.append(value)
        pixels.extend(row[:width])

    if data[-769] != 0x0C:
        raise SystemExit("PCX has no 256-colour palette: " + str(path))
    palette = data[-768:]
    rgba = bytearray(width * height * 4)
    for index, colour in enumerate(pixels):
        palette_offset = colour * 3
        output_offset = index * 4
        rgba[output_offset : output_offset + 3] = palette[palette_offset : palette_offset + 3]
        rgba[output_offset + 3] = 255
    return width, height, bytes(rgba)


def write_png(path, width, height, rgba):
    def chunk(kind, payload):
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
        )

    scanlines = b"".join(
        b"\x00" + rgba[row * width * 4 : (row + 1) * width * 4]
        for row in range(height)
    )
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(scanlines, 9))
    png += chunk(b"IEND", b"")
    path.write_bytes(png)


def run_converter(args, pof, glb, project_root, temp_dir):
    if args.converter:
        command = [str(args.converter), str(pof), str(glb)]
    else:
        pof_tools = Path(args.pof_tools) if args.pof_tools else Path("D:/Games/FreespaceOpen/tools/pof-tools-src")
        pof_crate = pof_tools / "pof"
        if not (pof_crate / "Cargo.toml").is_file():
            raise SystemExit(
                "pof-tools was not found. Pass --pof-tools PATH or use --converter PATH."
            )
        cargo_project = temp_dir / "pof2glb-runner"
        (cargo_project / "src").mkdir(parents=True)
        shutil.copy2(project_root / "tools" / "pof2glb" / "src" / "main.rs", cargo_project / "src" / "main.rs")
        cargo_toml = (
            "[package]\n"
            "name = \"pof2glb-runner\"\n"
            "version = \"0.1.0\"\n"
            "edition = \"2021\"\n\n"
            "[dependencies]\n"
            "pof = { path = '" + pof_crate.resolve().as_posix() + "' }\n"
        )
        (cargo_project / "Cargo.toml").write_text(cargo_toml, encoding="utf-8")
        command = [
            args.cargo,
            "run",
            "--quiet",
            "--manifest-path",
            str(cargo_project / "Cargo.toml"),
            "--",
            str(pof),
            str(glb),
        ]
    print("converting POF to glTF")
    subprocess.run(command, check=True, cwd=project_root)


def main():
    project_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(
        description="Generate FS2Hercules assets from a user-owned FreeSpace 2 installation."
    )
    parser.add_argument("--freespace", required=True, help="FreeSpace 2 install directory or VP archive")
    parser.add_argument("--output", default=str(project_root / "assets"), help="Generated asset directory")
    parser.add_argument("--pof-tools", help="Directory containing the pof-tools-src checkout")
    parser.add_argument("--converter", type=Path, help="Existing pof2glb executable, instead of Cargo")
    parser.add_argument("--cargo", default="cargo", help="Cargo executable")
    args = parser.parse_args()

    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    archives = find_vp_files(args.freespace)
    model = find_entry(archives, MODEL_NAME)
    texture = find_entry(archives, TEXTURE_NAME)

    with tempfile.TemporaryDirectory(prefix="fs2hercules-") as temporary:
        temp_dir = Path(temporary)
        pof = temp_dir / "fighter06.POF"
        pcx = temp_dir / "fighter06-01a.pcx"
        glb = temp_dir / "fighter06.glb"
        extract_entry(model, pof)
        extract_entry(texture, pcx)
        run_converter(args, pof, glb, project_root, temp_dir)

        converter = project_root / "tools" / "glb2mesh.py"
        subprocess.run(
            [
                sys.executable,
                str(converter),
                str(glb),
                str(output / "hercules.nomesh"),
                "fighter06a",
                "--material",
                "fighter06-01a",
                "--output-material",
                "HercPBR",
            ],
            check=True,
            cwd=project_root,
        )

        width, height, rgba = decode_pcx(pcx)
        write_png(output / "HercPBR.png", width, height, rgba)
        write_png(output / "HercPBR-normal.png", width, height, bytes([128, 128, 255, 255]) * (width * height))
        write_png(output / "HercPBR-glow.png", width, height, bytes([0, 0, 0, 255]) * (width * height))

    print("generated:")
    for path in sorted(output.iterdir()):
        if path.is_file():
            print("  " + str(path))
    print("original VP/POF/PCX data was read temporarily and was not copied to the output")


if __name__ == "__main__":
    main()
