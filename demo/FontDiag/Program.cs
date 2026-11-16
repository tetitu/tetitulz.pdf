// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Objects;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: FontDiag <input.pdf> [pageCount]");
    Environment.Exit(1);
}

var pdfPath = args[0];
int maxPages = args.Length > 1 ? int.Parse(args[1]) : 4;

using var ms = new MemoryStream(File.ReadAllBytes(pdfPath));
var doc = PdfDocument.Open(ms);
var pages = doc.Pages;

Console.WriteLine($"PDF: {pdfPath}");
Console.WriteLine($"Total pages: {pages.Count}");
Console.WriteLine();

for (int i = 0; i < Math.Min(pages.Count, maxPages); i++)
{
    var page = pages[i];
    Console.WriteLine($"=== Page {i + 1} ===");

    var resources = page.Resources;
    if (resources == null)
    {
        Console.WriteLine("  <no resources>");
    }
    else
    {
        var fontRes = resources[PdfName.Font];
        if (fontRes == null)
        {
            Console.WriteLine("  /Font: <none>");
        }
        else
        {
            var fontDict = doc.ResolveObject(fontRes) as PdfDictionary ?? fontRes as PdfDictionary;
            if (fontDict != null)
            {
                foreach (var key in fontDict.Keys)
                {
                    var entry = doc.ResolveObject(fontDict[key]) as PdfDictionary;
                    if (entry == null) continue;

                    string baseFont = entry.GetName(PdfName.BaseFont)?.Value ?? "?";
                    string subtype  = entry.GetName(PdfName.Subtype)?.Value  ?? "?";

                    Console.WriteLine($"  /{key.Value} → {subtype} {baseFont}");

                    // ---- Font file info ----
                    PrintFontFileInfo(entry, doc, "    ");

                    // ---- Encoding + Differences ----
                    PrintEncoding(entry, doc, "    ");

                    // ---- CharCodeToGid after CFF parse + PDF encoding override ----
                    PrintCffCharMap(entry, doc, key.Value, "    ");

                    Console.WriteLine();
                }
            }
        }
    }

    // Content stream: show Tf and Tm operators
    byte[]? cs = page.GetContentStreamData();
    if (cs != null)
    {
        string csText = Encoding.Latin1.GetString(cs);
        var tfMatches = Regex.Matches(csText, @"/([\w.+]+)\s+([\d.]+)\s+Tf");
        Console.WriteLine($"  Content stream: {cs.Length} bytes, {tfMatches.Count} Tf operators");
        foreach (Match m in tfMatches.Cast<Match>().Take(8))
            Console.WriteLine($"    Tf: /{m.Groups[1].Value}  size={m.Groups[2].Value}");
        // Tm operators: a b c d e f Tm
        var tmMatches = Regex.Matches(csText,
            @"([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+Tm");
        Console.WriteLine($"  Tm operators ({tmMatches.Count} total, first 6):");
        foreach (Match m in tmMatches.Cast<Match>().Take(6))
            Console.WriteLine($"    Tm: [{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value} {m.Groups[4].Value} {m.Groups[5].Value} {m.Groups[6].Value}]");
        // First 1500 printable chars of the content stream, for context.
        var printable = new string(csText.Take(1500).Select(c => c >= 32 && c < 127 ? c : ' ').ToArray());
        Console.WriteLine($"  Content stream head (1500 chars):\n{printable}");
    }
    else
    {
        Console.WriteLine("  Content stream: <null>");
    }

    Console.WriteLine();
}

// ---- Render page indices 1..3 with diagnostics, where they exist ----
// NOTE: pi is the 0-based index RenderPageToPng expects, so this renders the
// SECOND through FOURTH pages — the labels below print the index, not the page number.
//
// BOUNDED BY THE DOCUMENT. This ran 1..3 unconditionally, so every document with fewer than four
// pages printed "Page index N is out of range" once or twice — caught and printed, exit code still
// 0, so the tool read as working while emitting noise that looks like a finding. The scan above
// already bounds itself with Math.Min(pages.Count, maxPages); this half did not.
int lastRenderIndex = Math.Min(3, pages.Count - 1);
Console.WriteLine(lastRenderIndex >= 1
    ? $"=== Render diagnostics for page indices 1-{lastRenderIndex} ==="
    : "=== Render diagnostics skipped: the document has only one page ===");

var diagEvents = new List<string>();
RenderDiagnostics.OnEvent += (kind, detail, ex) =>
    diagEvents.Add($"  [{kind}] {detail}{(ex != null ? ": " + ex.Message : "")}");

