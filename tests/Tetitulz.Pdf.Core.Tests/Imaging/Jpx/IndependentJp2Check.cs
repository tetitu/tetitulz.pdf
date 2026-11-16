// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// A JP2 (ISO/IEC 15444-1 Annex I) and raw-codestream (Annex A) validator that shares no code with
/// this library's JPEG 2000 reader or writer — the boxes and marker segments read from the standard's
/// tables alone — so the encoder's files are judged by something other than the code that wrote them.
/// </summary>
/// <remarks>
/// Each rule is a named test; a file is valid when none fails. The names follow the usual validator
/// convention (<c>brandIsValid</c>, <c>enumCSIsValid</c>, …). Part 2 (JPX) is not accepted: a file that
/// needs it fails exactly the Part 1 rules it breaks.
/// </remarks>
internal static class IndependentJp2Check
{
    internal sealed record Verdict(bool IsValid, string[] FailedTests);

    /// <summary>Validates <paramref name="data"/> as <c>jp2</c> (the file format) or <c>j2c</c> (a raw codestream).</summary>
    internal static Verdict Validate(byte[] data, string format)
    {
        var failed = new List<string>();
        void Test(string name, bool passed) { if (!passed && !failed.Contains(name)) failed.Add(name); }

        if (format == "j2c") CheckCodestream(data, 0, data.Length, null, Test);
        else CheckFile(data, Test);
        return new Verdict(failed.Count == 0, failed.ToArray());
    }

    private sealed record Box(string Type, int Start, int End);   // Start: the box's contents; End: exclusive

    private static List<Box>? Boxes(byte[] d, int start, int end, Action<string, bool> test)
    {
        var boxes = new List<Box>();
        int pos = start;
        while (pos < end)
        {
            if (pos + 8 > end) { test("boxLengthIsValid", false); return null; }
            long length = U32(d, pos);
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            int header = 8;
            if (length == 1)
            {
                if (pos + 16 > end) { test("boxLengthIsValid", false); return null; }
                length = (long)((ulong)U32(d, pos + 8) << 32 | (ulong)U32(d, pos + 12));
                header = 16;
            }
            else if (length == 0) length = end - pos;
            if (length < header || pos + length > end) { test("boxLengthIsValid", false); return null; }
            boxes.Add(new Box(type, pos + header, (int)(pos + length)));
            pos += (int)length;
        }
        return boxes;
    }

    private static void CheckFile(byte[] d, Action<string, bool> test)
    {
        List<Box>? boxes = Boxes(d, 0, d.Length, test);
        if (boxes == null || boxes.Count == 0) { test("containsSignatureBox", false); return; }

        // I.5.1: the signature box first, twelve bytes, 0x0D0A870A.
        Box sig = boxes[0];
        test("containsSignatureBox", sig.Type == "jP  ");
        test("signatureIsValid", sig.Type == "jP  " && sig.End - sig.Start == 4 && U32(d, sig.Start) == 0x0D0A870A);

        // I.5.2: the file type box second; brand 'jp2 ', minor version 0, 'jp2 ' among the compatible.
        Box? ftyp = boxes.Count > 1 && boxes[1].Type == "ftyp" ? boxes[1] : null;
        test("fileTypeBoxIsSecondBox", ftyp != null);
        if (ftyp != null)
        {
            int n = ftyp.End - ftyp.Start;
            test("fileTypeBoxLengthIsValid", n >= 8 && (n - 8) % 4 == 0);
            if (n >= 8)
            {
                test("brandIsValid", Ascii(d, ftyp.Start, 4) == "jp2 ");
                test("minorVersionIsValid", U32(d, ftyp.Start + 4) == 0);
                bool compatible = false;
                for (int p = ftyp.Start + 8; p + 4 <= ftyp.End; p += 4) compatible |= Ascii(d, p, 4) == "jp2 ";
                test("compatibilityListIsValid", compatible);
            }
        }

        int headerIndex = boxes.FindIndex(b => b.Type == "jp2h");
        int codestreamIndex = boxes.FindIndex(b => b.Type == "jp2c");
        test("containsHeaderBox", headerIndex >= 0);
        test("containsContiguousCodestreamBox", codestreamIndex >= 0);
        test("headerBoxBeforeCodestreamBox", headerIndex >= 0 && codestreamIndex >= 0 && headerIndex < codestreamIndex);
        test("noMoreThanOneHeaderBox", boxes.Count(b => b.Type == "jp2h") <= 1);

        Ihdr? ihdr = headerIndex >= 0 ? CheckHeader(d, boxes[headerIndex], test) : null;
        if (codestreamIndex >= 0) CheckCodestream(d, boxes[codestreamIndex].Start, boxes[codestreamIndex].End, ihdr, test);
    }

    private sealed record Ihdr(long Height, long Width, int Components, int Bpc);

