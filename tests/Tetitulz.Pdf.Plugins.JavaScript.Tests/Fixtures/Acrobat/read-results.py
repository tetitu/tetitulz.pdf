"""Reads the text fields out of a probe document Acrobat saved — the "results" field holds the probe's
answers — without any PDF library, so the answers TSV can be made (and checked) from the file alone.

    python read-results.py AcrobatProbe2.pdf                      # every text field's value
    python read-results.py AcrobatProbe2.pdf --field results      # one field's value, as saved
    python read-results.py AcrobatProbe2.pdf --compare answers2-26.2.tsv   # the TSV against the field

Acrobat writes the field dictionaries into object streams, so the file is inflated and every object,
direct or in a stream, is searched for a text field (/FT /Tx, or a /Parent that is one) with a /T and
a /V; the last definition of a field in file order wins, as the newest revision is appended last.
Run by hand; no test runs it.
"""
import re
import sys
import zlib

_WS = rb"[\x00\t\n\x0c\r ]"


def _dict_end(data, start):
    """The index just past the dictionary opening at data[start:start+2] == b'<<'."""
    depth, i, n = 0, start, len(data)
    while i < n:
        if data[i:i + 2] == b"<<":
            depth += 1
            i += 2
        elif data[i:i + 2] == b">>":
            depth -= 1
            i += 2
            if depth == 0:
                return i
        elif data[i:i + 1] == b"(":
            i = _literal_end(data, i)
        elif data[i:i + 1] == b"<":
            j = data.find(b">", i + 1)
            i = n if j < 0 else j + 1
        else:
            i += 1
    return n


def _literal_end(data, start):
    """The index just past the literal string opening at data[start] == b'('."""
    depth, i, n = 0, start, len(data)
    while i < n:
        c = data[i:i + 1]
        if c == b"\\":
            i += 2
            continue
        if c == b"(":
            depth += 1
        elif c == b")":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return n


def _unescape(literal):
    """A literal string's bytes (§7.3.4.2), the parentheses stripped."""
    body = literal[1:-1]
    out = bytearray()
    i, n = 0, len(body)
    while i < n:
        c = body[i:i + 1]
        if c != b"\\":
            out += c
            i += 1
            continue
        i += 1
        if i >= n:
            break
        e = body[i:i + 1]
        simple = {b"n": b"\n", b"r": b"\r", b"t": b"\t", b"b": b"\b", b"f": b"\f", b"(": b"(", b")": b")", b"\\": b"\\"}
        if e in simple:
            out += simple[e]
            i += 1
        elif e in b"01234567":
            j = i
            while j < n and j - i < 3 and body[j:j + 1] in b"01234567":
                j += 1
            out.append(int(body[i:j], 8) & 0xFF)
            i = j
        elif e == b"\r":
            i += 2 if body[i:i + 2] == b"\r\n" else 1
        elif e == b"\n":
            i += 1
        else:
            out += e
            i += 1
    return bytes(out)


def _text(string_bytes):
    """A text string's characters: UTF-16BE behind a byte-order mark, else PDFDocEncoding read as Latin-1."""
    if string_bytes[:2] == b"\xfe\xff":
        return string_bytes[2:].decode("utf-16-be", errors="replace")
    return string_bytes.decode("latin-1")


def _string_value(data, i):
    """The string object starting at data[i] ('(' or '<'), as text; None when there is none."""
    c = data[i:i + 1]
    if c == b"(":
        return _text(_unescape(data[i:_literal_end(data, i)]))
    if c == b"<" and data[i:i + 2] != b"<<":
        j = data.find(b">", i + 1)
        hexdigits = re.sub(_WS, b"", data[i + 1:j])
        if len(hexdigits) % 2:
            hexdigits += b"0"
        return _text(bytes.fromhex(hexdigits.decode("ascii")))
    return None


