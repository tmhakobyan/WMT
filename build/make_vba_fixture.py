"""Generates synthetic VBA test fixtures (no real user data, no Office needed).

Produces:
  tests/WMT.Tests/Fixtures/synthetic.dotm      OOXML template with word/vbaProject.bin + an ActiveX part
  tests/WMT.Tests/Fixtures/vbaProject.bin      the raw compound file

The VBA project contains one module with Declare statements (with and without PtrSafe),
CreateObject, a hard-coded external path, #If VBA7, and two registered references.
Run:  python build/make_vba_fixture.py
"""
import io, os, struct, sys, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "tests", "WMT.Tests", "Fixtures")

MODULE_SOURCE = (
    'Attribute VB_Name = "HyphenTools"\r\n'
    '#If VBA7 Then\r\n'
    'Private Declare PtrSafe Function GetTickCount Lib "kernel32" () As Long\r\n'
    '#Else\r\n'
    'Private Declare Function GetTickCount Lib "kernel32" () As Long\r\n'
    '#End If\r\n'
    'Private Declare Function HyphenateWord Lib "ArmHyph.dll" (ByVal s As String) As Long\r\n'
    'Sub Hyphenate()\r\n'
    '    Dim d As Object\r\n'
    '    Set d = CreateObject("Scripting.Dictionary")\r\n'
    '    Open "C:\\Tools\\Hyphen\\rules-hy.txt" For Input As #1\r\n'
    '    Close #1\r\n'
    'End Sub\r\n'
)


def compress(data: bytes) -> bytes:
    """MS-OVBA compressed container: raw chunks for full 4096 blocks, literal-only chunk for the tail."""
    out = bytearray(b"\x01")
    for i in range(0, len(data), 4096):
        block = data[i:i + 4096]
        if len(block) == 4096:
            out += struct.pack("<H", 0x3FFF) + block
            continue
        assert len(block) <= 3640
        body = bytearray()
        for j in range(0, len(block), 8):
            body.append(0)
            body += block[j:j + 8]
        size = len(body) + 2
        out += struct.pack("<H", 0xB000 | (size - 3)) + body
    return bytes(out)


def rec(rid, payload=b""):
    return struct.pack("<HI", rid, len(payload)) + payload


def dir_stream():
    cp = "cp1252"
    d = bytearray()
    d += rec(0x0001, struct.pack("<I", 3))          # SYSKIND win64
    d += rec(0x0002, struct.pack("<I", 0x409))      # LCID
    d += rec(0x0014, struct.pack("<I", 0x409))      # LCIDINVOKE
    d += rec(0x0003, struct.pack("<H", 1252))       # CODEPAGE
    d += rec(0x0004, b"TemplateProject")            # NAME
    d += rec(0x0005, b"") + rec(0x0040, b"")        # DOCSTRING
    d += rec(0x0006, b"") + rec(0x003D, b"")        # HELPFILEPATH
    d += rec(0x0007, struct.pack("<I", 0))          # HELPCONTEXT
    d += rec(0x0008, struct.pack("<I", 0))          # LIBFLAGS
    d += struct.pack("<HI", 0x0009, 4) + struct.pack("<IH", 1, 0)  # VERSION (quirk: 6 bytes)
    d += rec(0x000C, b"") + rec(0x003C, b"")        # CONSTANTS

    def registered(name, libid):
        nb = name.encode(cp)
        lb = libid.encode(cp)
        r = rec(0x0016, nb) + rec(0x003E, name.encode("utf-16-le"))
        r += rec(0x000D, struct.pack("<I", len(lb)) + lb + struct.pack("<IH", 0, 0))
        return r

    d += registered("stdole", r"*\G{00020430-0000-0000-C000-000000000046}#2.0#0#C:\Windows\System32\stdole2.tlb#OLE Automation")
    d += registered("ArmTools", r"*\G{12345678-1234-1234-1234-1234567890AB}#1.0#0#C:\Tools\ArmTools\ArmTools.tlb#Armenian tools")

    d += rec(0x000F, struct.pack("<H", 1))          # MODULES count
    d += rec(0x0013, struct.pack("<H", 0xFFFF))     # PROJECTCOOKIE
    name = "HyphenTools"
    d += rec(0x0019, name.encode(cp)) + rec(0x0047, name.encode("utf-16-le"))
    d += rec(0x001A, name.encode(cp)) + rec(0x0032, name.encode("utf-16-le"))
    d += rec(0x001C, b"") + rec(0x0048, b"")
    d += rec(0x0031, struct.pack("<I", 0))          # TextOffset
    d += rec(0x001E, struct.pack("<I", 0))
    d += rec(0x002C, struct.pack("<H", 0xFFFF))
    d += rec(0x0021)                                # procedural module
    d += rec(0x002B)                                # module terminator
    d += struct.pack("<HI", 0x0010, 0)              # dir terminator
    return bytes(d)


