import os, struct, sys, zlib, shutil, hashlib, zipfile

ROOT = os.path.dirname(os.path.abspath(__file__))
STUB = os.path.join(ROOT, "fh2_stub.exe")
# Archivos nuevos que el usuario dejo en update\. La version anterior queda aparte.
SRC = os.path.join(ROOT, "update")
OLD = r"C:\Program Files (x86)\Forgotten Hope 2\mods\fh2 5.6.507"
LIST = os.path.join(ROOT, "copy-list.txt")
DEL = os.path.join(ROOT, "delete-list.txt")
OUT = os.path.join(ROOT, "fh2_update_5.6.507_to_5.6.586.exe")
TO = "5.6.586"
FROM = "5.6.507"
MODE_FULL = 1
MODE_REBUILD = 2

def crc_file(path):
    c = 0
    with open(path, "rb") as f:
        while True:
            chunk = f.read(8 * 1024 * 1024)
            if not chunk:
                break
            c = zlib.crc32(chunk, c)
    return c & 0xFFFFFFFF

def data_offset(f, info):
    f.seek(info.header_offset)
    local = f.read(30)
    if local[:4] != b"PK\x03\x04":
        raise RuntimeError("cabecera zip invalida en " + info.filename)
    name_len = int.from_bytes(local[26:28], "little")
    extra_len = int.from_bytes(local[28:30], "little")
    return info.header_offset + 30 + name_len + extra_len

def same_range(a, a_off, b, b_off, length):
    left = length
    a.seek(a_off)
    b.seek(b_off)
    while left:
        n = min(1024 * 1024, left)
        ca = a.read(n)
        cb = b.read(n)
        if ca != cb or not ca:
            return False
        left -= len(ca)
    return True

def stream_copy(src, dest, length, crc):
    left = length
    while left:
        chunk = src.read(min(8 * 1024 * 1024, left))
        if not chunk:
            raise RuntimeError("lectura corta")
        dest.write(chunk)
        crc = zlib.crc32(chunk, crc)
        left -= len(chunk)
    return crc & 0xFFFFFFFF

def write_literal(dest, src, length, crc):
    dest.write(bytes([1]))
    dest.write(struct.pack("<q", length))
    dest.write(struct.pack("<q", 0))
    return stream_copy(src, dest, length, crc), 1

def find_spans(new_path, old_path):
    spans = []
    with zipfile.ZipFile(new_path) as nz, zipfile.ZipFile(old_path) as oz, \
            open(new_path, "rb") as nf, open(old_path, "rb") as of:
        old_map = {i.filename: i for i in oz.infolist()}
        for info in nz.infolist():
            if info.compress_size <= 0:
                continue
            prev = old_map.get(info.filename)
            if prev is None:
                continue
            if prev.CRC != info.CRC or prev.compress_size != info.compress_size or prev.file_size != info.file_size:
                continue
            new_off = data_offset(nf, info)
            old_off = data_offset(of, prev)
            if not same_range(nf, new_off, of, old_off, info.compress_size):
                continue
            spans.append((new_off, old_off, info.compress_size))
    spans.sort()
    cleaned = []
    pos = 0
    for new_off, old_off, length in spans:
        if new_off < pos or length <= 0:
            continue
        cleaned.append((new_off, old_off, length))
        pos = new_off + length
    return cleaned

def emit_rebuild(dest, new_path, old_path, spans, new_size, crc_new):
    blob_start = dest.tell()
    dest.write(struct.pack("<I", 0))
    ops = 0
    crc = 0
    pos = 0
    with open(new_path, "rb") as nf, open(old_path, "rb") as of:
        for new_off, old_off, length in spans:
            gap = new_off - pos
            if gap > 0:
                nf.seek(pos)
                crc, added = write_literal(dest, nf, gap, crc)
                ops += added
            of.seek(old_off)
            nf.seek(new_off)
            left = length
            ok = True
            # verify and crc without writing the bytes into the package
            while left:
                n = min(1024 * 1024, left)
                a = of.read(n)
                b = nf.read(n)
                if a != b or not a:
                    ok = False
                    break
                crc = zlib.crc32(a, crc)
                left -= len(a)
            if not ok:
                raise RuntimeError("el trozo copiado no coincide")
            dest.write(bytes([2]))
            dest.write(struct.pack("<q", old_off))
            dest.write(struct.pack("<q", length))
            ops += 1
            pos = new_off + length
        tail = new_size - pos
        if tail > 0:
            nf.seek(pos)
            crc, added = write_literal(dest, nf, tail, crc)
            ops += added
    crc &= 0xFFFFFFFF
    if crc != crc_new:
        raise RuntimeError("crc de reconstruccion distinta")
    end = dest.tell()
    dest.seek(blob_start)
    dest.write(struct.pack("<I", ops))
    dest.seek(end)
    return end - blob_start, crc

def emit_full(dest, path):
    blob_start = dest.tell()
    size = os.path.getsize(path)
    crc = 0
    with open(path, "rb") as src:
        while True:
            chunk = src.read(8 * 1024 * 1024)
            if not chunk:
                break
            dest.write(chunk)
            crc = zlib.crc32(chunk, crc)
    return dest.tell() - blob_start, size, crc & 0xFFFFFFFF

def write_str(buf, text):
    data = text.encode("utf-8")
    buf.extend(struct.pack("<I", len(data)))
    buf.extend(data)

