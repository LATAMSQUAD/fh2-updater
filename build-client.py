# -*- coding: utf-8 -*-
# Arma el actualizador de cliente: 5.5.512 -> 5.6.586.
# Dentro de cada zip que el cliente ya tiene, solo viajan los archivos
# listados en update/changes.txt. Los zip nuevos se copian enteros.

import os
import struct
import subprocess
import sys
import zipfile
import zlib

ROOT = os.path.dirname(os.path.abspath(__file__))
STUB = os.path.join(ROOT, "fh2_stub.exe")
CHANGES = os.path.join(ROOT, "update", "changes.txt")
NEW = r"C:\Program Files (x86)\Forgotten Hope 2\mods\fh2"
OLD = r"C:\Program Files (x86)\Forgotten Hope 2\mods\fh2 5.5.512"
OUT = os.path.join(ROOT, "fh2_client_5.5.512_to_5.6.586.exe")
TO = "5.6.586"
FROM = "5.5.512"
MODE_FULL = 1
MODE_ZIP = 3
MAX_BYTES = 1024 * 1024 * 1024


def is_client_zip(path):
    base = path.replace("\\", "/").split("/")[-1].lower()
    if base == "server.zip" or base.endswith("_server.zip"):
        return False
    return True


def is_server_file(path):
    base = path.replace("\\", "/").split("/")[-1].lower()
    return base == "server.zip" or base.endswith("_server.zip")


def parse_changes(path):
    """Devuelve (parches, zips_nuevos). parches: zip -> lista (kind, nombre)."""
    patches = {}
    nuevos = []
    current = None
    nuevo = False
    for raw in open(path, encoding="utf-8", errors="replace"):
        line = raw.rstrip("\n")
        if line.startswith("ZIP NUEVO "):
            current = line[len("ZIP NUEVO "):].strip()
            nuevo = True
            if is_client_zip(current):
                nuevos.append(current)
            continue
        if line.startswith("ZIP ELIMINADO ") or line.startswith("ZIP "):
            if line.startswith("ZIP ELIMINADO "):
                current = None
                nuevo = False
                continue
            current = line[4:].strip()
            nuevo = False
            if is_client_zip(current):
                patches.setdefault(current, [])
            else:
                current = None
            continue
        if current is None or nuevo:
            continue
        if len(line) < 5 or line[2] not in ("M", "A", "D") or line[:2] != "  ":
            continue
        kind = line[2]
        body = line[5:]
        mark = body.rfind("  (")
        if mark < 0:
            continue
        name = body[:mark].replace("\\", "/")
        patches[current].append((kind, name))
    return patches, nuevos


def data_offset(handle, info):
    handle.seek(info.header_offset)
    local = handle.read(30)
    if local[:4] != b"PK\x03\x04":
        raise RuntimeError("cabecera zip invalida en " + info.filename)
    name_len = int.from_bytes(local[26:28], "little")
    extra_len = int.from_bytes(local[28:30], "little")
    return info.header_offset + 30 + name_len + extra_len


