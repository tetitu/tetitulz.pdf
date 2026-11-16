// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// A <c>/DecodeParms</c> <b>array element</b> written as an indirect reference must reach the
/// filter.
/// </summary>
/// <remarks>
/// <para><b>The defect was a missing write-back, not a missing resolve.</b>
/// <c>PdfParser.ResolveDecodeParmsStreamValues</c> did resolve the element — into a <i>local</i> —
/// and duly repaired the referenced dictionary. What it never did was assign it back:
/// <c>arr[i]</c> went on holding the <see cref="PdfReference"/>, and <c>FilterPipeline</c> reads
/// each element as <c>element as PdfDictionary</c>, which is <see langword="null"/> for a
/// reference. <b>The parameters were repaired and then thrown away.</b></para>
///
/// <para><b>Its own doc comment claimed otherwise</b> — "an element that is itself an indirect
/// reference is resolved through <c>GetObject</c>" — which was true of the local and unobservable
/// to every caller. A comment asserting a safety property is not the property; this repository has
/// now recorded that three times.</para>
///
/// <para><b>Asserted on the decoded bytes.</b> A predictor that is silently skipped still yields a
/// plausible, non-empty buffer — it is simply the un-predicted data, one byte per row longer and
/// wrong from the first row on. Asserting "decode returned something" passes with the defect
/// present.</para>
/// </remarks>
public class IndirectDecodeParmsElementTests
{
    /// <summary>The two rows the fixture encodes, after the PNG Up predictor is undone.</summary>
    private static readonly byte[] Expected = { 10, 20, 30, 40, 11, 22, 33, 44 };

    [Theory]
    [InlineData(true)]   // /DecodeParms [7 0 R]
    [InlineData(false)]  // /DecodeParms [<< /Predictor 12 … >>]
    public void PredictorInAnIndirectArrayElement_IsApplied(bool indirect)
    {
        using var ms = new MemoryStream(PredictedStreamPdf(indirect));
        using var doc = PdfDocument.Open(ms);

        var stream = doc.ResolveObject(new PdfReference(5, 0)) as PdfStream;
        Assert.NotNull(stream);

        Assert.Equal(Expected, stream!.GetDecodedData());
    }

    /// <summary>
    /// A <c>/Filter</c> <b>array element</b> written as an indirect reference is that filter.
    /// </summary>
    /// <remarks>
    /// The sibling of the <c>/DecodeParms</c> element fix, one key over. The parser resolved a
    /// <c>/Filter</c> that was itself a reference but not the names inside an array, so
    /// <c>/Filter [8 0 R]</c> reached <c>FilterPipeline</c> as a reference where a name belongs, and
    /// the stage was dropped: reported through <c>LastUnknownFilterName</c>, and the stream handed
    /// back still compressed.
    /// </remarks>
    [Theory]
    [InlineData(false)]   // /Filter [/FlateDecode]
    [InlineData(true)]    // /Filter [8 0 R]
    public void AnIndirectFilterArrayElement_IsApplied(bool indirectFilter)
    {
        using var ms = new MemoryStream(PredictedStreamPdf(indirect: false, indirectFilter: indirectFilter));
        using var doc = PdfDocument.Open(ms);

        var stream = doc.ResolveObject(new PdfReference(5, 0)) as PdfStream;
        Assert.NotNull(stream);

        Assert.Equal(Expected, stream!.GetDecodedData());
    }

    /// <summary>
    /// A <c>/DecodeParms</c> <b>dictionary</b> beside a one-element <c>/Filter</c> array is that
    /// filter's parameters.
    /// </summary>
    /// <remarks>
    /// §7.3.8.2 makes the dictionary the required form for one filter, and a one-element array is
    /// one filter. <c>FilterPipeline</c> paired an array <c>/Filter</c> only with an array
    /// <c>/DecodeParms</c>, so this conformant shape decoded with no predictor. Found by the test
    /// below, whose dictionary-form cases failed for this reason before the entry inside was ever
    /// read — which is why this one exists separately, with every value direct.
    /// </remarks>
    [Fact]
    public void ADictionaryDecodeParmsForAOneElementFilterArray_IsApplied()
    {
        using var ms = new MemoryStream(PredictedStreamPdf(indirect: false, indirectColumns: false, asArray: false));
        using var doc = PdfDocument.Open(ms);

        var stream = doc.ResolveObject(new PdfReference(5, 0)) as PdfStream;
        Assert.NotNull(stream);

        Assert.Equal(Expected, stream!.GetDecodedData());
    }