def _objects(data):
    """Every (number, body) in file order: direct objects, then each object stream's members in its place."""
    found = []
    for m in re.finditer(rb"(?<![0-9])(\d+)" + _WS + rb"+(\d+)" + _WS + rb"+obj\b", data):
        num = int(m.group(1))
        start = m.end()
        while data[start:start + 1] in (b" ", b"\r", b"\n", b"\t"):
            start += 1
        if data[start:start + 2] != b"<<":
            end = data.find(b"endobj", start)
            found.append((num, data[start:end if end >= 0 else len(data)]))
            continue
        end = _dict_end(data, start)
        body = data[start:end]
        found.append((num, body))
        tail = data[end:end + 20]
        if not re.match(_WS + rb"*stream", tail):
            continue
        if b"/ObjStm" not in body:
            continue
        length = re.search(rb"/Length" + _WS + rb"+(\d+)(?!" + _WS + rb"+\d+" + _WS + rb"+R)", body)
        s = data.find(b"stream", end) + len(b"stream")
        if data[s:s + 2] == b"\r\n":
            s += 2
        elif data[s:s + 1] in (b"\n", b"\r"):
            s += 1
        raw = data[s:s + int(length.group(1))] if length else data[s:data.find(b"endstream", s)]
        try:
            inflated = zlib.decompress(raw) if b"/FlateDecode" in body else raw
        except zlib.error:
            continue
        n = int(re.search(rb"/N" + _WS + rb"+(\d+)", body).group(1))
        first = int(re.search(rb"/First" + _WS + rb"+(\d+)", body).group(1))
        header = inflated[:first].split()
        pairs = [(int(header[k]), int(header[k + 1])) for k in range(0, 2 * n, 2)]
        for k, (onum, off) in enumerate(pairs):
            o_start = first + off
            o_end = first + pairs[k + 1][1] if k + 1 < len(pairs) else len(inflated)
            found.append((onum, inflated[o_start:o_end].strip()))
    return found


def text_fields(data):
    """{fully qualified name: value} of every text field the file defines, the last definition winning."""
    objects = _objects(data)
    by_number = {}
    for num, body in objects:
        by_number[num] = body  # the last definition in file order
    fields = {}

    def entry(body, key):
        m = re.search(rb"/" + key + rb"(?=" + _WS + rb"|[(<\[/])" + _WS + rb"*", body)
        return None if m is None else m.end()

    def resolve_string(body, key):
        i = entry(body, key)
        if i is None:
            return None
        ref = re.match(rb"(\d+)" + _WS + rb"+\d+" + _WS + rb"+R", body[i:])
        if ref:
            target = by_number.get(int(ref.group(1)))
            return None if target is None else _string_value(target, 0)
        return _string_value(body, i)

    def field_type(body, depth=0):
        m = re.search(rb"/FT" + _WS + rb"*/(\w+)", body)
        if m:
            return m.group(1)
        p = re.search(rb"/Parent" + _WS + rb"+(\d+)" + _WS + rb"+\d+" + _WS + rb"+R", body)
        if p and depth < 32 and int(p.group(1)) in by_number:
            return field_type(by_number[int(p.group(1))], depth + 1)
        return None

    def full_name(body, depth=0):
        t = resolve_string(body, b"T")
        p = re.search(rb"/Parent" + _WS + rb"+(\d+)" + _WS + rb"+\d+" + _WS + rb"+R", body)
        parent = full_name(by_number[int(p.group(1))], depth + 1) if p and depth < 32 and int(p.group(1)) in by_number else None
        if t is None:
            return parent
        return t if not parent else parent + "." + t

    for num, body in objects:
        if body is not by_number.get(num):
            continue  # superseded by a later definition
        if entry(body, b"T") is None or field_type(body) != b"Tx":
            continue
        name = full_name(body)
        if name is None:
            continue
        value = resolve_string(body, b"V")
        fields[name] = "" if value is None else value
    return fields


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2
    data = open(argv[1], "rb").read()
    fields = text_fields(data)
    if "--compare" in argv:
        tsv = open(argv[argv.index("--compare") + 1], "rb").read().decode("utf-8").replace("\r\n", "\n").rstrip("\n").split("\n")
        saved = fields.get("results", "").replace("\r\n", "\n").replace("\r", "\n").rstrip("\n").split("\n")
        print("rows in the TSV: %d; lines in the saved field: %d" % (len(tsv), len(saved)))
        differing = [(k + 1, a, b) for k, (a, b) in enumerate(zip(tsv, saved)) if a != b]
        if len(tsv) != len(saved) or differing:
            for k, a, b in differing[:10]:
                print("line %d differs:\n  TSV:   %r\n  field: %r" % (k, a, b))
            if len(tsv) != len(saved):
                extra = tsv[len(saved):] if len(tsv) > len(saved) else saved[len(tsv):]
                print("only in the %s: %r" % ("TSV" if len(tsv) > len(saved) else "field", extra[:5]))
            print("DIFFERENT")
            return 1
        print("IDENTICAL")
        return 0
    if "--field" in argv:
        sys.stdout.write(fields.get(argv[argv.index("--field") + 1], ""))
        return 0
    for name in sorted(fields):
        value = fields[name]
        shown = value if len(value) <= 80 else value[:77] + "..."
        print("%s\t%d chars\t%r" % (name, len(value), shown))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
