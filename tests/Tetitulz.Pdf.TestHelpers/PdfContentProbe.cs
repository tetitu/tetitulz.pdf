// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Reads geometry back out of a generated PDF's content streams, so a test can assert on
/// <b>where things were drawn</b> rather than on whether bytes came back.
///
/// <para><b>Why geometry, and not extracted text.</b>
/// <c>Core.Text.PdfTextExtractor.ExtractText</c> re-applies bidi reordering per line, so text
/// drawn in the <i>wrong</i> visual order extracts to the <i>right</i> logical string. An
/// ordering assertion built on it passes with the renderer's bidi deleted — it is unfalsifiable
/// for the thing it appears to test. These probes read the <c>Tm</c> coordinates the renderer
/// actually emitted.</para>
///
/// <para>Lives here rather than in one test project because the Builder and HtmlToPdf suites both
/// need it, and two copies of a decoder are how two suites come to disagree about what the
/// output contains.</para>
/// </summary>
public static class PdfContentProbe
{
    /// <summary>The whole file as Latin-1 text, for structural assertions on uncompressed objects.</summary>
    public static string Raw(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    // The trailer /ID is 16 random bytes per build, so two runs of the same engine never match
    // byte-for-byte. Masking that one field is what makes "the same engine produced both"
    // assertable while leaving everything else under comparison.
    private static readonly Regex FileIdHex = new Regex("<[0-9A-Fa-f]{32}>", RegexOptions.Compiled);

    /// <summary>The file as text with the nondeterministic trailer <c>/ID</c> masked.</summary>
    public static string NormalizedRaw(byte[] pdf) => FileIdHex.Replace(Raw(pdf), "<FILEID>");

    /// <summary>
    /// The <c>/BaseFont</c> of the font a literal string starting with <paramref name="word"/> is
    /// SET in: the resource of the last <c>Tf</c> before it, followed to its font dictionary.
    /// </summary>
    /// <remarks>
    /// <b>Not a search of the file for a font name.</b> A Builder page carries all twelve standard
    /// faces as resources whatever it draws, so <c>Assert.Contains("Courier", raw)</c> passed for a
    /// page with no monospace text at all. Throws where it cannot follow the chain (a font in an
    /// object stream, an inline font dictionary) rather than answering something else.
    /// </remarks>
    public static string BaseFontOf(byte[] pdf, string word)
    {
        // Inflated streams first, then the file itself: an uncompressed content stream is only there.
        var shown = Regex.Match(JoinKernedStrings(DecodeStreams(pdf)) + "\n" + Raw(pdf),
            @"/([\w.+-]+) [\d.]+ Tf(?:(?!Tf)[\s\S])*?\(" + Regex.Escape(word));
        if (!shown.Success) throw new InvalidOperationException($"'{word}' was not drawn in a literal string");

        string raw = Raw(pdf);
        var resource = Regex.Match(raw, "/" + Regex.Escape(shown.Groups[1].Value) + @"\s+(\d+)\s+(\d+)\s+R");
        if (!resource.Success) throw new InvalidOperationException($"font resource /{shown.Groups[1].Value} is not an indirect reference in the file");

        var obj = Regex.Match(raw, @"(?<![\d])" + resource.Groups[1].Value + @"\s+" + resource.Groups[2].Value + @"\s+obj([\s\S]*?)endobj");
        if (!obj.Success) throw new InvalidOperationException($"font object {resource.Groups[1].Value} is not in the file uncompressed");

        var baseFont = Regex.Match(obj.Groups[1].Value, @"/BaseFont\s*/([\w.+#-]+)");
        if (!baseFont.Success) throw new InvalidOperationException($"font object {resource.Groups[1].Value} has no /BaseFont name");
        return baseFont.Groups[1].Value;
    }

    /// <summary>
    /// Inflates every FlateDecode stream in the file and concatenates the results. Streams that
    /// are not zlib (an embedded JPEG, a font program) are skipped rather than throwing.
    /// </summary>
    public static string DecodeStreams(byte[] pdf) => string.Concat(InflatedStreams(pdf));

    /// <summary>Every FlateDecode stream in the file, inflated, one string each, in file order; the rest skipped.</summary>
    public static IEnumerable<string> InflatedStreams(byte[] pdf)
    {
        string raw = Raw(pdf);
        int i = 0;
        while ((i = raw.IndexOf("stream", i, StringComparison.Ordinal)) >= 0)
        {
            int start = i + 6;
            if (start < raw.Length && raw[start] == '\r') start++;
            if (start < raw.Length && raw[start] == '\n') start++;
            int end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) break;
            byte[] comp = Encoding.Latin1.GetBytes(raw.Substring(start, end - start));
            string? inflated = null;
            try
            {
                using var ms = new MemoryStream(comp);
                using var z = new ZLibStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                z.CopyTo(outMs);
                inflated = Encoding.Latin1.GetString(outMs.ToArray());
            }
            catch { /* not a zlib stream — skip */ }
            if (inflated != null) yield return inflated;
            i = end + 9;
        }
    }