    private static Ihdr? CheckHeader(byte[] d, Box jp2h, Action<string, bool> test)
    {
        List<Box>? children = Boxes(d, jp2h.Start, jp2h.End, test);
        if (children == null || children.Count == 0) { test("containsImageHeaderBox", false); return null; }
        test("containsImageHeaderBox", children.Any(b => b.Type == "ihdr"));
        test("firstJP2HeaderBoxIsImageHeaderBox", children[0].Type == "ihdr");
        test("containsColourSpecificationBox", children.Any(b => b.Type == "colr"));
        test("noMoreThanOneBitsPerComponentBox", children.Count(b => b.Type == "bpcc") <= 1);
        test("noMoreThanOnePaletteBox", children.Count(b => b.Type == "pclr") <= 1);
        test("noMoreThanOneChannelDefinitionBox", children.Count(b => b.Type == "cdef") <= 1);

        Ihdr? ihdr = null;
        Box? ih = children.FirstOrDefault(b => b.Type == "ihdr");
        if (ih != null)
        {
            // I.5.3.1: HEIGHT, WIDTH, NC, BPC, C, UnkC, IPR — 14 bytes.
            bool sized = ih.End - ih.Start == 14;
            test("imageHeaderBoxLengthIsValid", sized);
            if (sized)
            {
                long height = U32(d, ih.Start), width = U32(d, ih.Start + 4);
                int nc = U16(d, ih.Start + 8), bpc = d[ih.Start + 10];
                test("heightIsValid", height >= 1);
                test("widthIsValid", width >= 1);
                test("nCIsValid", nc >= 1 && nc <= 16384);
                test("bPCIsValid", bpc == 255 || ((bpc & 0x7F) + 1) <= 38);
                test("cIsValid", d[ih.Start + 11] == 7);
                test("unkIsValid", d[ih.Start + 12] <= 1);
                test("iPRIsValid", d[ih.Start + 13] <= 1);
                test("bPCVsBitsPerComponentBox", bpc != 255 || children.Any(b => b.Type == "bpcc"));
                ihdr = new Ihdr(height, width, nc, bpc);
            }
        }

        foreach (Box colr in children.Where(b => b.Type == "colr"))
        {
            // I.5.3.3: METH, PREC, APPROX, then the enumerated space or the profile.
            if (colr.End - colr.Start < 3) { test("colourSpecificationBoxLengthIsValid", false); continue; }
            int meth = d[colr.Start];
            test("methIsValid", meth == 1 || meth == 2);
            test("approxIsValid", d[colr.Start + 2] == 0);
            if (meth == 1)
            {
                bool sized = colr.End - colr.Start == 7;
                test("colourSpecificationBoxLengthIsValid", sized);
                if (sized)
                {
                    long cs = U32(d, colr.Start + 3);
                    test("enumCSIsValid", cs == 16 || cs == 17 || cs == 18);
                }
            }
            else if (meth == 2)
            {
                // A restricted ICC profile: its header's size field is the bytes that follow.
                int n = colr.End - colr.Start - 3;
                test("iccSizeIsValid", n >= 128 && U32(d, colr.Start + 3) == n);
            }
        }

        foreach (Box cdef in children.Where(b => b.Type == "cdef"))
        {
            // I.5.3.6: N, then N (Cn, Typ, Asoc) of two bytes each.
            if (cdef.End - cdef.Start < 2) { test("channelDefinitionBoxLengthIsValid", false); continue; }
            int n = U16(d, cdef.Start);
            bool sized = cdef.End - cdef.Start == 2 + 6 * n;
            test("channelDefinitionBoxLengthIsValid", sized);
            if (!sized) continue;
            for (int i = 0; i < n; i++)
            {
                int typ = U16(d, cdef.Start + 2 + 6 * i + 2);
                test("typIsValid", typ <= 2 || typ == 65535);
            }
        }
        return ihdr;
    }

