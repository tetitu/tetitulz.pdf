// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// <c>/JBIG2Globals</c> reaches the filter as a resolved stream, and a value that does not resolve
/// is reported rather than silently ignored.
/// </summary>
/// <remarks>
/// <para>
/// A PDF stream may only exist as an indirect object, so a conforming file always writes
/// <c>/DecodeParms &lt;&lt; /JBIG2Globals 8 0 R &gt;&gt;</c>. <c>PdfDictionary</c>'s indexer does not
/// follow a reference and a filter runs in Core with no document to resolve against, so
/// <c>Jbig2DecodeFilter</c>'s <c>is PdfStream</c> test matched on <b>no real file, ever</b>.
/// </para>
/// <para>
/// <b>The failure was silent and total.</b> Without globals the symbol dictionary is missing, so
/// every text-region instance is skipped, the untouched all-zero page bitmap is returned, the filter
/// inverts it to white, and <c>LastFallbackReason</c> stays null — a scanned document in symbol mode
/// rendered as blank white pages while every honest-failure channel reported success. That is the
/// exact shape CLAUDE.md's Diagnostics Pattern exists to prevent.
/// </para>
/// </remarks>
public class Jbig2GlobalsResolutionTests
{
    /// <summary>
    /// The parser must hand the filter a resolved <see cref="PdfStream"/>, not the
    /// <see cref="PdfReference"/> the file actually contains.
    /// </summary>
    [Fact]
    public void Parse_IndirectJbig2Globals_IsResolvedToAStreamOnDecodeParms()
    {
        using var input = new MemoryStream(PdfWithIndirectJbig2Globals());
        using var doc = PdfDocument.Open(input);

        var page = doc.Pages[0];
        var resources = doc.ResolveObject(page.Dictionary[PdfName.Resources]) as PdfDictionary;
        var xobjects = doc.ResolveObject(resources[PdfName.XObject]) as PdfDictionary;
        var image = doc.ResolveObject(xobjects[new PdfName("Im0")]) as PdfStream;

        Assert.NotNull(image);

        var decodeParms = doc.ResolveObject(image[PdfName.DecodeParms]) as PdfDictionary;
        Assert.NotNull(decodeParms);

        // The whole point: the raw file holds `8 0 R` here. If it still does, the filter's
        // `is PdfStream` test cannot match and the symbol dictionary is invisible.
        Assert.IsType<PdfStream>(decodeParms[PdfName.JBIG2Globals]);
    }