def build_one_for_test(rel):
    base = os.path.join(os.environ["TEMP"], "fh2-one-zip")
    if os.path.isdir(base):
        shutil.rmtree(base, ignore_errors=True)
    os.makedirs(base)
    packed = os.path.join(base, "one.exe")
    shutil.copyfile(STUB, packed)
    new_path = os.path.join(SRC, rel)
    old_path = os.path.join(OLD, rel)
    spans = find_spans(new_path, old_path)
    crc_new = crc_file(new_path)
    new_size = os.path.getsize(old_path and new_path)
    with open(packed, "r+b") as dest:
        dest.seek(0, os.SEEK_END)
        offset = dest.tell()
        blob_len, crc = emit_rebuild(dest, new_path, old_path, spans, os.path.getsize(new_path), crc_new)
        index_off = dest.tell()
        index = bytearray()
        write_str(index, TO)
        write_str(index, FROM)
        index.extend(struct.pack("<I", 1))
        write_str(index, rel)
        index.extend(struct.pack("<i", MODE_REBUILD))
        index.extend(struct.pack("<q", os.path.getsize(new_path)))
        index.extend(struct.pack("<q", offset))
        index.extend(struct.pack("<q", blob_len))
        index.extend(struct.pack("<I", crc))
        index.extend(struct.pack("<I", 0))
        dest.write(index)
        dest.write(b"FH2UPD02")
        dest.write(struct.pack("<q", index_off))
        dest.write(struct.pack("<q", len(index)))
    saved = os.path.getsize(new_path) - blob_len
    print("TEST_PACK", rel, "zip", os.path.getsize(new_path), "blob", blob_len, "spans", len(spans), "saved", saved, flush=True)
    # apply with the real exe into a folder that has the old zip
    dest_root = os.path.join(base, "dest")
    os.makedirs(os.path.dirname(os.path.join(dest_root, rel)))
    shutil.copyfile(old_path, os.path.join(dest_root, rel))
    open(os.path.join(dest_root, "init.con"), "w").close()
    open(os.path.join(dest_root, "mod.desc"), "w").close()
    report = os.path.join(base, "apply.txt")
    # no CLI apply of one file; reproduce apply in python and compare hashes
    out = os.path.join(base, "out.zip")
    apply_python(packed, offset, blob_len, old_path, out)
    h1 = hashlib.md5(open(new_path, "rb").read()).hexdigest()
    h2 = hashlib.md5(open(out, "rb").read()).hexdigest()
    print("TEST_MATCH", h1 == h2, h1, h2, flush=True)
    return h1 == h2

def apply_python(packed, offset, blob_len, old_path, out_path):
    pack = open(packed, "rb")
    pack.seek(offset)
    opcount = struct.unpack("<I", pack.read(4))[0]
    old = open(old_path, "rb")
    out = open(out_path, "wb")
    for _ in range(opcount):
        kind = pack.read(1)[0]
        a, b = struct.unpack("<qq", pack.read(16))
        if kind == 1:
            left = a
            while left:
                chunk = pack.read(min(8 * 1024 * 1024, left))
                out.write(chunk)
                left -= len(chunk)
        elif kind == 2:
            old.seek(a)
            left = b
            while left:
                chunk = old.read(min(8 * 1024 * 1024, left))
                out.write(chunk)
                left -= len(chunk)
        else:
            raise RuntimeError("kind")
    pack.close()
    old.close()
    out.close()

def build_all():
    rels = [ln.strip() for ln in open(LIST, encoding="utf-8") if ln.strip()]
    deletes = [ln.strip() for ln in open(DEL, encoding="utf-8") if ln.strip()]
    if os.path.exists(OUT):
        os.remove(OUT)
    shutil.copyfile(STUB, OUT)
    records = []
    carried = 0
    with open(OUT, "r+b") as dest:
        dest.seek(0, os.SEEK_END)
        for n, rel in enumerate(rels, 1):
            new_path = os.path.join(SRC, rel)
            old_path = os.path.join(OLD, rel)
            if not os.path.isfile(new_path):
                raise RuntimeError("falta " + rel)
            offset = dest.tell()
            use_rebuild = rel.lower().endswith(".zip") and os.path.isfile(old_path)
            mode = MODE_FULL
            if use_rebuild:
                try:
                    spans = find_spans(new_path, old_path)
                    crc_new = crc_file(new_path)
                    new_size = os.path.getsize(new_path)
                    blob_len, crc = emit_rebuild(dest, new_path, old_path, spans, new_size, crc_new)
                    if blob_len < new_size:
                        mode = MODE_REBUILD
                        out_size = new_size
                    else:
                        dest.seek(offset)
                        dest.truncate()
                        blob_len, out_size, crc = emit_full(dest, new_path)
                except Exception as ex:
                    print("FALLBACK", rel, ex, flush=True)
                    dest.seek(offset)
                    dest.truncate()
                    blob_len, out_size, crc = emit_full(dest, new_path)
            else:
                blob_len, out_size, crc = emit_full(dest, new_path)
            records.append((rel, mode, out_size, offset, blob_len, crc))
            carried += blob_len
            if n == 1 or n % 20 == 0 or n == len(rels):
                print("FILE", n, "/", len(rels), "MB", round(carried / 1024 / 1024, 1), rel, flush=True)
        index_off = dest.tell()
        index = bytearray()
        write_str(index, TO)
        write_str(index, FROM)
        index.extend(struct.pack("<I", len(records)))
        for rel, mode, out_size, offset, blob_len, crc in records:
            write_str(index, rel)
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
    print("WROTE", OUT, os.path.getsize(OUT), "carried", carried, "rebuilds", sum(1 for r in records if r[1] == MODE_REBUILD), flush=True)

if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "full":
        build_all()
    else:
        rel = r"levels\bure\server.zip"
        ok = build_one_for_test(rel)
        sys.exit(0 if ok else 1)