using var ms2 = new MemoryStream(File.ReadAllBytes(pdfPath));
for (int pi = 1; pi <= lastRenderIndex; pi++)
{
    diagEvents.Clear();
    Console.WriteLine($"\n--- Rendering page {pi} ---");
    try
    {
        var png = PdfOperations.RenderPageToPng(ms2, pi, dpi: 72);
        Console.WriteLine($"  Rendered OK ({png?.Length} bytes PNG)");
    }
    catch (Exception ex) { Console.WriteLine($"  EXCEPTION: {ex.Message}"); }
    if (diagEvents.Count > 0) foreach (var e in diagEvents) Console.WriteLine(e);
    else Console.WriteLine("  No render diagnostics.");
    ms2.Position = 0;
}

// -----------------------------------------------------------------------
//  Helpers
// -----------------------------------------------------------------------

static void PrintFontFileInfo(PdfDictionary fontEntry, PdfDocument doc, string indent)
{
    var fdObj = fontEntry[PdfName.FontDescriptor];
    if (fdObj == null)
    {
        // Type0: look in descendant
        var da = fontEntry[PdfName.DescendantFonts];
        if (da != null)
        {
            var darr = doc.ResolveObject(da) as PdfArray;
            if (darr?.Count > 0)
            {
                var cid = doc.ResolveObject(darr[0]) as PdfDictionary;
                fdObj = cid?[PdfName.FontDescriptor];
            }
        }
    }
    if (fdObj == null) { Console.WriteLine($"{indent}FontDescriptor: none"); return; }
    var fd = doc.ResolveObject(fdObj) as PdfDictionary;
    if (fd == null) { Console.WriteLine($"{indent}FontDescriptor: unresolvable"); return; }

    foreach (var fkName in new[] { "FontFile2", "FontFile3", "FontFile" })
    {
        var ffObj = fd[new PdfName(fkName)];
        if (ffObj == null) continue;
        var ffs = doc.ResolveObject(ffObj) as PdfStream;
        if (ffs == null) { Console.WriteLine($"{indent}/{fkName}: (stream unresolvable)"); return; }
        byte[]? raw = ffs.GetDecodedData();
        int size = raw?.Length ?? -1;

        // Try SkiaSharp
        string skia = "skia=?";
        if (raw != null && raw.Length >= 4)
        {
            using var skData = SkiaSharp.SKData.CreateCopy(raw);
            using var tf = SkiaSharp.SKTypeface.FromData(skData);
            skia = tf != null ? $"skia=OK({tf.FamilyName})" : "skia=FAIL(raw CFF)";
        }

        Console.WriteLine($"{indent}/{fkName}: {size} bytes  {skia}");
        return;
    }
    Console.WriteLine($"{indent}font file: none in descriptor");
}

static void PrintEncoding(PdfDictionary fontEntry, PdfDocument doc, string indent)
{
    var encObj = fontEntry[PdfName.Encoding];
    if (encObj == null) { Console.WriteLine($"{indent}Encoding: none"); return; }

    if (encObj is PdfName encName || doc.ResolveObject(encObj) is PdfName encName2)
    {
        string nm = (encObj as PdfName)?.Value ?? ((PdfName)doc.ResolveObject(encObj)).Value;
        Console.WriteLine($"{indent}Encoding: named={nm}");
        return;
    }

    var encDict = doc.ResolveObject(encObj) as PdfDictionary;
    if (encDict == null) { Console.WriteLine($"{indent}Encoding: unresolvable"); return; }

    string baseEnc = (doc.ResolveObject(encDict[PdfName.BaseEncoding]) as PdfName)?.Value
                  ?? (encDict[PdfName.BaseEncoding] as PdfName)?.Value
                  ?? "(StandardEncoding)";

    var diffsObj = encDict[PdfName.Differences];
    if (diffsObj == null)
    {
        Console.WriteLine($"{indent}Encoding: dict  base={baseEnc}  no Differences");
        return;
    }
    var diffs = doc.ResolveObject(diffsObj) as PdfArray;
    if (diffs == null) { Console.WriteLine($"{indent}Encoding: Differences unresolvable"); return; }

    Console.WriteLine($"{indent}Encoding: dict  base={baseEnc}  Differences:");
    int cur = 0;
    var sb = new StringBuilder();
    foreach (var item in diffs)
    {
        if (item is PdfNumber n) { cur = (int)n.LongValue; }
        else if (item is PdfName nm2)
        {
            sb.Append($" {cur}={nm2.Value}");
            cur++;
        }
    }
    // Print in chunks of 8 entries per line
    var parts = sb.ToString().Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    for (int j = 0; j < parts.Length; j += 8)
        Console.WriteLine($"{indent}  " + string.Join("  ", parts.Skip(j).Take(8)));
}