def dos_datetime(date_time):
    year, month, day, hour, minute, second = date_time
    if year < 1980:
        year = 1980
    dos_time = (hour << 11) | (minute << 5) | (second // 2)
    dos_date = ((year - 1980) << 9) | (month << 5) | day
    return dos_time & 0xFFFF, dos_date & 0xFFFF


def find_info(zf, name):
    want = name.replace("\\", "/")
    for info in zf.infolist():
        if info.filename.replace("\\", "/") == want:
            return info
    low = want.lower()
    for info in zf.infolist():
        if info.filename.replace("\\", "/").lower() == low:
            return info
    return None


def write_u16(buf, value):
    buf.extend(struct.pack("<H", value))


def write_u32(buf, value):
    buf.extend(struct.pack("<I", value & 0xFFFFFFFF))


def emit_member(buf, info, payload):
    name = info.filename.replace("\\", "/").encode("utf-8")
    if len(name) > 1024:
        raise RuntimeError("nombre interno demasiado largo: " + info.filename)
    dos_time, dos_date = dos_datetime(info.date_time)
    buf.append(1)
    write_u16(buf, len(name))
    buf.extend(name)
    write_u16(buf, info.compress_type)
    write_u16(buf, info.flag_bits)
    write_u16(buf, dos_time)
    write_u16(buf, dos_date)
    write_u32(buf, info.CRC)
    write_u32(buf, info.compress_size)
    write_u32(buf, info.file_size)
    if len(payload) != info.compress_size:
        raise RuntimeError("tamano comprimido distinto en " + info.filename)
    buf.extend(payload)


def emit_delete(buf, name):
    raw = name.replace("\\", "/").encode("utf-8")
    buf.append(2)
    write_u16(buf, len(raw))
    buf.extend(raw)


def build_zip_blob(zip_rel, ops):
    path = os.path.join(NEW, zip_rel.replace("/", os.sep))
    if not os.path.isfile(path):
        raise RuntimeError("falta el zip nuevo " + zip_rel)
    buf = bytearray()
    write_u32(buf, len(ops))
    with zipfile.ZipFile(path) as zf, open(path, "rb") as handle:
        for kind, name in ops:
            if kind == "D":
                emit_delete(buf, name)
                continue
            info = find_info(zf, name)
            if info is None:
                raise RuntimeError("no esta en el zip nuevo: " + zip_rel + " :: " + name)
            handle.seek(data_offset(handle, info))
            payload = handle.read(info.compress_size)
            emit_member(buf, info, payload)
    return bytes(buf)


def crc_bytes(data):
    return zlib.crc32(data) & 0xFFFFFFFF


def write_str(buf, text):
    raw = text.encode("utf-8")
    buf.extend(struct.pack("<I", len(raw)))
    buf.extend(raw)


def emit_full(dest, path):
    size = os.path.getsize(path)
    crc = 0
    with open(path, "rb") as src:
        while True:
            chunk = src.read(8 * 1024 * 1024)
            if not chunk:
                break
            dest.write(chunk)
            crc = zlib.crc32(chunk, crc)
    return size, size, crc & 0xFFFFFFFF


def loose_files_for_new_maps(nuevos):
    maps = []
    for rel in nuevos:
        parts = rel.replace("\\", "/").split("/")
        if len(parts) >= 2 and parts[0].lower() == "levels":
            maps.append(parts[1])
    found = []
    seen = set()
    for map_name in maps:
        base = os.path.join(NEW, "levels", map_name)
        if not os.path.isdir(base):
            continue
        for dirpath, _dirs, files in os.walk(base):
            for filename in files:
                full = os.path.join(dirpath, filename)
                rel = os.path.relpath(full, NEW)
                if is_server_file(rel) or rel.replace("\\", "/").lower() in seen:
                    continue
                # El zip nuevo ya se copia entero en otro paso.
                if rel.replace("\\", "/").lower() in [n.lower() for n in nuevos]:
                    continue
                seen.add(rel.replace("\\", "/").lower())
                found.append(rel)
    for extra in ("mod.desc", "changelog.txt"):
        if os.path.isfile(os.path.join(NEW, extra)):
            found.append(extra)
    return found


def finish(dest, records, deletes):
    index_off = dest.tell()
    index = bytearray()
    write_str(index, TO)
    write_str(index, FROM)
    index.extend(struct.pack("<I", len(records)))
    for rel, mode, out_size, offset, blob_len, crc in records:
        write_str(index, rel.replace("/", "\\"))
        index.extend(struct.pack("<i", mode))
        index.extend(struct.pack("<q", out_size))
        index.extend(struct.pack("<q", offset))
        index.extend(struct.pack("<q", blob_len))
        index.extend(struct.pack("<I", crc))
    index.extend(struct.pack("<I", len(deletes)))
    for rel in deletes:
        write_str(index, rel)
    dest.write(index)
    dest.write(b"FH2UPD02")
    dest.write(struct.pack("<q", index_off))
    dest.write(struct.pack("<q", len(index)))


def build_all():
    if not os.path.isfile(STUB):
        raise RuntimeError("falta fh2_stub.exe")
    patches, nuevos = parse_changes(CHANGES)
    if os.path.exists(OUT):
        os.remove(OUT)
    import shutil
    shutil.copyfile(STUB, OUT)
    records = []
    carried = 0
    with open(OUT, "r+b") as dest:
        dest.seek(0, os.SEEK_END)
        n = 0
        total = len(patches) + len(nuevos)
        for rel, ops in sorted(patches.items()):
            n += 1
            blob = build_zip_blob(rel, ops)
            offset = dest.tell()
            dest.write(blob)
            records.append((rel, MODE_ZIP, 0, offset, len(blob), crc_bytes(blob)))
            carried += len(blob)
            if n == 1 or n % 10 == 0 or n == len(patches):
                print("ZIP", n, "/", len(patches), "MB", round(carried / 1024 / 1024, 1), rel, flush=True)
        for rel in nuevos:
            path = os.path.join(NEW, rel.replace("/", os.sep))
            if not os.path.isfile(path):
                raise RuntimeError("falta zip nuevo " + rel)
            offset = dest.tell()
            blob_len, out_size, crc = emit_full(dest, path)
            records.append((rel, MODE_FULL, out_size, offset, blob_len, crc))
            carried += blob_len
            print("NUEVO", rel, "MB", round(blob_len / 1024 / 1024, 1), flush=True)
        for rel in loose_files_for_new_maps(nuevos):
            path = os.path.join(NEW, rel)
            offset = dest.tell()
            blob_len, out_size, crc = emit_full(dest, path)
            records.append((rel, MODE_FULL, out_size, offset, blob_len, crc))
            carried += blob_len
        finish(dest, records, [])
    size = os.path.getsize(OUT)
    if size >= MAX_BYTES:
        os.remove(OUT)
        raise RuntimeError("el exe pesa %s bytes, mas de 1 GB" % size)
    print("WROTE", OUT, "MB", round(size / 1024 / 1024, 1), "files", len(records), flush=True)


def roundtrip():
    """Prueba el formato contra el exe con un zip pequeno."""
    import shutil
    import tempfile
    patches, _nuevos = parse_changes(CHANGES)
    sample = None
    for rel, ops in patches.items():
        puts = [op for op in ops if op[0] != "D"]
        if not puts:
            continue
        path = os.path.join(NEW, rel.replace("/", os.sep))
        if os.path.isfile(path) and os.path.getsize(path) < 40 * 1024 * 1024:
            sample = (rel, ops, puts[0][1])
            break
    if sample is None:
        raise RuntimeError("no hay un zip chico para probar")
    rel, ops, member = sample
    base = tempfile.mkdtemp(prefix="fh2-client-")
    try:
        packed = os.path.join(base, "mini.exe")
        shutil.copyfile(STUB, packed)
        dest_root = os.path.join(base, "mods")
        os.makedirs(os.path.dirname(os.path.join(dest_root, rel.replace("/", os.sep))))
        old_path = os.path.join(OLD, rel.replace("/", os.sep))
        shutil.copyfile(old_path, os.path.join(dest_root, rel.replace("/", os.sep)))
        blob = build_zip_blob(rel, ops)
        with open(packed, "r+b") as dest:
            dest.seek(0, os.SEEK_END)
            offset = dest.tell()
            dest.write(blob)
            finish(dest, [(rel, MODE_ZIP, 0, offset, len(blob), crc_bytes(blob))], [])
        code = subprocess.call([packed, "--aplicar", dest_root])
        if code != 0:
            raise RuntimeError("aplicar devolvio " + str(code))
        new_zip = os.path.join(NEW, rel.replace("/", os.sep))
        got_zip = os.path.join(dest_root, rel.replace("/", os.sep))
        with zipfile.ZipFile(new_zip) as expect, zipfile.ZipFile(got_zip) as got:
            info = find_info(expect, member)
            left = expect.read(info)
            right = got.read(find_info(got, member))
        if left != right:
            raise RuntimeError("el archivo interno no coincidio: " + member)
        print("ROUNDTRIP_OK", rel, member, flush=True)
    finally:
        shutil.rmtree(base, ignore_errors=True)


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "test":
        roundtrip()
    else:
        roundtrip()
        build_all()
