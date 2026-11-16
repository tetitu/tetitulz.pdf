import sys
js = open(sys.argv[1], 'rb').read()
objs = {}
def put(n, body): objs[n] = body
def stream(dict_extra, data): return b"<< /Length %d %s>>\nstream\n" % (len(data), dict_extra) + data + b"\nendstream"
ap = stream(b"/Type /XObject /Subtype /Form /BBox [0 0 20 20] ", b"0 g 2 2 16 16 re f")
off = stream(b"/Type /XObject /Subtype /Form /BBox [0 0 20 20] ", b"")
put(1, b"<< /Type /Catalog /Pages 2 0 R /AcroForm 30 0 R /Names << /EmbeddedFiles << /Names [(hello.txt) 40 0 R] >> >> /OpenAction 50 0 R >>")
put(2, b"<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>")
annots0 = b"[10 0 R 11 0 R 12 0 R 13 0 R 14 0 R 16 0 R 17 0 R 18 0 R 20 0 R 21 0 R 23 0 R 24 0 R 25 0 R]"
put(3, b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots " + annots0 + b" /Contents 5 0 R /Resources << /Font << /Helv 6 0 R >> >> >>")
put(4, b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [15 0 R] >>")
put(5, stream(b"", b"BT /Helv 12 Tf 36 760 Td (Tetitulz Acrobat probe, round 2: wait three seconds after the alert, then save.) Tj ET"))
put(6, b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
def text(name, rect, ff=0, v=b"", parent=None, page=3):
    t = b"/T (" + name + b") " if name is not None else b""
    p = b"/Parent %d 0 R " % parent if parent else b""
    return b"<< /Type /Annot /Subtype /Widget /FT /Tx " + t + p + b"/Ff %d /V (%s) /Rect [%s] /P %d 0 R /F 4 /DA (/Helv 6 Tf 0 g) >>" % (ff, v, rect, page)
put(10, text(b"results", b"36 36 576 600", 4096))
put(11, text(b"timer", b"36 744 200 756"))
put(12, text(b"ival", b"220 744 400 756"))
put(13, b"<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (pb) /Rect [36 700 136 720] /P 3 0 R /F 4 /MK << /CA (PB Caption) /RC (PB Rollover) /AC (PB Down) >> /DA (/Helv 8 Tf 0 g) >>")
put(14, b"<< /Type /Annot /Subtype /Widget /FT /Btn /T (cb) /V /Yes /AS /Yes /Rect [150 700 170 720] /P 3 0 R /F 4 /AP << /N << /Yes 60 0 R /Off 61 0 R >> >> /MK << /CA (4) >> >>")
put(19, b"<< /FT /Btn /Ff 49152 /T (rb) /V /B /Kids [16 0 R 15 0 R] >>")
put(16, b"<< /Type /Annot /Subtype /Widget /Parent 19 0 R /AS /Off /Rect [200 700 220 720] /P 3 0 R /F 4 /AP << /N << /A 60 0 R /Off 61 0 R >> >> >>")
put(15, b"<< /Type /Annot /Subtype /Widget /Parent 19 0 R /AS /B /Rect [300 500 320 520] /P 4 0 R /F 4 /AP << /N << /B 60 0 R /Off 61 0 R >> >> >>")
put(17, b"<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (co) /Opt [[(1) (One)] [(2) (Two)]] /V (2) /Rect [250 700 350 720] /P 3 0 R /F 4 /DA (/Helv 8 Tf 0 g) >>")
put(18, b"<< /Type /Annot /Subtype /Widget /FT /Ch /T (li) /Opt [(a) (b) (c)] /V (b) /Rect [360 660 460 720] /P 3 0 R /F 4 /DA (/Helv 8 Tf 0 g) >>")
put(22, b"<< /FT /Tx /T (grp) /Kids [20 0 R 21 0 R] >>")
put(20, text(b"a", b"36 640 136 655", v=b"3", parent=22))
put(21, text(b"b", b"150 640 250 655", v=b"4", parent=22))
put(23, text(b"p1", b"36 620 100 635", v=b"2"))
put(24, text(b"p2", b"110 620 170 635", v=b""))
put(25, text(b"p3", b"180 620 240 635", v=b"abc"))
put(30, b"<< /Fields [10 0 R 11 0 R 12 0 R 13 0 R 14 0 R 19 0 R 17 0 R 18 0 R 22 0 R 23 0 R 24 0 R 25 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> /NeedAppearances true >>")
put(40, b"<< /Type /Filespec /F (hello.txt) /UF (hello.txt) /Desc (A test attachment) /EF << /F 41 0 R >> >>")
put(41, stream(b"/Type /EmbeddedFile /Subtype /text#2Fplain /Params << /Size 11 /CreationDate (D:20260304130509Z) /ModDate (D:20260305101112Z) >> ", b"hello world"))
put(50, b"<< /S /JavaScript /JS 51 0 R >>")
put(51, stream(b"", js))
put(60, ap); put(61, off)
put(70, b"<< /Author (Probe Author) /Title (Probe Two) /Subject (Probe subject) /Keywords (probe, two) /Creator (Tetitulz probe) /Producer (Tetitulz) /CreationDate (D:20260304130509+02'00') /ModDate (D:20260305101112Z) /Custom (custom value) >>")
size = max(objs) + 1
out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n"); offs = {}
for n in sorted(objs):
    offs[n] = len(out); out += b"%d 0 obj\n" % n + objs[n] + b"\nendobj\n"
x = len(out); out += b"xref\n0 %d\n0000000000 65535 f \n" % size
for n in range(1, size):
    out += (b"%010d 00000 n \n" % offs[n]) if n in offs else b"0000000000 65535 f \n"
out += b"trailer\n<< /Size %d /Root 1 0 R /Info 70 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (size, x)
open(sys.argv[2], 'wb').write(out); print(len(out), "bytes")