static void PrintCffCharMap(PdfDictionary fontEntry, PdfDocument doc, string resName, string indent)
{
    // Find /FontFile3 (CFF)
    var fdObj = fontEntry[PdfName.FontDescriptor];
    if (fdObj == null)
    {
        var da = fontEntry[PdfName.DescendantFonts];
        if (da != null)
        {
            var darr = doc.ResolveObject(da) as PdfArray;
            if (darr?.Count > 0)
            {
                var cid = doc.ResolveObject(darr[0]) as PdfDictionary;
                fdObj = cid?[PdfName.FontDescriptor];
            }
        }
    }
    if (fdObj == null) return;
    var fd = doc.ResolveObject(fdObj) as PdfDictionary;
    if (fd == null) return;

    foreach (var fkName in new[] { "FontFile3", "FontFile", "FontFile2" })
    {
        var ffObj = fd[new PdfName(fkName)];
        if (ffObj == null) continue;
        var ffs = doc.ResolveObject(ffObj) as PdfStream;
        if (ffs == null) continue;
        byte[]? raw = ffs.GetDecodedData();
        if (raw == null || raw.Length < 4) return;

        // Skip if SkiaSharp can load it (not the CFF-fallback path)
        using var skData = SkiaSharp.SKData.CreateCopy(raw);
        using var tf = SkiaSharp.SKTypeface.FromData(skData);
        if (tf != null) return; // SkiaSharp handles it — no CFF fallback

        var cffFont = CffParser.Parse(raw);
        if (cffFont == null) { Console.WriteLine($"{indent}CFF parse FAILED: {CffParser.LastFailureReason}"); return; }

        Console.WriteLine($"{indent}CFF: {cffFont.Charstrings.Count} glyphs  " +
                          $"GlyphNameToGid.Count={cffFont.GlyphNameToGid?.Count ?? 0}  " +
                          $"NominalW={cffFont.NominalWidthX}  DefaultW={cffFont.DefaultWidthX}");

        // Dump ALL glyph names sorted by GID
        if (cffFont.GlyphNameToGid != null)
        {
            Console.WriteLine($"{indent}All GlyphNameToGid entries (GID order):");
            foreach (var kv in cffFont.GlyphNameToGid.OrderBy(kv => kv.Value))
                Console.Write($" {kv.Value}:{kv.Key}");
            Console.WriteLine();
        }

        // Show CharCodeToGid (raw, before override) for codes 1-31 and 65-173
        Console.WriteLine($"{indent}CFF internal CharCodeToGid [1-31]:");
        for (int cc = 1; cc <= 31; cc++)
            if (cffFont.CharCodeToGid.TryGetValue(cc, out int gid))
                Console.Write($" {cc}→{gid}");
        Console.WriteLine();
        Console.WriteLine($"{indent}CFF internal CharCodeToGid [65-90]:");
        for (int cc = 65; cc <= 90; cc++)
            if (cffFont.CharCodeToGid.TryGetValue(cc, out int gid2))
                Console.Write($" {cc}({(char)cc}→{gid2})");
        Console.WriteLine();
        Console.WriteLine($"{indent}CFF internal CharCodeToGid [97-130]:");
        for (int cc = 97; cc <= 130; cc++)
            if (cffFont.CharCodeToGid.TryGetValue(cc, out int gid3))
                Console.Write($" {cc}({(char)cc}→{gid3})");
        Console.WriteLine();

        // Full override simulation: for ALL charCodes in /Differences, show name lookup result
        var encObj = fontEntry[PdfName.Encoding];
        if (encObj != null)
        {
            Console.WriteLine($"{indent}Override check (all /Differences charCodes 1-173):");
            var allDiffCodes = new List<int>();
            var encDictCheck = doc.ResolveObject(encObj) as PdfDictionary;
            if (encDictCheck != null)
            {
                var diffsObjCheck = encDictCheck[PdfName.Differences];
                if (diffsObjCheck != null)
                {
                    var diffsCheck = doc.ResolveObject(diffsObjCheck) as PdfArray;
                    if (diffsCheck != null)
                    {
                        int curCode = 0;
                        foreach (var item in diffsCheck)
                        {
                            if (item is PdfNumber pn) { curCode = (int)pn.LongValue; }
                            else if (item is PdfName pname) { allDiffCodes.Add(curCode); curCode++; }
                        }
                    }
                }
            }
            foreach (int cc in allDiffCodes)
            {
                string gname = GetGlyphNameFromEncoding(cc, encObj, doc);
                if (gname == null) continue;
                int gid4 = -1;
                bool found = cffFont.GlyphNameToGid != null && cffFont.GlyphNameToGid.TryGetValue(gname, out gid4);
                string gidStr = found ? $"GID {gid4}" : "NOT FOUND in GlyphNameToGid";
                bool inRaw = cffFont.CharCodeToGid.TryGetValue(cc, out int rawGid2);
                string rawStr = inRaw ? $"raw→GID {rawGid2}" : "raw→NONE";
                Console.WriteLine($"{indent}  cc={cc:D3} → \"{gname}\" → {gidStr}  ({rawStr})");
            }
        }

        // Dump charstrings for ALL GIDs (not just selected ones)
        Console.WriteLine($"{indent}All charstring bytes (GID 0 to {Math.Min(cffFont.Charstrings.Count-1, 50)}):");
        for (int dg = 0; dg < Math.Min(cffFont.Charstrings.Count, 51); dg++)
        {
            byte[] cs = cffFont.Charstrings[dg];
            string hex = string.Join(" ", cs.Take(48).Select(b => b.ToString("X2")));
            string name2 = cffFont.GlyphNameToGid?.FirstOrDefault(kv => kv.Value == dg).Key ?? "?";
            Console.WriteLine($"{indent}  GID {dg:D2} ({name2}): [{cs.Length}b] {hex}{(cs.Length > 48 ? "..." : "")}");
        }

        return;
    }

static int[] GetGidsForNames(IReadOnlyDictionary<string,int> nameToGid, string[] names)
{
    var seen = new System.Collections.Generic.HashSet<int>();
    var result = new System.Collections.Generic.List<int>();
    foreach (var nm in names)
        if (nameToGid.TryGetValue(nm, out int gid) && seen.Add(gid))
            result.Add(gid);
    return result.ToArray();
}
}

