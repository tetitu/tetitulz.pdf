// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Character spacing, horizontal scale and rise are text-state parameters that PERSIST across
/// pieces (§9.3.1) — neither <c>Tm</c> nor a new <c>Tj</c> resets them. These assert on the
/// operator stream, because a leaked <c>Tc</c> and a correct one produce equally valid PDFs.
/// </summary>
public class TextStateTests
{
    [Fact]
    public void CharSpacingOnOneRun_IsReturnedToZeroBeforeTheNextRunOnTheSameLine()
    {
        string content = Content(
            new TextRun { Text = "Total", CharSpacing = 3 },
            new TextRun { Text = "1234" });

        int set = content.IndexOf("3 Tc", StringComparison.Ordinal);
        int second = content.IndexOf("(1234)", StringComparison.Ordinal);
        Assert.True(set >= 0, "the first run's char spacing was never emitted");
        Assert.True(second > set, "the second run should be drawn after the first");

        int reset = content.IndexOf("0 Tc", set, StringComparison.Ordinal);
        Assert.True(reset >= 0 && reset < second,
            "char spacing must be returned to 0 before the next piece is shown, " +
            "or the second run is drawn wider than the width it was measured at");
    }

    [Fact]
    public void HorizontalScaleOnOneRun_IsReturnedTo100BeforeTheNextRunOnTheSameLine()
    {
        string content = Content(
            new TextRun { Text = "Wide", HorizontalScale = 150 },
            new TextRun { Text = "Normal" });

        int set = content.IndexOf("150 Tz", StringComparison.Ordinal);
        int second = content.IndexOf("(Normal)", StringComparison.Ordinal);
        Assert.True(set >= 0, "the first run's horizontal scale was never emitted");
        Assert.True(second > set);

        int reset = content.IndexOf("100 Tz", set, StringComparison.Ordinal);
        Assert.True(reset >= 0 && reset < second,
            "horizontal scale must be returned to 100 before the next piece is shown");
    }

    /// <summary>
    /// §9.4.4: the rise displaces the glyph <b>in addition to</b> the text-matrix translation. So
    /// the baseline handed to Tm must be the plain one — adding the rise there as well drew every
    /// superscript at twice its offset, while the underline / strike / highlight passes used the
    /// single offset and detached from the glyphs.
    /// </summary>
    [Fact]
    public void SuperscriptRise_ReachesTheGlyphsThroughTsOnly_NotAlsoThroughTheTextMatrix()
    {
        const double size = 12;
        string content = Content(
            new TextRun { Text = "base", FontSize = size },
            new TextRun { Text = "sup", FontSize = size, VerticalAlign = VerticalAlign.Superscript });

        double baseY = TmYBefore(content, "(base)");
        double supY = TmYBefore(content, "(sup)");

        // Same line, same baseline: the two text matrices must agree.
        Assert.Equal(baseY, supY, 3);

        // …and the whole offset arrives as Ts. 0.35 is PdfExporter.SuperscriptRiseFactor.
        double ts = TsBefore(content, "(sup)");
        Assert.Equal(size * 0.35, ts, 2);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly Regex TmRe =
        new Regex(@"1 0 0 1 (-?[\d.]+) (-?[\d.]+) Tm", RegexOptions.Compiled);
    private static readonly Regex TsRe = new Regex(@"(-?[\d.]+) Ts", RegexOptions.Compiled);

    /// <summary>The y of the last <c>Tm</c> emitted before <paramref name="marker"/> is shown.</summary>
    private static double TmYBefore(string content, string marker)
    {
        int at = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{marker}' is not in the content stream");
        var ms = TmRe.Matches(content.Substring(0, at));
        Assert.True(ms.Count > 0, $"no Tm precedes '{marker}'");
        return double.Parse(ms[ms.Count - 1].Groups[2].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>The last <c>Ts</c> value in force when <paramref name="marker"/> is shown.</summary>
    private static double TsBefore(string content, string marker)
    {
        int at = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{marker}' is not in the content stream");
        var ms = TsRe.Matches(content.Substring(0, at));
        Assert.True(ms.Count > 0, $"no Ts precedes '{marker}'");
        return double.Parse(ms[ms.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string Content(params TextRun[] runs)
    {
        var doc = new Document();
        var sec = new Section();
        var p = new Paragraph();
        foreach (var r in runs) p.Runs.Add(r);
        sec.Children.Add(p);
        doc.Sections.Add(sec);
        return Inflate(doc.ToPdf());
    }

    // Inflates every FlateDecode content stream and concatenates the operators.
    private static string Inflate(byte[] pdf)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder();
        int idx = 0;
        while (true)
        {
            int s = raw.IndexOf("stream", idx, StringComparison.Ordinal);
            if (s < 0) break;
            if (s >= 3 && raw.Substring(s - 3, 3) == "end") { idx = s + 6; continue; }
            int dataStart = s + 6;
            if (dataStart < raw.Length && raw[dataStart] == '\r') dataStart++;
            if (dataStart < raw.Length && raw[dataStart] == '\n') dataStart++;
            int e = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (e < 0) break;
            int len = e - dataStart;
            while (len > 0 && (raw[dataStart + len - 1] == '\n' || raw[dataStart + len - 1] == '\r')) len--;
            byte[] data = Encoding.Latin1.GetBytes(raw.Substring(dataStart, len));
            try
            {
                using var inMs = new MemoryStream(data);
                using var z = new ZLibStream(inMs, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                z.CopyTo(outMs);
                sb.Append(Encoding.Latin1.GetString(outMs.ToArray()));
                sb.Append('\n');
            }
            catch { /* not a flate stream (e.g. a font program) */ }
            idx = e + 9;
        }
        return sb.ToString();
    }
}