    /// <summary>
    /// A <b>scalar entry</b> inside <c>/DecodeParms</c> written as an indirect reference reaches the
    /// filter too — with the parameters written as a dictionary, as an array element, and as a
    /// reference to either.
    /// </summary>
    /// <remarks>
    /// The parser resolved <c>/DecodeParms</c> itself, its array elements and <c>/JBIG2Globals</c>
    /// inside it, and no other entry. The filters run with no document, so <c>/Columns 6 0 R</c> read
    /// as absent and the predictor ran with its default of one column — the same wrong-from-the-first-
    /// row output this file's first test describes, reached one level further down.
    /// </remarks>
    [Theory]
    [InlineData(false, true)]    // /DecodeParms [<< … /Columns 6 0 R >>]
    [InlineData(false, false)]   // /DecodeParms << … /Columns 6 0 R >>
    [InlineData(true, true)]     // /DecodeParms [7 0 R], 7 holding /Columns 6 0 R
    [InlineData(true, false)]    // /DecodeParms 7 0 R, 7 holding /Columns 6 0 R
    public void AnIndirectEntryInsideDecodeParms_IsApplied(bool indirectParms, bool asArray)
    {
        using var ms = new MemoryStream(PredictedStreamPdf(indirectParms, indirectColumns: true, asArray: asArray));
        using var doc = PdfDocument.Open(ms);

        var stream = doc.ResolveObject(new PdfReference(5, 0)) as PdfStream;
        Assert.NotNull(stream);

        Assert.Equal(Expected, stream!.GetDecodedData());
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Object 5 is a Flate stream carrying PNG-Up-predicted data; object 7 is the
    /// <c>/DecodeParms</c> dictionary that says so, and object 6 its column count.
    /// </summary>
    private static byte[] PredictedStreamPdf(bool indirect, bool indirectColumns = false, bool asArray = true,
                                             bool indirectFilter = false)
    {
        // PNG Up (filter type 2): each row is preceded by its filter byte and stored as the
        // difference from the row above. Row 0's predecessor is all zeroes.
        byte[] predicted =
        {
            2, 10, 20, 30, 40,   // row 0 — 10,20,30,40 minus zeroes
            2,  1,  2,  3,  4,   // row 1 — 11,22,33,44 minus row 0
        };
        byte[] compressed = Deflate(predicted);

        string parms = "<< /Predictor 12 /Colors 1 /BitsPerComponent 8 /Columns "
                       + (indirectColumns ? "6 0 R" : "4") + " >>";

        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            StreamObj("", "q Q\n"),
            null,   // 5 — the predicted stream, spliced in as raw bytes below
            "4",    // 6 — the column count, referenced only when indirectColumns
            parms,  // 7 — the parms dictionary, referenced only in the indirect case
            "/FlateDecode",   // 8 — the filter name, referenced only when indirectFilter
        };

        // Object 5 has binary data, so it is assembled with the byte writer rather than as text.
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        var bytes = new MemoryStream();

        void Emit(string s) => bytes.Write(Encoding.Latin1.GetBytes(s), 0, s.Length);

        Emit("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add((int)bytes.Position);
            if (i == 4)
            {
                Emit("5 0 obj\n<< /Length " + compressed.Length.ToString(CultureInfo.InvariantCulture) +
                     " /Filter [" + (indirectFilter ? "8 0 R" : "/FlateDecode") + "] /DecodeParms " +
                     (asArray ? "[" + (indirect ? "7 0 R" : parms) + "]" : (indirect ? "7 0 R" : parms)) +
                     " >>\nstream\n");
                bytes.Write(compressed, 0, compressed.Length);
                Emit("\nendstream\nendobj\n");
            }
            else
            {
                Emit((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + bodies[i] + "\nendobj\n");
            }
        }

        int xref = (int)bytes.Position;
        Emit("xref\n0 " + (bodies.Count + 1).ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Emit("trailer\n<< /Size " + (bodies.Count + 1).ToString(CultureInfo.InvariantCulture) +
             " /Root 1 0 R >>\nstartxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return bytes.ToArray();
    }

    /// <summary>zlib-wrapped deflate, which is what <c>/FlateDecode</c> means (§7.4.4).</summary>
    private static byte[] Deflate(byte[] raw)
    {
        using var outMs = new MemoryStream();
        using (var z = new ZLibStream(outMs, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return outMs.ToArray();
    }

    private static string StreamObj(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";
}