    private static readonly Regex ContentToken = new Regex(
        @"\((?:\\.|[^\\)])*\)|[^\s()<>\[\]{}/%]+|/[^\s()<>\[\]{}/%]*", RegexOptions.Compiled);

    /// <summary>
    /// Every simple-font text draw (<c>1 0 0 1 x y Tm (…) Tj</c>) where it lands on the page — its
    /// text matrix's origin through the CTM in force, <c>q</c>, <c>Q</c> and <c>cm</c> followed — each
    /// stream walked on its own from the identity, in file order. <see cref="DrawnTexts"/> reads the
    /// text matrix alone, which a translation round the text does not change.
    /// </summary>
    public static List<DrawnText> DrawnTextsOnPage(byte[] pdf)
    {
        var list = new List<DrawnText>();
        foreach (string content in InflatedStreams(pdf))
        {
            var stack = new Stack<double[]>();
            double[] ctm = { 1, 0, 0, 1, 0, 0 };
            var numbers = new List<double>();
            double tx = 0, ty = 0;
            bool upright = false;
            string? text = null;
            foreach (Match m in ContentToken.Matches(content))
            {
                string token = m.Value;
                // A TJ array's strings are one text, its kerning numbers aside.
                if (token[0] == '(') { text += UnescapePdfLiteral(token.Substring(1, token.Length - 2)); continue; }
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) { numbers.Add(number); continue; }
                switch (token)
                {
                    case "q": stack.Push(ctm); break;
                    case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                    case "cm" when numbers.Count >= 6:
                    {
                        double[] k = numbers.GetRange(numbers.Count - 6, 6).ToArray();
                        ctm = new[]
                        {
                            k[0] * ctm[0] + k[1] * ctm[2], k[0] * ctm[1] + k[1] * ctm[3],
                            k[2] * ctm[0] + k[3] * ctm[2], k[2] * ctm[1] + k[3] * ctm[3],
                            k[4] * ctm[0] + k[5] * ctm[2] + ctm[4], k[4] * ctm[1] + k[5] * ctm[3] + ctm[5],
                        };
                        break;
                    }
                    case "Tm" when numbers.Count >= 6:
                    {
                        double[] k = numbers.GetRange(numbers.Count - 6, 6).ToArray();
                        upright = k[0] == 1 && k[1] == 0 && k[2] == 0 && k[3] == 1;
                        tx = k[4]; ty = k[5];
                        break;
                    }
                    case "Tj" or "TJ" when text != null && upright:
                        list.Add(new DrawnText(ctm[0] * tx + ctm[2] * ty + ctm[4], ctm[1] * tx + ctm[3] * ty + ctm[5], text));
                        break;
                }
                numbers.Clear();
                text = null;
            }
        }
        return list;
    }

    /// <summary>
    /// The decompressed font program a descriptor's <c>/FontFile</c>, <c>/FontFile2</c> or
    /// <c>/FontFile3</c> points at, or <c>null</c> when the file has none.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a test needs the program and not the file size.</b> CFF subsetting works by
    /// blanking charstrings in place, so the embedded program is <i>exactly</i> as long as the
    /// original and the PDF barely changes size. The only observable difference is inside the
    /// program: which charstrings are still their original bytes and which are a bare
    /// <c>endchar</c>. Nothing short of pulling the stream out and reading it can see that.</para>
    /// <para>Deliberately text-scanning rather than parsing: this assembly has no reference to the
    /// library it is used to test, which is what stops a defect in the parser from hiding the same
    /// defect in the writer.</para>
    /// </remarks>
    /// <param name="key">The descriptor key, e.g. <c>"FontFile3"</c>.</param>
    /// <param name="occurrence">Which one, when the document embeds several fonts. 0-based.</param>
    public static byte[]? EmbeddedFontProgram(byte[] pdf, string key = "FontFile3", int occurrence = 0)
    {
        string raw = Raw(pdf);
        var refs = Regex.Matches(raw, @"/" + Regex.Escape(key) + @"\s+(\d+)\s+(\d+)\s+R");
        if (occurrence < 0 || occurrence >= refs.Count) return null;

        string objNum = refs[occurrence].Groups[1].Value;
        var body = Regex.Match(raw, @"(?:^|[\r\n])" + objNum + @"\s+0\s+obj\b");
        if (!body.Success) return null;

        int at = raw.IndexOf("stream", body.Index, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = at + 6;
        if (start < raw.Length && raw[start] == '\r') start++;
        if (start < raw.Length && raw[start] == '\n') start++;
        int end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
        if (end < 0) return null;

        byte[] payload = Encoding.Latin1.GetBytes(raw.Substring(start, end - start));
        try
        {
            using var ms = new MemoryStream(payload);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            z.CopyTo(outMs);
            return outMs.ToArray();
        }
        catch
        {
            return payload;   // an unfiltered /FontFile is legal and needs no inflate
        }
    }

    /// <summary>
    /// The <c>/BaseFont</c> names the file declares, in file order. An ISO 32000-1 §9.6.4 subset
    /// carries an <c>ABCDEF+</c> prefix, so this is how a test tells a subset from a whole font.
    /// </summary>
    public static List<string> BaseFontNames(byte[] pdf)
    {
        var names = new List<string>();
        foreach (Match m in Regex.Matches(Raw(pdf), @"/BaseFont\s*/([^\s/>\[\]()<]+)"))
            names.Add(m.Groups[1].Value);
        return names;
    }

    /// <summary>Number of pages, counted by <c>/MediaBox</c> entries.</summary>
    public static int CountPages(byte[] pdf)
    {
        string raw = Raw(pdf);
        int n = 0, i = 0;
        while ((i = raw.IndexOf("/MediaBox", i, StringComparison.Ordinal)) >= 0) { n++; i += 9; }
        return n;
    }

    // ---------------------------------------------------------------- text draws

    /// <summary>One text-showing operation with the absolute position from its text matrix.</summary>
    public readonly struct DrawnText
    {
        public DrawnText(double x, double y, string text) { X = x; Y = y; Text = text; }
        public double X { get; }
        public double Y { get; }
        public string Text { get; }
        public override string ToString() =>
            $"'{Text}' @ ({X.ToString("0.##", CultureInfo.InvariantCulture)}, {Y.ToString("0.##", CultureInfo.InvariantCulture)})";
    }

    private static readonly Regex TmTj = new Regex(
        @"1\s+0\s+0\s+1\s+(-?[\d.]+)\s+(-?[\d.]+)\s+Tm\s*\n?(?:\((.*?)\)\s*Tj|\[((?:\((?:\\.|[^\\)])*\)|\s|-?[\d.]+)*)\]\s*TJ)",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LiteralInArray = new Regex(@"\(((?:\\.|[^\\)])*)\)", RegexOptions.Compiled);

    private static readonly Regex KernedLiteralArray = new Regex(
        @"\[((?:\((?:\\.|[^\\)])*\)|\s|-?[\d.]+)*)\]\s*TJ", RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="decodedContent"/> with each kerned literal-string <c>TJ</c> array — a
    /// standard-14 run's pairs, <c>[(Visib) 20 (le)] TJ</c> — written back as the one <c>(Visible) Tj</c>
    /// it draws, so a test reading a word reads it whole. Glyph-id arrays are left as they are.
    /// </summary>
    public static string JoinKernedStrings(string decodedContent)
        => KernedLiteralArray.Replace(decodedContent,
            m => "(" + string.Concat(LiteralInArray.Matches(m.Groups[1].Value).Select(s => s.Groups[1].Value)) + ") Tj");

    /// <summary>
    /// Every simple-font (WinAnsi) text draw in the decoded content, in stream order, with its
    /// absolute x/y — a <c>Tj</c> string, or a kerned <c>TJ</c> array's strings joined. Composite
    /// Identity-H draws use hex glyph strings — see <see cref="DrawnGlyphRuns"/>.
    /// </summary>
    public static List<DrawnText> DrawnTexts(string decodedContent)
    {
        var list = new List<DrawnText>();
        foreach (Match m in TmTj.Matches(decodedContent))
        {
            string text = m.Groups[3].Success
                ? UnescapePdfLiteral(m.Groups[3].Value)
                : string.Concat(LiteralInArray.Matches(m.Groups[4].Value).Select(s => UnescapePdfLiteral(s.Groups[1].Value)));
            list.Add(new DrawnText(D(m.Groups[1].Value), D(m.Groups[2].Value), text));
        }
        return list;
    }

    private static readonly Regex TmHexTj = new Regex(
        @"1\s+0\s+0\s+1\s+(-?[\d.]+)\s+(-?[\d.]+)\s+Tm\s*\n?<([0-9A-Fa-f]+)>\s*Tj",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Every composite (Identity-H) glyph run, in stream order, as (x, y, glyph ids). The glyph
    /// ids are opaque without the font, but their <i>positions</i> are exactly what an RTL
    /// ordering test needs.
    /// </summary>
    public static List<(double X, double Y, ushort[] Gids)> DrawnGlyphRuns(string decodedContent)
    {
        var list = new List<(double, double, ushort[])>();
        foreach (Match m in TmHexTj.Matches(decodedContent))
        {
            string hex = m.Groups[3].Value;
            var gids = new ushort[hex.Length / 4];
            for (int i = 0; i < gids.Length; i++)
                gids[i] = Convert.ToUInt16(hex.Substring(i * 4, 4), 16);
            list.Add((D(m.Groups[1].Value), D(m.Groups[2].Value), gids));
        }
        return list;
    }

    /// <summary>The x of the first draw whose text equals <paramref name="text"/>, or NaN.</summary>
    public static double XOf(string decodedContent, string text)
    {
        foreach (var d in DrawnTexts(decodedContent)) if (d.Text == text) return d.X;
        return double.NaN;
    }

    /// <summary>The y of the first draw whose text equals <paramref name="text"/>, or NaN.</summary>
    public static double YOf(string decodedContent, string text)
    {
        foreach (var d in DrawnTexts(decodedContent)) if (d.Text == text) return d.Y;
        return double.NaN;
    }

    // ---------------------------------------------------------------- stroked lines

    /// <summary>A stroked straight line — how table borders and rules are drawn.</summary>
    public readonly struct Segment
    {
        public Segment(double x0, double y0, double x1, double y1)
        { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        public double X0 { get; }
        public double Y0 { get; }
        public double X1 { get; }
        public double Y1 { get; }
        public bool IsVertical => Math.Abs(X0 - X1) < 0.01;
        public bool IsHorizontal => Math.Abs(Y0 - Y1) < 0.01;
        public override string ToString() => $"({X0:0.##},{Y0:0.##})→({X1:0.##},{Y1:0.##})";
    }

    private static readonly Regex MoveLineStroke = new Regex(
        @"(-?[\d.]+)\s+(-?[\d.]+)\s+m\s*\n?(-?[\d.]+)\s+(-?[\d.]+)\s+l\s*\n?S",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Every <c>m … l … S</c> line, so a test can assert which edges exist, not how many.</summary>
    public static List<Segment> StrokedSegments(string decodedContent)
    {
        var list = new List<Segment>();
        foreach (Match m in MoveLineStroke.Matches(decodedContent))
            list.Add(new Segment(D(m.Groups[1].Value), D(m.Groups[2].Value),
                D(m.Groups[3].Value), D(m.Groups[4].Value)));
        return list;
    }

    /// <summary>Number of filled rectangles (<c>… re</c> followed by <c>f</c>) — shading, highlights.</summary>
    public static int RectangleFillCount(string decodedContent) =>
        Regex.Matches(decodedContent, @"[-\d.]+\s+[-\d.]+\s+[-\d.]+\s+[-\d.]+\s+re\s*\n?f").Count;

    // ---------------------------------------------------------------- helpers

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static string UnescapePdfLiteral(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            if (++i >= s.Length) break;
            sb.Append(s[i] switch
            {
                'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f',
                _ => s[i],
            });
        }
        return sb.ToString();
    }
}