class Entry:
    def __init__(self, name, typ, data=b""):
        self.name, self.typ, self.data = name, typ, data
        self.children = []
        self.left = self.right = self.child = 0xFFFFFFFF
        self.start = 0xFFFFFFFE
        self.size = len(data) if typ == 2 else 0


def cfb(root_children):
    root = Entry("Root Entry", 5)
    root.children = root_children
    entries = []

    def collect(e):
        e.idx = len(entries)
        entries.append(e)
        for c in e.children:
            collect(c)
    collect(root)

    def key(e):
        return (len(e.name), e.name.upper())
    for e in entries:
        if e.children:
            kids = sorted(e.children, key=key)
            e.child = kids[0].idx
            for a, b in zip(kids, kids[1:]):
                a.right = b.idx

    # mini stream: all streams are < 4096 bytes
    mini = bytearray()
    minifat = []
    for e in entries:
        if e.typ != 2:
            continue
        if e.size == 0:
            e.start = 0xFFFFFFFE
            continue
        nsec = (len(e.data) + 63) // 64
        first = len(mini) // 64
        e.start = first
        mini += e.data + b"\0" * (nsec * 64 - len(e.data))
        for k in range(nsec):
            minifat.append(first + k + 1 if k < nsec - 1 else 0xFFFFFFFE)
    root.size = len(mini)

    dir_bytes = bytearray()
    for e in entries:
        nm = e.name.encode("utf-16-le") + b"\0\0"
        ent = nm + b"\0" * (64 - len(nm))
        ent += struct.pack("<HBB", len(nm), e.typ, 1)
        ent += struct.pack("<III", e.left, e.right, e.child)
        ent += b"\0" * 16 + struct.pack("<I", 0) + b"\0" * 16
        ent += struct.pack("<IQ", 0, 0)  # placeholder start/size patched below
        dir_bytes += ent
    while len(dir_bytes) % 512:
        dir_bytes += (b"\0" * 64 + struct.pack("<HBB", 0, 0, 0) + struct.pack("<III", 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF) + b"\0" * 52)

    minifat_bytes = b"".join(struct.pack("<I", x) for x in minifat)
    minifat_bytes += b"\xff" * ((-len(minifat_bytes)) % 512)
    mini_bytes = bytes(mini) + b"\0" * ((-len(mini)) % 512)

    n_dir = len(dir_bytes) // 512
    n_mf = len(minifat_bytes) // 512
    n_ms = len(mini_bytes) // 512
    # sector layout: [FAT][DIR...][MINIFAT...][MINISTREAM...]
    fat_sec = 0
    dir_start = 1
    mf_start = dir_start + n_dir
    ms_start = mf_start + n_mf
    total = ms_start + n_ms
    assert total <= 128
    fat = [0xFFFFFFFF] * 128
    fat[fat_sec] = 0xFFFFFFFD

    def chain(start, n):
        for k in range(n):
            fat[start + k] = start + k + 1 if k < n - 1 else 0xFFFFFFFE
    chain(dir_start, n_dir)
    chain(mf_start, n_mf)
    chain(ms_start, n_ms)
    root.start = ms_start if n_ms else 0xFFFFFFFE

    dir_bytes = bytearray(dir_bytes)
    for e in entries:
        off = e.idx * 128 + 0x74
        dir_bytes[off:off + 12] = struct.pack("<IQ", e.start if (e.typ == 2 or e.typ == 5) else 0, e.size)

    hdr = bytearray(512)
    hdr[0:8] = bytes([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1])
    struct.pack_into("<HHHHH", hdr, 0x18, 0x3E, 3, 0xFFFE, 9, 6)
    struct.pack_into("<I", hdr, 0x2C, 1)          # FAT sectors
    struct.pack_into("<I", hdr, 0x30, dir_start)
    struct.pack_into("<I", hdr, 0x38, 4096)
    struct.pack_into("<I", hdr, 0x3C, mf_start if n_mf else 0xFFFFFFFE)
    struct.pack_into("<I", hdr, 0x40, n_mf)
    struct.pack_into("<I", hdr, 0x44, 0xFFFFFFFE)
    struct.pack_into("<I", hdr, 0x48, 0)
    for i in range(109):
        struct.pack_into("<I", hdr, 0x4C + i * 4, 0xFFFFFFFF)
    struct.pack_into("<I", hdr, 0x4C, fat_sec)
    fat_bytes = b"".join(struct.pack("<I", x) for x in fat)
    return bytes(hdr) + fat_bytes + bytes(dir_bytes) + minifat_bytes + mini_bytes