    private static void CheckCodestream(byte[] d, int start, int end, Ihdr? ihdr, Action<string, bool> test)
    {
        // A.4.1 / A.5.1: SOC, then SIZ as the first marker segment.
        test("codestreamStartsWithSOCMarker", end - start >= 4 && U16(d, start) == 0xFF4F);
        test("foundSIZMarker", end - start >= 4 && U16(d, start + 2) == 0xFF51);
        if (end - start < 6 || U16(d, start + 2) != 0xFF51) return;

        int pos = start + 4;
        int lsiz = U16(d, pos);
        if (pos + lsiz > end || lsiz < 38) { test("lsizIsValid", false); return; }
        int csiz = U16(d, pos + 36);
        test("lsizIsValid", lsiz == 38 + 3 * csiz);
        test("rsizIsValid", (U16(d, pos + 2) & 0x8000) == 0);   // Part 2 capabilities are not Part 1
        long xsiz = U32(d, pos + 4), ysiz = U32(d, pos + 8), xo = U32(d, pos + 12), yo = U32(d, pos + 16);
        long xt = U32(d, pos + 20), yt = U32(d, pos + 24), xto = U32(d, pos + 28), yto = U32(d, pos + 32);
        test("xsizIsValid", xsiz > xo);
        test("ysizIsValid", ysiz > yo);
        test("xTsizIsValid", xt >= 1 && xto <= xo && xto + xt > xo);
        test("yTsizIsValid", yt >= 1 && yto <= yo && yto + yt > yo);
        test("csizIsValid", csiz >= 1 && csiz <= 16384);
        int[] depths = new int[Math.Max(0, csiz)];
        for (int i = 0; i < csiz && pos + 38 + 3 * i + 2 < pos + lsiz; i++)
        {
            int ssiz = d[pos + 38 + 3 * i];
            depths[i] = (ssiz & 0x7F) + 1;
            test("ssizIsValid", depths[i] <= 38);
            test("xRsizIsValid", d[pos + 39 + 3 * i] >= 1);
            test("yRsizIsValid", d[pos + 40 + 3 * i] >= 1);
        }
        if (ihdr != null)
        {
            test("ncConsistentWithSizeCsiz", ihdr.Components == csiz);
            test("widthConsistentWithSIZ", ihdr.Width == xsiz - xo);
            test("heightConsistentWithSIZ", ihdr.Height == ysiz - yo);
            if (ihdr.Bpc != 255) test("bpcConsistentWithSIZ", depths.All(dp => dp == (ihdr.Bpc & 0x7F) + 1));
        }
        long tilesX = (xsiz - xto + xt - 1) / Math.Max(1, xt), tilesY = (ysiz - yto + yt - 1) / Math.Max(1, yt);
        long tiles = tilesX * tilesY;
        pos += lsiz;

        // The rest of the main header: marker segments up to the first SOT; COD and QCD among them.
        bool cod = false, qcd = false;
        while (pos + 4 <= end && U16(d, pos) != 0xFF90)
        {
            int marker = U16(d, pos);
            if ((marker & 0xFF00) != 0xFF00) { test("mainHeaderMarkersAreValid", false); return; }
            int length = U16(d, pos + 2);
            if (length < 2 || pos + 2 + length > end) { test("mainHeaderMarkersAreValid", false); return; }
            if (marker == 0xFF52) { cod = true; CheckCod(d, pos + 4, length - 2, test); }
            if (marker == 0xFF5C) qcd = true;
            pos += 2 + length;
        }
        test("containsCODSegment", cod);
        test("containsQCDSegment", qcd);

        // Tile-parts: SOT, Psot bytes each (0 only for the last), Isot within the grid; EOC at the end.
        int tileParts = 0;
        while (pos + 12 <= end && U16(d, pos) == 0xFF90)
        {
            tileParts++;
            test("lsotIsValid", U16(d, pos + 2) == 10);
            int isot = U16(d, pos + 4);
            long psot = U32(d, pos + 6);
            test("isotIsValid", isot < tiles);
            if (psot == 0) { pos = end - 2; break; }
            test("psotIsValid", psot >= 14 && pos + psot <= end);
            if (psot < 14 || pos + psot > end) return;
            pos += (int)psot;
        }
        test("foundFirstTilepart", tileParts > 0);
        test("foundEOCMarker", pos + 2 == end && U16(d, pos) == 0xFFD9);
    }

    private static void CheckCod(byte[] d, int at, int length, Action<string, bool> test)
    {
        // A.6.1: Scod, SGcod (order, layers, MCT), SPcod (levels, xcb, ycb, style, transform, precincts).
        if (length < 10) { test("lcodIsValid", false); return; }
        test("orderIsValid", d[at + 1] <= 4);
        test("layersIsValid", U16(d, at + 2) >= 1);
        test("multipleComponentTransformationIsValid", d[at + 4] <= 1);
        test("levelsIsValid", d[at + 5] <= 32);
        int xcb = d[at + 6] + 2, ycb = d[at + 7] + 2;
        test("codeBlockWidthExponentIsValid", xcb >= 2 && xcb <= 10);
        test("codeBlockHeightExponentIsValid", ycb >= 2 && ycb <= 10);
        test("sumHeightWidthExponentIsValid", xcb + ycb <= 12);
        test("transformationIsValid", d[at + 9] <= 1);
        bool precincts = (d[at] & 1) != 0;
        test("lcodIsValid", length == 10 + (precincts ? d[at + 5] + 1 : 0));
    }

    private static long U32(byte[] d, int at) => ((long)d[at] << 24) | ((long)d[at + 1] << 16) | ((long)d[at + 2] << 8) | d[at + 3];
    private static int U16(byte[] d, int at) => (d[at] << 8) | d[at + 1];
    private static string Ascii(byte[] d, int at, int n) => System.Text.Encoding.ASCII.GetString(d, at, n);
}
