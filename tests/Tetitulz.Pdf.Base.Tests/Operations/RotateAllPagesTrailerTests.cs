// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The trailer <see cref="PdfOperations.RotateAllPages"/> appends must be
/// <c>Core.Writing.PdfTrailerBuilder</c>'s — it must not carry the keys that described the
/// cross-reference <b>section it superseded</b>.
/// </summary>
/// <remarks>
/// <para><b>Why this operation and not another.</b> <c>RotateAllPagesIn</c> was the one place in
/// the tree that still built an appended-revision trailer <i>inline</i>: a new dictionary, every
/// key of <c>doc.Trailer</c> copied forward, <c>/Size</c> restated. That is
/// <c>PdfTrailerBuilder.ForUpdate</c>'s body written out, minus its deny list — so it carried
/// <c>/Type /XRef</c>, <c>/W</c>, <c>/Index</c>, <c>/Filter</c>, <c>/DecodeParms</c> and a stale
/// <c>/Length</c> forward for any PDF 1.5+ document, whose <c>PdfDocument.Trailer</c> <i>is</i> the
/// cross-reference stream's own dictionary as the parser presents it. It survived the
/// consolidation that routed eight other copies into the builder because <b>it had no method name
/// to grep for</b>.</para>
///
/// <para><b>It was masked, not live</b>, and that is precisely why it needed a test rather than a
/// shrug: the writer hands whatever trailer it is given to
/// <c>PdfTrailerBuilder.ForAppendedRevision</c>, which strips those keys again a moment later. That
/// is a fact about the current call order, not a property of the caller. This suite pins the
/// end-to-end outcome, so the copy could be deleted with the survivor demonstrably covered
/// <i>from this path</i> — a merge whose survivor no test reaches is a deletion, and a green suite
/// cannot tell the two apart.</para>
///
/// <para>The fixture is a real cross-reference <b>stream</b> document, because that is the only
/// shape whose trailer carries the offending keys at all. The appended section is a classic
/// <c>xref</c> table (the six-argument writer's format), so §7.5.5 governs the trailer it
/// writes.</para>
/// </remarks>
public sealed class RotateAllPagesTrailerTests
{
    /// <summary>
    /// Exactly the keys a §7.5.5 trailer for this appended revision may carry: the document's own,
    /// plus the chain pointer this revision writes for itself.
    /// </summary>
    private static readonly string[] ExpectedTrailerKeys = { "Info", "Prev", "Root", "Size" };

    /// <summary>
    /// <b>The claim.</b> Every name in the appended trailer is accounted for — which denies
    /// <c>/Type</c>, <c>/W</c>, <c>/Index</c>, <c>/Filter</c>, <c>/DecodeParms</c> and
    /// <c>/Length</c> without having to enumerate them, and would equally catch a key nobody
    /// thought to name.
    /// </summary>
    [Fact]
    public void TheAppendedTrailerCarriesNoKeyOfTheCrossReferenceStreamItSupersedes()
    {
        byte[] original = XrefStreamPdf();
        string trailer = AppendedTrailerOf(original, Rotate(original));

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(trailer, @"/([A-Za-z][A-Za-z0-9]*)"))
            names.Add(m.Groups[1].Value);

