# glb (from pof2glb) -> simple binary for the Unity plugin.
# Format: int32 submeshCount; per submesh: str material, int32 vcount,
# vcount*(pos3 nrm3 uv2) float32, int32 icount, icount*int32.
import argparse
import json
import struct
import sys

import numpy as np


def read_accessor(document, binary, index):
    accessor = document["accessors"][index]
    buffer_view = document["bufferViews"][accessor["bufferView"]]
    components = {"SCALAR": 1, "VEC2": 2, "VEC3": 3}[accessor["type"]]
    data_type = {5126: np.float32, 5125: np.uint32, 5123: np.uint16}[accessor["componentType"]]
    offset = buffer_view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    item_size = np.dtype(data_type).itemsize
    stride = buffer_view.get("byteStride", item_size * components)
    array = np.ndarray(
        (accessor["count"], components),
        data_type,
        binary,
        offset,
        (stride, item_size),
    )
    return array.copy() if components > 1 else array[:, 0].copy()


def main():
    parser = argparse.ArgumentParser(description="Convert selected glTF mesh data to the FS2Hercules runtime format.")
    parser.add_argument("src", help="Input GLB")
    parser.add_argument("dst", help="Output .nomesh")
    parser.add_argument("root_name", help="glTF node to export")
    parser.add_argument(
        "--material",
        dest="materials",
        action="append",
        help="Export only this source material; repeat to include more than one",
    )
    parser.add_argument(
        "--output-material",
        default=None,
        help="Rename all selected materials in the output",
    )
    args = parser.parse_args()

    binary_file = open(args.src, "rb").read()
    json_length = struct.unpack("<I", binary_file[12:16])[0]
    document = json.loads(binary_file[20 : 20 + json_length])
    binary = binary_file[20 + json_length + 8 :]

    by_material = {}

    def walk(node_index, translation):
        node = document["nodes"][node_index]
        name = node.get("name", "")
        translation = translation + np.array(node.get("translation", [0, 0, 0]))
        if name.startswith("insignia"):
            return
        if "mesh" in node:
            for primitive in document["meshes"][node["mesh"]]["primitives"]:
                material = (
                    document["materials"][primitive["material"]]["name"]
                    if "material" in primitive
                    else "none"
                )
                if args.materials and material not in args.materials:
                    continue
                positions = read_accessor(document, binary, primitive["attributes"]["POSITION"]) + translation
                normals = read_accessor(document, binary, primitive["attributes"]["NORMAL"])
                uvs = read_accessor(document, binary, primitive["attributes"]["TEXCOORD_0"])
                indices = (
                    read_accessor(document, binary, primitive["indices"]).astype(np.int64)
                    if "indices" in primitive
                    else np.arange(len(positions))
                )
                by_material.setdefault(material, []).append((positions, normals, uvs, indices))
        for child in node.get("children", []):
            walk(child, translation)

    try:
        root = next(i for i, node in enumerate(document["nodes"]) if node.get("name") == args.root_name)
    except StopIteration:
        raise SystemExit("glTF root node not found: " + args.root_name)
    walk(root, np.zeros(3))

    if not by_material:
        wanted = ", ".join(args.materials or ["<any material>"])
        raise SystemExit("No mesh primitives matched " + wanted)

    output = [struct.pack("<i", len(by_material))]
    total_triangles = 0
    for source_material, chunks in by_material.items():
        positions = np.concatenate([chunk[0] for chunk in chunks])
        normals = np.concatenate([chunk[1] for chunk in chunks])
        uvs = np.concatenate([chunk[2] for chunk in chunks])
        base = np.cumsum([0] + [len(chunk[0]) for chunk in chunks[:-1]])
        indices = np.concatenate(
            [chunk[3] + offset for chunk, offset in zip(chunks, base)]
        ).reshape(-1, 3)[:, [0, 2, 1]].ravel()

        positions = positions * [1, 1, -1]
        normals = normals * [1, 1, -1]
        uvs = uvs * [1, -1] + [0, 1]
        vertices = np.hstack([positions, normals, uvs]).astype(np.float32)
        output_material = args.output_material or source_material
        material_bytes = output_material.encode("utf-8")
        output += [
            struct.pack("<i", len(material_bytes)),
            material_bytes,
            struct.pack("<i", len(vertices)),
            vertices.tobytes(),
            struct.pack("<i", len(indices)),
            indices.astype(np.int32).tobytes(),
        ]
        total_triangles += len(indices) // 3
        print(
            "{} -> {}: verts={} tris={} min={} max={}".format(
                source_material,
                output_material,
                len(vertices),
                len(indices) // 3,
                positions.min(0).round(2),
                positions.max(0).round(2),
            )
        )

    with open(args.dst, "wb") as output_file:
        output_file.write(b"".join(output))
    print("tris {} bytes {}".format(total_triangles, sum(map(len, output))))


if __name__ == "__main__":
    main()