    /// <summary>
    /// A <c>/JBIG2Globals</c> that is present but not a stream must set the fallback reason, so
    /// "there were no globals" and "the globals could not be reached" stop looking the same.
    /// </summary>
    [Fact]
    public void Decode_UnresolvedJbig2Globals_ReportsInsteadOfDecodingBlankSilently()
    {
        var parms = new PdfDictionary();
        parms.Set(PdfName.JBIG2Globals, new PdfReference(99, 0));   // never resolved

        var filter = new Jbig2DecodeFilter();
        filter.Decode(new byte[] { 0x00, 0x01, 0x02, 0x03 }, parms);

        Assert.NotNull(Jbig2DecodeFilter.LastFallbackReason);
        Assert.Contains("JBIG2Globals", Jbig2DecodeFilter.LastFallbackReason, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>/JBIG2Globals</c> stream that resolves but cannot be decoded — here a Flate filter over
    /// bytes that are not deflate — is reported as such, and that report leads the fallback reason,
    /// because it is the root cause of the decode failure that follows.
    /// </summary>
    [Fact]
    public void Decode_Jbig2GlobalsThatFailToDecode_ReportTheGlobalsFailureFirst()
    {
        var globals = new PdfStream { RawData = new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF } };
        globals.Set(PdfName.Filter, PdfName.FlateDecode);
        var parms = new PdfDictionary();
        parms.Set(PdfName.JBIG2Globals, globals);

        new Jbig2DecodeFilter().Decode(new byte[] { 0x00, 0x01, 0x02, 0x03 }, parms);

        Assert.StartsWith("Failed to decode /JBIG2Globals stream: ", Jbig2DecodeFilter.LastGlobalsWarning);
        Assert.StartsWith("Failed to decode /JBIG2Globals stream: ", Jbig2DecodeFilter.LastFallbackReason);
    }

    /// <summary>
    /// Absent globals is a fact about the file, not a failure, so it must not produce the
    /// unresolved-globals report.
    /// </summary>
    /// <remarks>
    /// The payload here is not a valid JBIG2 stream, so the filter legitimately records some other
    /// fallback reason. This asserts the <b>specific</b> claim rather than "no reason at all": a
    /// channel that fires on every file is one a caller learns to ignore, which is the failure mode
    /// this repository already records for the render diagnostics.
    /// </remarks>
    [Fact]
    public void Decode_NoJbig2Globals_DoesNotReportUnresolvedGlobals()
    {
        var filter = new Jbig2DecodeFilter();
        filter.Decode(new byte[] { 0x00, 0x01, 0x02, 0x03 }, new PdfDictionary());

        string reason = Jbig2DecodeFilter.LastFallbackReason;
        Assert.True(reason == null || reason.IndexOf("JBIG2Globals", System.StringComparison.Ordinal) < 0,
            $"absent globals must not be reported as unresolved globals; got: {reason}");
    }

    /// <summary>
    /// The globals are one more stream of the image's and decode under the filter's own ceiling: a
    /// Flate globals stream inflating past <c>MaxOutputBytes</c> is refused and reported, where it
    /// once inflated under Core's 512 MB default whatever the caller allowed. The control is the same
    /// stream under a ceiling it fits.
    /// </summary>
    [Fact]
    public void Decode_Jbig2GlobalsPastTheFiltersCeiling_AreRefusedUnderIt()
    {
        var globals = new PdfStream { RawData = FlateEncodeFilter.Encode(new byte[64 * 1024]) };
        globals.Set(PdfName.Filter, PdfName.FlateDecode);
        var parms = new PdfDictionary();
        parms.Set(PdfName.JBIG2Globals, globals);

        new Jbig2DecodeFilter { MaxOutputBytes = 1024 }.Decode(new byte[] { 0x00, 0x01, 0x02, 0x03 }, parms);
        Assert.StartsWith("Failed to decode /JBIG2Globals stream: ", Jbig2DecodeFilter.LastGlobalsWarning);

        new Jbig2DecodeFilter { MaxOutputBytes = 1 << 20 }.Decode(new byte[] { 0x00, 0x01, 0x02, 0x03 }, parms);
        Assert.Null(Jbig2DecodeFilter.LastGlobalsWarning);
    }

    /// <summary>
    /// Globals are segments, never samples: a globals stream whose own filter is an image codec —
    /// one nesting another globals stream — is refused by name before anything is decoded, since each
    /// level would otherwise run a decoder under a budget of its own.
    /// </summary>
    [Fact]
    public void Decode_Jbig2GlobalsFilteredAsAnImageCodec_AreRefusedByName()
    {
        var globals = new PdfStream { RawData = new byte[] { 0x00, 0x00, 0x00, 0x00 } };
        globals.Set(PdfName.Filter, PdfName.JBIG2Decode);
        var parms = new PdfDictionary();
        parms.Set(PdfName.JBIG2Globals, globals);

        new Jbig2DecodeFilter().Decode(new byte[] { 0x00, 0x01, 0x02, 0x03 }, parms);

        Assert.Contains("image codec", Jbig2DecodeFilter.LastGlobalsWarning, System.StringComparison.Ordinal);
    }

    /// <summary>Object 8 is the globals stream; the image's /DecodeParms names it as `8 0 R`.</summary>
    private static byte[] PdfWithIndirectJbig2Globals()
    {
        const string globals = "\x00\x00\x00\x00";
        const string imageData = "\x00\x00\x00\x00";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /XObject << /Im0 5 0 R >> >> >>",
            StreamObject("", "q 10 0 0 10 0 0 cm /Im0 Do Q\n"),
            StreamObject("/Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 1 "
                + "/ColorSpace /DeviceGray /Filter /JBIG2Decode "
                + "/DecodeParms << /JBIG2Globals 8 0 R >>", imageData),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< >>",
            StreamObject("", globals),
        });
    }

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");

        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";
}