        Assert.Equal(ExpectedTrailerKeys, names);
    }

    /// <summary>
    /// <b>The premise.</b> The keys denied above really are in the document's trailer as the parser
    /// presents it — otherwise the assertion above would pass against a fixture that never carried
    /// them, which is the shape of a test that cannot fail.
    /// </summary>
    [Theory]
    [InlineData("Type")]
    [InlineData("W")]
    [InlineData("Length")]
    public void TheFixturesOwnTrailerCarriesTheCrossReferenceStreamsKeys(string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(XrefStreamPdf()));

        Assert.True(doc.Trailer.ContainsKey(new PdfName(key)),
            $"the fixture must reach the writer with /{key} in its trailer, or the deny list is untested");
    }

    /// <summary>
    /// <b>The floor.</b> The document keys must survive, and the revision must actually have done
    /// its work — a trailer stripped down to nothing, or an operation that copied the input
    /// through, would satisfy the first assertion perfectly.
    /// </summary>
    [Fact]
    public void TheRotationIsAppliedAndTheDocumentKeysSurvive()
    {
        byte[] original = XrefStreamPdf();
        byte[] rotated = Rotate(original);
        string trailer = AppendedTrailerOf(original, rotated);

        Assert.Contains("/Root", trailer, StringComparison.Ordinal);
        Assert.Contains("/Prev", trailer, StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(rotated));
        Assert.Single(doc.Pages);
        Assert.Equal(90, (int)((doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Rotate])
                                as PdfNumber).LongValue));
    }

    // ------------------------------------------------------------------

    private static byte[] Rotate(byte[] original)
    {
        using var input = new MemoryStream(original, writable: false);
        using var output = new MemoryStream();
        PdfOperations.RotateAllPages(input, output, 90);
        return output.ToArray();
    }

    /// <summary>
    /// The dictionary of the trailer written by the appended revision — the bytes between the last
    /// <c>trailer</c> keyword and the <c>startxref</c> that follows it. The original is copied
    /// verbatim, so everything past its length belongs to the revision under test.
    /// </summary>
    private static string AppendedTrailerOf(byte[] original, byte[] produced)
    {
        Assert.True(produced.Length > original.Length, "the operation appended nothing");

        string appended = Encoding.Latin1.GetString(
            produced, original.Length, produced.Length - original.Length);

        int keyword = appended.LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.True(keyword >= 0, "the appended revision wrote no classic trailer");

        int open = appended.IndexOf("<<", keyword, StringComparison.Ordinal);
        int close = appended.IndexOf(">>", open, StringComparison.Ordinal);
        Assert.True(open > 0 && close > open, "the appended trailer is not a dictionary");

        return appended.Substring(open, close - open + 2);
    }

    /// <summary>
    /// A PDF whose cross-reference section is a <b>cross-reference stream</b> (object 5). The
    /// stream is uncompressed with <c>/W [1 2 2]</c> so it can be built by hand and read back
    /// without a filter round-trip.
    /// </summary>
    private static byte[] XrefStreamPdf()
    {
        var ms = new MemoryStream();
        void Ascii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        Ascii("%PDF-1.5\n");

        var offsets = new int[6];
        string[] bodies =
        {
            null,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Title (Xref Stream Original) >>",
        };

        for (int n = 1; n <= 4; n++)
        {
            offsets[n] = (int)ms.Position;
            Ascii(n.ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + bodies[n] + "\nendobj\n");
        }

        // The xref stream is itself an indirect object and needs an entry for itself, so its
        // offset must be known before its payload is built.
        offsets[5] = (int)ms.Position;

        var data = new byte[6 * 5];   // 6 entries, /W [1 2 2] => 5 bytes each
        void Entry(int i, byte type, int offset, int generation)
        {
            int p = i * 5;
            data[p] = type;
            data[p + 1] = (byte)((offset >> 8) & 0xFF);
            data[p + 2] = (byte)(offset & 0xFF);
            data[p + 3] = (byte)((generation >> 8) & 0xFF);
            data[p + 4] = (byte)(generation & 0xFF);
        }

        Entry(0, 0, 0, 65535);                                  // the free-list head
        for (int n = 1; n <= 5; n++) Entry(n, 1, offsets[n], 0);

        Ascii("5 0 obj\n<< /Type /XRef /Size 6 /W [1 2 2] /Root 1 0 R /Info 4 0 R /Length " +
              data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        ms.Write(data, 0, data.Length);
        Ascii("\nendstream\nendobj\n");
        Ascii("startxref\n" + offsets[5].ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return ms.ToArray();
    }
}