static string? GetGlyphNameFromEncoding(int charCode, PdfObject encObj, PdfDocument doc)
{
    if (encObj is PdfName nm) return WinAnsiName(charCode); // named encoding
    var resolved = doc.ResolveObject(encObj);
    if (resolved is PdfName nm2) return WinAnsiName(charCode);

    var encDict = resolved as PdfDictionary;
    if (encDict == null) return null;

    // Start from base encoding
    string? baseName = (doc.ResolveObject(encDict[PdfName.BaseEncoding]) as PdfName)?.Value
                    ?? (encDict[PdfName.BaseEncoding] as PdfName)?.Value;
    string? result = baseName == "WinAnsiEncoding" ? WinAnsiName(charCode) : null;

    // Apply Differences
    var diffsObj = encDict[PdfName.Differences];
    if (diffsObj == null) return result;
    var diffs = doc.ResolveObject(diffsObj) as PdfArray;
    if (diffs == null) return result;

    int cur = 0;
    foreach (var item in diffs)
    {
        if (item is PdfNumber n) { cur = (int)n.LongValue; }
        else if (item is PdfName dname) {
            if (cur == charCode) return dname.Value;
            cur++;
        }
    }
    return result;
}

static string? WinAnsiName(int code)
{
    // Only the relevant uppercase range
    string[] table = {
        null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null, // 0-15
        null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null, // 16-31
        "space","exclam","quotedbl","numbersign","dollar","percent","ampersand","quotesingle",
        "parenleft","parenright","asterisk","plus","comma","hyphen","period","slash",     // 32-47
        "zero","one","two","three","four","five","six","seven","eight","nine",
        "colon","semicolon","less","equal","greater","question","at",                     // 48-64
        "A","B","C","D","E","F","G","H","I","J","K","L","M",
        "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",                             // 65-90
        "bracketleft","backslash","bracketright","asciicircum","underscore","grave",      // 91-96
        "a","b","c","d","e","f","g","h","i","j","k","l","m",
        "n","o","p","q","r","s","t","u","v","w","x","y","z",                             // 97-122
    };
    if (code < 0 || code >= table.Length) return null;
    return table[code];
}

static int RuntimeId(object obj)
    => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
