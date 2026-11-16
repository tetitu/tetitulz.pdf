// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Diagnostic test that renders raw/multicolumn.pdf with full diagnostic logging
/// to expose which font is incorrect and why.
/// Run with TETITULZ_PDF_DUMP_RENDERS=1 to dump the PNG to %TEMP%\tetitulz_renders\.
/// </summary>
public sealed class MulticolumnFontDiagnosticTests
{
    // Resolved via TestCorpus (env var, else <repo>/raw or <repo>/tests/pdf). Null when the
    // developer-local corpus is absent, in which case [CorpusFact] skips every test here —
    // previously this fell back to a hardcoded c:\myapp\... path and hard-failed everywhere.
    private static readonly string? PdfPath = TestCorpus.Find("multicolumn.pdf");
    private readonly ITestOutputHelper _out;

    public MulticolumnFontDiagnosticTests(ITestOutputHelper output) => _out = output;

    [CorpusFact("multicolumn.pdf")]
    public void RenderPage0_CaptureAllFontAndRenderDiagnostics()
    {
        var events = new List<string>();
        // Keep the delegate in a local so it can actually be removed again. The previous
        // code unsubscribed a *different* lambda instance, so the handler leaked onto the
        // static RenderDiagnostics.OnEvent event and polluted every later test in the run.
        Action<RenderDiagnosticKind, string, Exception> handler = (kind, detail, ex) =>
            events.Add($"[{kind}] {detail}{(ex != null ? " | " + ex.Message : "")}");
        RenderDiagnostics.OnEvent += handler;

        byte[] png = null;
        try
        {
            using var fs = File.OpenRead(PdfPath!);
            png = PdfOperations.RenderPageToPng(fs, pageIndex: 0, dpi: 150);
        }
        finally
        {
            RenderDiagnostics.OnEvent -= handler;
        }

        Assert.NotNull(png);
        Assert.True(png.Length > 8, "Rendered PNG is too small");

        _out.WriteLine($"PNG size: {png.Length:N0} bytes");

        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        _out.WriteLine($"Bitmap: {bmp.Width}x{bmp.Height}");

        _out.WriteLine($"\n=== Render diagnostics ({events.Count}) ===");
        foreach (var e in events)
            _out.WriteLine(e);

        // Enumerate fonts from the PDF
        _out.WriteLine("\n=== Page font resources ===");
        _out.WriteLine(EnumeratePageFontsToString());

        // Always write diagnostics to a temp file for easy inspection
        {
            string outDir = Path.Combine(Path.GetTempPath(), "tetitulz_renders");
            Directory.CreateDirectory(outDir);
            string diagPath = Path.Combine(outDir, "multicolumn_font_diag.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"PNG size: {png.Length:N0} bytes");
            sb.AppendLine($"Bitmap: {bmp.Width}x{bmp.Height}");
            sb.AppendLine($"\n=== Render diagnostics ({events.Count}) ===");
            foreach (var e in events) sb.AppendLine(e);
            sb.AppendLine("\n=== Page font resources ===");
            // Re-enumerate fonts and write to buffer too
            sb.AppendLine(EnumeratePageFontsToString());
            File.WriteAllText(diagPath, sb.ToString());
            _out.WriteLine($"Font diagnostics written to: {diagPath}");

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TETITULZ_PDF_DUMP_RENDERS")))
            {
                string outPath = Path.Combine(outDir, "multicolumn_p0.png");
                File.WriteAllBytes(outPath, png);
                _out.WriteLine($"Dumped PNG to: {outPath}");
            }
        }
    }

    private string EnumeratePageFontsToString()
    {
        var sb = new System.Text.StringBuilder();
        using var fs = File.OpenRead(PdfPath!);
        using var doc = PdfOperations.OpenWithRepair(fs);

        if (doc.Pages.Count == 0) { sb.AppendLine("  (no pages)"); return sb.ToString(); }

        var page = doc.Pages[0];
        var resources = GetPageResources(page, doc);
        if (resources == null) { sb.AppendLine("  (no /Resources)"); return sb.ToString(); }

        var fontRes = resources[PdfName.Font];
        if (fontRes == null) { sb.AppendLine("  (no /Font in resources)"); return sb.ToString(); }

        var fontDict = doc.ResolveObject(fontRes) as PdfDictionary;
        if (fontDict == null) { sb.AppendLine("  (font dict is not a dict)"); return sb.ToString(); }

        foreach (var kv in fontDict.Keys)
        {
            string name = kv.Value;
            var entry = doc.ResolveObject(fontDict[kv]) as PdfDictionary
                     ?? fontDict[kv] as PdfDictionary;
            if (entry == null) { sb.AppendLine($"  /{name}: unresolvable"); continue; }

            string subtype  = (entry[PdfName.Subtype] as PdfName)?.Value ?? "?";
            string basefont = (entry[PdfName.BaseFont] as PdfName)?.Value ?? "?";
            string encoding = FormatEncoding(entry, doc);
            string embedded = CheckEmbedded(entry, doc);

            string toUnicode = entry[PdfName.ToUnicode] != null ? "yes" : "none";
            sb.AppendLine($"  /{name}: Subtype={subtype} BaseFont={basefont} Encoding={encoding} ToUnicode={toUnicode} Embedded={embedded}");

            // For Type0 fonts, show the descendant's info too
            if (subtype == "Type0")
            {
                var da = entry[PdfName.DescendantFonts];
                var arr = da != null ? doc.ResolveObject(da) as PdfArray : null;
                if (arr != null && arr.Count > 0)
                {
                    var desc = doc.ResolveObject(arr[0]) as PdfDictionary;
                    if (desc != null)
                    {
                        string dsub = (desc[PdfName.Subtype] as PdfName)?.Value ?? "?";
                        string demb = CheckEmbedded(desc, doc);
                        sb.AppendLine($"    → Descendant Subtype={dsub} Embedded={demb}");
                    }
                }
            }
        }
        return sb.ToString();
    }

    private static PdfDictionary GetPageResources(PdfPage page, PdfDocument doc)
    {
        var resObj = page.Dictionary[new PdfName("Resources")];
        if (resObj == null) return null;
        return doc.ResolveObject(resObj) as PdfDictionary ?? resObj as PdfDictionary;
    }

    [CorpusFact("multicolumn.pdf")]
    public void Type1Parser_EexecDecrypt_InspectFirstBytes()
    {
        // Low-level diagnostic: look at the raw and decrypted bytes of the binary section
        using var fs = File.OpenRead(PdfPath!);
        using var doc = PdfOperations.OpenWithRepair(fs);
        var page = doc.Pages[0];
        var resources = GetPageResources(page, doc);
        var fontDict = doc.ResolveObject(resources[PdfName.Font]) as PdfDictionary;

        var diagLines = new System.Text.StringBuilder();
        void Log(string s) { _out.WriteLine(s); diagLines.AppendLine(s); }

        foreach (var kv in fontDict.Keys)
        {
            var entry = doc.ResolveObject(fontDict[kv]) as PdfDictionary ?? fontDict[kv] as PdfDictionary;
            if (entry == null) continue;
            string basefont = (entry[PdfName.BaseFont] as PdfName)?.Value ?? "";
            if (!basefont.Contains("CMR10")) continue; // just inspect CMR10

            var fd = doc.ResolveObject(entry[PdfName.FontDescriptor]) as PdfDictionary;
            var ffStream = doc.ResolveObject(fd?[PdfName.FontFile]) as PdfStream;
            if (ffStream == null) continue;

            int l1 = (int)((ffStream[new PdfName("Length1")] as PdfNumber)?.LongValue ?? 0);
            int l2 = (int)((ffStream[new PdfName("Length2")] as PdfNumber)?.LongValue ?? 0);
            byte[] data = ffStream.GetDecodedData();
            Assert.NotNull(data);

            Log($"{basefont}: l1={l1} l2={l2} total={data.Length}");

            // Show last 100 bytes of ASCII section (contains eexec invocation)
            int previewStart = Math.Max(0, l1 - 100);
            string asciiEnd = System.Text.Encoding.Latin1.GetString(data, previewStart, Math.Min(100, l1 - previewStart));
            Log($"  ASCII end: [{asciiEnd.Replace("\n","\\n").Replace("\r","\\r")}]");

            // Show first 20 bytes of binary section as hex
            string binHex = string.Join(" ", data.Skip(l1).Take(20).Select(b => b.ToString("X2")));
            bool allHex = data.Skip(l1).Take(20).All(b => IsHexChar2(b) || b == '\n' || b == '\r' || b == ' ');
            Log($"  Binary section first 20 bytes: [{binHex}] allHex={allHex}");

            // Try eexec decryption and show first 200 chars of output
            byte[] decrypted = EexecDecryptPublic(data, l1, l2, isHex: allHex);
            if (decrypted != null)
            {
                string dec = System.Text.Encoding.Latin1.GetString(decrypted, 0, Math.Min(200, decrypted.Length));
                Log($"  Decrypted first 200 chars: [{dec.Replace("\n","\\n").Replace("\r","\\r")}]");
            }
            else
            {
                Log("  Decryption returned null");
            }
        }

        string diagDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tetitulz_renders");
        System.IO.Directory.CreateDirectory(diagDir);
        string diagFile = System.IO.Path.Combine(diagDir, "eexec_diag.txt");
        System.IO.File.WriteAllText(diagFile, diagLines.ToString());
    }

    private static bool IsHexChar2(byte b)
        => (b >= '0' && b <= '9') || (b >= 'A' && b <= 'F') || (b >= 'a' && b <= 'f');

    private static byte[] EexecDecryptPublic(byte[] data, int l1, int l2, bool isHex)
    {
        byte[] cipher;
        if (isHex)
        {
            var hex = new System.Collections.Generic.List<byte>();
            for (int i = l1; i + 1 < l1 + l2; )
            {
                while (i < l1 + l2 && !IsHexChar2(data[i])) i++;
                if (i + 1 >= l1 + l2) break;
                int hi = HexVal2(data[i++]);
                int lo = HexVal2(data[i++]);
                if (hi < 0 || lo < 0) continue;
                hex.Add((byte)((hi << 4) | lo));
            }
            cipher = hex.ToArray();
        }
        else
        {
            cipher = new byte[l2];
            Array.Copy(data, l1, cipher, 0, l2);
        }
        System.Diagnostics.Debug.WriteLine($"    cipher length={cipher.Length} first4=[{string.Join(" ",cipher.Take(4).Select(b=>b.ToString("X2")))}]");

        ushort r = 55665;
        var plain = new byte[cipher.Length];
        for (int i = 0; i < cipher.Length; i++)
        {
            byte c = cipher[i];
            plain[i] = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
        }
        if (plain.Length <= 4) return null;
        var result = new byte[plain.Length - 4];
        Array.Copy(plain, 4, result, 0, result.Length);
        return result;
    }

    private static int HexVal2(byte b)
    {
        if (b >= '0' && b <= '9') return b - '0';
        if (b >= 'A' && b <= 'F') return b - 'A' + 10;
        if (b >= 'a' && b <= 'f') return b - 'a' + 10;
        return -1;
    }

    [CorpusFact("multicolumn.pdf")]
    public void Type1_GlyphPaths_ShowCorrectBounds()
    {
        using var fs = File.OpenRead(PdfPath!);
        using var doc = PdfOperations.OpenWithRepair(fs);
        var fontDict = doc.ResolveObject(GetPageResources(doc.Pages[0], doc)[PdfName.Font]) as PdfDictionary;

        var diagLines = new System.Text.StringBuilder();
        void Log(string s) { _out.WriteLine(s); diagLines.AppendLine(s); }

        foreach (var kv in fontDict.Keys)
        {
            var entry = doc.ResolveObject(fontDict[kv]) as PdfDictionary ?? fontDict[kv] as PdfDictionary;
            string basefont = (entry?[PdfName.BaseFont] as PdfName)?.Value ?? "";
            if (!basefont.Contains("CMR10")) continue;

            var fd = doc.ResolveObject(entry[PdfName.FontDescriptor]) as PdfDictionary;
            var ffStream = doc.ResolveObject(fd?[PdfName.FontFile]) as PdfStream;
            if (ffStream == null) continue;

            int l1 = (int)((ffStream[new PdfName("Length1")] as PdfNumber)?.LongValue ?? 0);
            int l2 = (int)((ffStream[new PdfName("Length2")] as PdfNumber)?.LongValue ?? 0);
            byte[] data = ffStream.GetDecodedData();
            var t1 = Tetitulz.Pdf.Core.Fonts.Type1.Type1Parser.Parse(data, l1, l2);
            Assert.NotNull(t1);

            Log($"{basefont}: {t1.CharStrings.Count} glyphs, {t1.Subrs.Count} subrs");
            Log($"  FontMatrixScale={t1.FontMatrixScale}");

            // Show encoding for interesting ranges
            for (int code = 32; code <= 42; code++)
                Log($"  enc[{code}] = {t1.Encoding[code]}");
            // A-Z range
            for (int code = 65; code <= 90; code++)
                if (t1.Encoding[code] != ".notdef") Log($"  enc[{code}] = {t1.Encoding[code]}");
            // a-z range
            for (int code = 97; code <= 122; code++)
                if (t1.Encoding[code] != ".notdef") Log($"  enc[{code}] = {t1.Encoding[code]}");
            // show all non-notdef
            var nonNotdef = Enumerable.Range(0, 256)
                .Where(c => t1.Encoding[c] != ".notdef")
                .Select(c => $"{c}={t1.Encoding[c]}")
                .ToList();
            Log($"  Non-notdef encoding entries: [{string.Join(", ", nonNotdef)}]");

            // Show raw charstring bytes for 'i' to verify decryption
            if (t1.CharStrings.TryGetValue("i", out byte[] iCs))
            {
                Log($"  'i' charstring ({iCs.Length} bytes): [{string.Join(" ", iCs.Take(30).Select(b => b.ToString("X2")))}]");
                // Decode as Type 1 numbers/operators
                var ops = new System.Text.StringBuilder();
                for (int idx = 0; idx < Math.Min(30, iCs.Length); idx++)
                {
                    byte b = iCs[idx];
                    if (b >= 32 && b <= 246) ops.Append($"push({b-139}) ");
                    else if (b >= 247 && b <= 250 && idx+1 < iCs.Length) { ops.Append($"push({(b-247)*256+iCs[++idx]+108}) "); }
                    else if (b >= 251 && b <= 254 && idx+1 < iCs.Length) { ops.Append($"push({-((b-251)*256+iCs[++idx]+108)}) "); }
                    else if (b == 28 && idx+2 < iCs.Length) { short v = (short)((iCs[idx+1]<<8)|iCs[idx+2]); idx+=2; ops.Append($"push({v}) "); }
                    else ops.Append($"op({b}) ");
                }
                Log($"  'i' decoded: [{ops}]");
            }

            // Evaluate a few glyphs and show path bounds + advance
            foreach (var glyphName in new[] { ".notdef", "a", "T", "space", "i" })
            {
                if (!t1.CharStrings.ContainsKey(glyphName)) { Log($"  {glyphName}: not in font"); continue; }
                var outline = Tetitulz.Pdf.Base.Rendering.Type1CharstringEvaluator.Evaluate(t1, glyphName);
                var bounds = outline.Path.Bounds;
                Log($"  '{glyphName}': adv={outline.AdvanceWidth:F0} path_empty={outline.Path.IsEmpty} L={bounds.Left:F0} T={bounds.Top:F0} R={bounds.Right:F0} B={bounds.Bottom:F0}");
            }
        }

        string diagDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tetitulz_renders");
        System.IO.Directory.CreateDirectory(diagDir);
        string diagFile = System.IO.Path.Combine(diagDir, "glyph_bounds.txt");
        System.IO.File.WriteAllText(diagFile, diagLines.ToString());
    }

    [CorpusFact("multicolumn.pdf")]
    public void Type1Parser_ParsesMulticolumnFonts_ExtractsCharStrings()
    {
        using var fs = File.OpenRead(PdfPath!);
        using var doc = PdfOperations.OpenWithRepair(fs);

        var page = doc.Pages[0];
        var resources = GetPageResources(page, doc);
        Assert.NotNull(resources);

        var fontRes = resources[PdfName.Font];
        var fontDict = doc.ResolveObject(fontRes) as PdfDictionary;
        Assert.NotNull(fontDict);

        int parsed = 0;
        foreach (var kv in fontDict.Keys)
        {
            var entry = doc.ResolveObject(fontDict[kv]) as PdfDictionary ?? fontDict[kv] as PdfDictionary;
            if (entry == null) continue;
            string basefont = (entry[PdfName.BaseFont] as PdfName)?.Value ?? "";
            var fdObj = entry[PdfName.FontDescriptor];
            var fd    = fdObj != null ? doc.ResolveObject(fdObj) as PdfDictionary : null;
            if (fd == null) continue;
            var ffObj = fd[PdfName.FontFile];
            if (ffObj == null) continue;
            var ffStream = doc.ResolveObject(ffObj) as PdfStream;
            if (ffStream == null) continue;

            int l1 = (int)((ffStream[new PdfName("Length1")] as PdfNumber)?.LongValue ?? 0);
            int l2 = (int)((ffStream[new PdfName("Length2")] as PdfNumber)?.LongValue ?? 0);
            byte[] data = ffStream.GetDecodedData();
            Assert.NotNull(data);

            var t1font = Tetitulz.Pdf.Core.Fonts.Type1.Type1Parser.Parse(data, l1, l2);
            _out.WriteLine($"{basefont}: l1={l1} l2={l2} decoded={data.Length} parsed={t1font != null}");
            if (t1font != null)
            {
                _out.WriteLine($"  CharStrings: {t1font.CharStrings.Count} glyphs");
                _out.WriteLine($"  Subrs: {t1font.Subrs.Count}");
                _out.WriteLine($"  FontMatrixScale: {t1font.FontMatrixScale}");
                // Show the encoding for codes 65-90 (A-Z)
                var encSample = string.Join(" ", Array.ConvertAll(
                    Enumerable.Range(32, 12).Select(i => $"{i}={t1font.Encoding[i]}").ToArray(), s => s));
                _out.WriteLine($"  Encoding[32..43]: {encSample}");
                // Try to evaluate a simple glyph
                if (t1font.CharStrings.ContainsKey("a") || t1font.CharStrings.ContainsKey("A"))
                {
                    string g = t1font.CharStrings.ContainsKey("a") ? "a" : "A";
                    var outline = Tetitulz.Pdf.Base.Rendering.Type1CharstringEvaluator.Evaluate(t1font, g);
                    _out.WriteLine($"  Glyph '{g}': empty={outline.Path.IsEmpty} advance={outline.AdvanceWidth}");
                }
                parsed++;
            }
        }

        // Write to file too
        _out.WriteLine($"Parsed {parsed} Type 1 fonts successfully");
        Assert.True(parsed > 0, "Expected at least one Type 1 font to parse successfully");
    }

    private static byte[] WrapType1AsPfb(byte[] raw, int l1, int l2)
    {
        using var ms = new MemoryStream(6 + l1 + 6 + l2 + 2);
        void WriteSector(byte type, int offset, int length) {
            ms.WriteByte(0x80); ms.WriteByte(type);
            ms.WriteByte((byte)(length & 0xFF)); ms.WriteByte((byte)((length >> 8) & 0xFF));
            ms.WriteByte((byte)((length >> 16) & 0xFF)); ms.WriteByte((byte)((length >> 24) & 0xFF));
            ms.Write(raw, offset, length);
        }
        WriteSector(1, 0, l1);
        WriteSector(2, l1, l2);
        ms.WriteByte(0x80); ms.WriteByte(0x03);
        return ms.ToArray();
    }

    private static string FormatEncoding(PdfDictionary fontDict, PdfDocument doc)
    {
        var encObj = fontDict[PdfName.Encoding];
        if (encObj == null) return "(none)";
        if (encObj is PdfName pn) return "/" + pn.Value;
        var resolved = doc.ResolveObject(encObj);
        if (resolved is PdfName pn2) return "/" + pn2.Value;
        if (resolved is PdfDictionary) return "(dict)";
        return "?";
    }

    private static string CheckEmbedded(PdfDictionary fontDict, PdfDocument doc)
    {
        var fdObj = fontDict[PdfName.FontDescriptor];
        if (fdObj == null) return "no-descriptor";
        var fd = doc.ResolveObject(fdObj) as PdfDictionary;
        if (fd == null) return "descriptor-unresolved";

        foreach (var key in new[] { "FontFile2", "FontFile3", "FontFile" })
        {
            var ff = fd[new PdfName(key)];
            if (ff == null) continue;
            var ffStream = doc.ResolveObject(ff) as PdfStream;
            if (ffStream == null) continue;

            string subtype = (ffStream[PdfName.Subtype] as PdfName)?.Value ?? "";
            int len = (int)((ffStream[new PdfName("Length1")] as PdfNumber)?.LongValue
                          ?? (ffStream[new PdfName("Length")] as PdfNumber)?.LongValue
                          ?? 0);
            int len2 = (int)((ffStream[new PdfName("Length2")] as PdfNumber)?.LongValue ?? 0);
            int len3 = (int)((ffStream[new PdfName("Length3")] as PdfNumber)?.LongValue ?? 0);

            // Decode and inspect first 24 bytes
            string magic = "";
            try
            {
                byte[] data = ffStream.GetDecodedData();
                if (data != null && data.Length > 0)
                {
                    int preview = Math.Min(16, data.Length);
                    // Show as ASCII if printable, else hex
                    bool isPrintable = true;
                    for (int i = 0; i < Math.Min(4, preview); i++)
                        if (data[i] < 0x20 || data[i] > 0x7E) { isPrintable = false; break; }
                    magic = isPrintable
                        ? $" magic='{System.Text.Encoding.ASCII.GetString(data, 0, preview).Replace("\n","\\n").Replace("\r","\\r")}'"
                        : $" magic=[{string.Join(" ", data[..Math.Min(8, data.Length)].Select(b => b.ToString("X2")))}]";

                    // Check if SkiaSharp can load it raw, and via PFB wrapping
                    bool skLoads = false;
                    bool pfbLoads = false;
                    try {
                        using var skData = SkiaSharp.SKData.CreateCopy(data);
                        using var tf = SkiaSharp.SKTypeface.FromData(skData);
                        skLoads = tf != null;
                    } catch { }
                    // Try PFB wrapping using the stream's Length1/Length2 entries
                    try {
                        int l1 = (int)((ffStream[new PdfName("Length1")] as PdfNumber)?.LongValue ?? 0);
                        int l2 = (int)((ffStream[new PdfName("Length2")] as PdfNumber)?.LongValue ?? 0);
                        if (l1 > 0 && l2 > 0 && l1 + l2 <= data.Length) {
                            byte[] pfb = WrapType1AsPfb(data, l1, l2);
                            using var pfbData = SkiaSharp.SKData.CreateCopy(pfb);
                            using var tf2 = SkiaSharp.SKTypeface.FromData(pfbData);
                            pfbLoads = tf2 != null;
                        }
                    } catch { }
                    magic += $" sk_raw={skLoads} sk_pfb={pfbLoads} decoded_len={data.Length}";
                }
            }
            catch { magic = " decode_failed"; }

            return $"yes(/{key}" + (subtype.Length > 0 ? $",{subtype}" : "")
                + $",L1={len} L2={len2} L3={len3}{magic})";
        }
        return "no-fontfile";
    }
}