def build():
    vba = Entry("VBA", 1)
    vba.children = [
        Entry("dir", 2, compress(dir_stream())),
        Entry("HyphenTools", 2, compress(MODULE_SOURCE.encode("cp1252"))),
        Entry("_VBA_PROJECT", 2, bytes([0xCC, 0x61, 0xFF, 0xFF, 0x00, 0x00, 0x00])),
    ]
    project = Entry("PROJECT", 2, b'ID="{11111111-2222-3333-4444-555555555555}"\r\nModule=HyphenTools\r\nName="TemplateProject"\r\n')
    return cfb([vba, project])


def dotm(vba_bin):
    ct = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
          '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
          '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
          '<Default Extension="xml" ContentType="application/xml"/>'
          '<Default Extension="bin" ContentType="application/vnd.ms-office.vbaProject"/>'
          '<Override PartName="/word/document.xml" ContentType="application/vnd.ms-word.template.macroEnabledTemplate.main+xml"/>'
          '<Override PartName="/word/activeX/activeX1.xml" ContentType="application/vnd.ms-office.activeX+xml"/>'
          '</Types>')
    rels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>'
            '</Relationships>')
    doc = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
           '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p/></w:body></w:document>')
    docrels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
               '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
               '<Relationship Id="rId1" Type="http://schemas.microsoft.com/office/2006/relationships/vbaProject" Target="vbaProject.bin"/>'
               '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate" Target="file:///C:\\Templates\\Base.dotx" TargetMode="External"/>'
               '</Relationships>')
    ax = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
          '<ax:ocx ax:classid="{8BD21D40-EC42-11CE-9E0D-00AA006002F3}" ax:persistence="persistPropertyBag" '
          'xmlns:ax="http://schemas.microsoft.com/office/2006/activeX"/>')
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", ct)
        z.writestr("_rels/.rels", rels)
        z.writestr("word/document.xml", doc)
        z.writestr("word/_rels/document.xml.rels", docrels)
        z.writestr("word/vbaProject.bin", vba_bin)
        z.writestr("word/activeX/activeX1.xml", ax)
    return buf.getvalue()


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    b = build()
    with open(os.path.join(OUT, "vbaProject.bin"), "wb") as f:
        f.write(b)
    with open(os.path.join(OUT, "synthetic.dotm"), "wb") as f:
        f.write(dotm(b))
    print("written", len(b), "bytes")
