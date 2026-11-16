// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// What a revision appended after a signature may do, asked of the shapes a forger chooses: an object
/// is judged by what it was in the signed revision, not by what its rewrite says it is, and a change
/// that writes no object — a cross-reference entry or a trailer pointing somewhere else — is a change.
/// </summary>
public sealed class SignedRevisionRewriteTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string Pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string Page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [6 0 R] >>";
    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
    private const string Annotation = "<< /Type /Annot /Subtype /Square /Rect [0 0 1 1] >>";

    private static string Stream(string text, string dict = "") =>
        $"<< {dict} /Length {text.Length} >>\nstream\n{text}\nendstream";

    /// <summary>The signed revision's objects, numbered from 1: a catalog, its page, its content, a font, its annotation.</summary>
    private static string[] Signed(params string[] more) =>
        new[] { Catalog, Pages, Page, Stream("(signed) Tj"), Font, Annotation }.Concat(more).ToArray();

    private static byte[] Assemble(string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static long StartXref(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.LastIndexOf("startxref", System.StringComparison.Ordinal);
        return long.Parse(text.Substring(at + 9, text.IndexOf("%%EOF", at, System.StringComparison.Ordinal) - at - 9).Trim(), CultureInfo.InvariantCulture);
    }

    /// <summary>A revision rewriting the objects given; the entries are the offsets the caller wants, when it wants any.</summary>
    private static byte[] Revise(byte[] pdf, IReadOnlyDictionary<int, string> objects, IReadOnlyDictionary<int, long> entries = null, string root = "1 0 R")
    {
        var sb = new StringBuilder(Encoding.Latin1.GetString(pdf));
        var at = new SortedDictionary<int, long>();
        foreach (var o in objects)
        {
            at[o.Key] = sb.Length;
            sb.Append(o.Key).Append(" 0 obj\n").Append(o.Value).Append("\nendobj\n");
        }
        if (entries != null) foreach (var e in entries) at[e.Key] = e.Value;

        long xref = sb.Length;
        sb.Append("xref\n");
        foreach (var e in at)
            sb.Append(e.Key).Append(" 1\n").Append(e.Value.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        if (at.Count == 0) sb.Append("0 1\n0000000000 65535 f \n");
        sb.Append("trailer\n<< /Size 100 /Root ").Append(root).Append(" /Prev ").Append(StartXref(pdf)).Append(" >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static DocMdpAnalysis Judge(byte[] changed, long signedEnd, int permission)
    {
        var ms = new MemoryStream(changed);
        using PdfDocument doc = PdfDocument.Open(ms);
        return DocMdpAnalyzer.AnalyzeChangesSince(doc, signedEnd, changed.Length, permission);
    }

    private static void AssertRefused(byte[] signed, IReadOnlyDictionary<int, string> objects, int permission)
    {
        DocMdpAnalysis analysis = Judge(Revise(signed, objects), signed.Length, permission);
        Assert.False(analysis.IsCompliant, "a forged rewrite passed: " + analysis.Summary);
    }

    private static void AssertAccepted(byte[] signed, IReadOnlyDictionary<int, string> objects, int permission)
    {
        DocMdpAnalysis analysis = Judge(Revise(signed, objects), signed.Length, permission);
        Assert.True(analysis.IsCompliant, "a permitted change was refused: " + analysis.Summary);
    }

    // ---------------------------------------------------------------- a rewrite naming itself something else

    /// <summary>
    /// <b>A page's content stream, rewritten under a dictionary that calls it something the classifier
    /// permits.</b> A viewer reads no such key on a content stream; the new bytes are what it draws.
    /// </summary>
    [Theory]
    [InlineData("/Type /DocTimeStamp", 1)]
    [InlineData("/Type /DocTimeStamp", 2)]
    [InlineData("/Type /DocTimeStamp", 3)]
    [InlineData("/SubFilter /ETSI.RFC3161 /Type /Sig", 1)]
    [InlineData("/ByteRange [0 0 0 0] /Contents <00>", 3)]
    [InlineData("/Subtype /X /Rect [0 0 1 1]", 3)]
    [InlineData("/Type /Annot", 3)]
    [InlineData("/Type /Catalog", 3)]
    [InlineData("/FT /Sig", 3)]
    [InlineData("/T (x)", 3)]
    public void AdversarialContentStream_RewrittenAsSomethingPermitted_IsRefused(string keys, int permission)
    {
        AssertRefused(Assemble(Signed()), new Dictionary<int, string> { [4] = Stream("(forged) Tj", keys) }, permission);
    }

    /// <summary>A stream's dictionary unchanged and only its bytes new: no key says so.</summary>
    [Theory]
    [InlineData("/T (x)", 3)]
    [InlineData("/FT /Tx", 3)]
    [InlineData("/Subtype /Widget", 2)]
    [InlineData("/Subtype /X /Rect [0 0 1 1]", 3)]
    public void AdversarialContentStream_PreparedWithAFieldsKeys_IsRefusedWhenItsBytesChange(string keys, int permission)
    {
        string[] bodies = Signed();
        bodies[3] = Stream("(signed) Tj", keys);
        AssertRefused(Assemble(bodies), new Dictionary<int, string> { [4] = Stream("(forged) Tj", keys) }, permission);
    }

    /// <summary>The page, the font and the catalog's page tree: each answers to what it was, whatever it now says.</summary>
    [Theory]
    [InlineData(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [6 0 R] /Rotate 90 /Subtype /X /Rect [0 0 1 1] >>")]
    [InlineData(5, "<< /Type /Annot /Subtype /Type1 /BaseFont /Symbol /Rect [0 0 1 1] >>")]
    [InlineData(2, "<< /Type /Pages /Kids [] /Count 0 /Subtype /X /Rect [0 0 1 1] >>")]
    public void AdversarialObject_RewrittenAsAnAnnotation_IsRefusedAtLevelThree(int number, string rewrite)
    {
        AssertRefused(Assemble(Signed()), new Dictionary<int, string> { [number] = rewrite }, 3);
    }

    /// <summary>An untyped object shaped like an annotation in the signed revision is one only where a page lists it.</summary>
    [Fact]
    public void AdversarialObject_ShapedLikeAnAnnotationButListedByNoPage_IsRefused()
    {
        byte[] signed = Assemble(Signed("<< /Subtype /X /Rect [0 0 1 1] /Rotate 0 >>"));
        AssertRefused(signed, new Dictionary<int, string> { [7] = "<< /Subtype /X /Rect [0 0 1 1] /Rotate 90 >>" }, 3);
    }

    /// <summary>A widget is a field's face: rewriting it as a mere annotation is not annotation editing.</summary>
    [Fact]
    public void AdversarialWidget_RewrittenAsAnAnnotation_IsRefused()
    {
        string[] bodies = Signed();
        bodies[5] = "<< /Type /Annot /Subtype /Widget /FT /Sig /T (s) /Rect [0 0 1 1] >>";
        AssertRefused(Assemble(bodies), new Dictionary<int, string> { [6] = Annotation }, 3);
    }

    /// <summary>A signature dictionary, once written, is not rewritten, as a timestamp or as anything else.</summary>
    [Theory]
    [InlineData("<< /Type /DocTimeStamp /SubFilter /ETSI.RFC3161 /ByteRange [0 1 2 3] /Contents <00> >>")]
    [InlineData("<< /Type /Annot /Subtype /Square /Rect [0 0 1 1] >>")]
    public void AdversarialSignatureDictionary_Rewritten_IsRefused(string rewrite)
    {
        byte[] signed = Assemble(Signed("<< /Type /DocTimeStamp /SubFilter /ETSI.RFC3161 /ByteRange [0 1 2 3] /Contents <11> >>"));
        AssertRefused(signed, new Dictionary<int, string> { [7] = rewrite }, 3);
    }

    /// <summary>The controls: what Table 257 permits at each level, written as a producer writes it.</summary>
    [Fact]
    public void AnAnnotationAPageListsMayChangeAtLevelThreeAndNotBelow()
    {
        byte[] signed = Assemble(Signed());
        var change = new Dictionary<int, string> { [6] = "<< /Type /Annot /Subtype /Square /Rect [0 0 9 9] >>" };

        AssertAccepted(signed, change, 3);
        AssertRefused(signed, change, 2);
    }

    [Fact]
    public void ANewAnnotationListedByItsPageIsPermittedAtLevelThree()
    {
        AssertAccepted(Assemble(Signed()), new Dictionary<int, string>
        {
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [6 0 R 7 0 R] >>",
            [7] = Annotation,
        }, 3);
    }

    [Fact]
    public void ADocumentTimestampAddedIsPermittedAtEveryLevel()
    {
        byte[] signed = Assemble(Signed());
        var added = new Dictionary<int, string> { [7] = "<< /Type /DocTimeStamp /SubFilter /ETSI.RFC3161 /ByteRange [0 1 2 3] /Contents <00> >>" };

        AssertAccepted(signed, added, 1);
        AssertAccepted(signed, added, 3);
    }

    /// <summary>
    /// A new stream is no timestamp whatever it calls itself: a signature value is a dictionary, and the
    /// bytes are what a later fill could name as an appearance at a level that permits no appearance.
    /// </summary>
    [Fact]
    public void AdversarialNewStream_NamedATimestamp_IsRefusedAtLevelOne()
    {
        AssertRefused(Assemble(Signed()), new Dictionary<int, string> { [7] = Stream("(forged) Tj", "/Type /DocTimeStamp") }, 1);
    }

    // ---------------------------------------------------------------- a change that writes no object

    /// <summary>
    /// <b>A revision of nothing but a cross-reference entry.</b> The object's earlier version is still in
    /// the signed bytes; pointing back at it writes no object past the signature, and the reader draws
    /// what the signer never saw.
    /// </summary>
    [Fact]
    public void AdversarialCrossReferenceEntry_PointingBackIntoTheSignedBytes_IsRefused()
    {
        byte[] first = Assemble(Signed());
        long hidden = Encoding.Latin1.GetString(first).IndexOf("4 0 obj", System.StringComparison.Ordinal);
        byte[] signed = Revise(first, new Dictionary<int, string> { [4] = Stream("(what the signer saw) Tj") });

        byte[] forged = Revise(signed, new Dictionary<int, string>(), new Dictionary<int, long> { [4] = hidden });
        DocMdpAnalysis analysis = Judge(forged, signed.Length, 1);

        Assert.False(analysis.IsCompliant, analysis.Summary);
        Assert.Contains(analysis.Violations, v => v.StartsWith("obj 4:", System.StringComparison.Ordinal));
    }

    /// <summary>The same pointer, into the same place, is no change at all: a revision that repeats an entry.</summary>
    [Fact]
    public void ACrossReferenceEntryNamingTheSameBytes_IsNoChange()
    {
        byte[] signed = Assemble(Signed());
        long same = Encoding.Latin1.GetString(signed).IndexOf("4 0 obj", System.StringComparison.Ordinal);

        byte[] repeated = Revise(signed, new Dictionary<int, string>(), new Dictionary<int, long> { [4] = same });
        DocMdpAnalysis analysis = Judge(repeated, signed.Length, 1);

        Assert.True(analysis.IsCompliant, analysis.Summary);
    }

    /// <summary>
    /// <b>The same through an object stream.</b> The signed bytes hold a stream with another version of
    /// the content's dictionary inside; a cross-reference stream appended later says the object lives there.
    /// </summary>
    [Fact]
    public void AdversarialCrossReferenceStream_PointingIntoASignedObjectStream_IsRefused()
    {
        const string Member = "<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>";
        byte[] signed = Assemble(Signed(Stream("5 0 " + Member, "/Type /ObjStm /N 1 /First 4")));

        // Object 5 now in object stream 7 at index 0; object 8 is this cross-reference stream.
        var sb = new StringBuilder(Encoding.Latin1.GetString(signed));
        long xref = sb.Length;
        string entries = "02" + "00000007" + "0000" + "01" + xref.ToString("X8", CultureInfo.InvariantCulture) + "0000" + ">";
        sb.Append("8 0 obj\n<< /Type /XRef /Size 9 /W [1 4 2] /Index [5 1 8 1] /Root 1 0 R /Prev ")
          .Append(StartXref(signed)).Append(" /Filter /ASCIIHexDecode /Length ").Append(entries.Length)
          .Append(" >>\nstream\n").Append(entries).Append("\nendstream\nendobj\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        byte[] forged = Encoding.Latin1.GetBytes(sb.ToString());

        DocMdpAnalysis analysis = Judge(forged, signed.Length, 3);

        Assert.Contains(analysis.Violations, v => v.StartsWith("obj 5:", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// One object stream, another member: a signed compressed entry moved within what is signed. The
    /// entry names no bytes of its own, so its stream and index are all that say where it stands.
    /// </summary>
    [Fact]
    public void AdversarialCompressedEntry_MovedWithinASignedObjectStream_IsRefused()
    {
        const string First = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
        const string Second = "<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>";
        string members = First + " " + Second;
        string head = "5 0 5 " + (First.Length + 1) + " ";
        byte[] first = Assemble(Signed(Stream(head + members, "/Type /ObjStm /N 2 /First " + head.Length)));

        byte[] Entry(byte[] pdf, int number, int index, int self)
        {
            var sb = new StringBuilder(Encoding.Latin1.GetString(pdf));
            long xref = sb.Length;
            string entries = "02" + "00000007" + index.ToString("X4", CultureInfo.InvariantCulture)
                             + "01" + xref.ToString("X8", CultureInfo.InvariantCulture) + "0000" + ">";
            sb.Append(self).Append(" 0 obj\n<< /Type /XRef /Size ").Append(self + 1).Append(" /W [1 4 2] /Index [").Append(number).Append(" 1 ").Append(self)
              .Append(" 1] /Root 1 0 R /Prev ").Append(StartXref(pdf)).Append(" /Filter /ASCIIHexDecode /Length ").Append(entries.Length)
              .Append(" >>\nstream\n").Append(entries).Append("\nendstream\nendobj\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            return Encoding.Latin1.GetBytes(sb.ToString());
        }

        byte[] signed = Entry(first, 5, 0, 8);
        byte[] forged = Entry(signed, 5, 1, 9);

        Assert.DoesNotContain(Judge(Entry(signed, 5, 0, 9), signed.Length, 3).Violations, v => v.StartsWith("obj 5:", System.StringComparison.Ordinal));
        Assert.Contains(Judge(forged, signed.Length, 3).Violations, v => v.StartsWith("obj 5:", System.StringComparison.Ordinal));
    }

    /// <summary>A signed revision that cannot be opened cannot be compared with: what changed since is not established.</summary>
    [Fact]
    public void AdversarialSignedRange_EndingWhereNoDocumentIs_IsRefused()
    {
        byte[] pdf = Assemble(Signed());

        DocMdpAnalysis analysis = Judge(pdf, 4, 3);

        Assert.False(analysis.IsCompliant);
        Assert.Contains(analysis.Violations, v => v.Contains("could not be read", System.StringComparison.Ordinal));
    }

    /// <summary>A freed object: what a page lists may go at level 3, nothing else may, whatever it looks like.</summary>
    [Theory]
    [InlineData(6, 3, true)]
    [InlineData(6, 2, false)]
    [InlineData(4, 3, false)]
    [InlineData(7, 3, false)]
    public void AFreedObject_IsAPermittedDeletionOnlyIfAPageListedItAsAnAnnotation(int number, int permission, bool permitted)
    {
        byte[] signed = Assemble(Signed("<< /Subtype /X /Rect [0 0 1 1] /Rotate 0 >>"));

        var sb = new StringBuilder(Encoding.Latin1.GetString(signed));
        long xref = sb.Length;
        sb.Append("xref\n").Append(number).Append(" 1\n0000000000 00001 f \ntrailer\n<< /Size 100 /Root 1 0 R /Prev ")
          .Append(StartXref(signed)).Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        DocMdpAnalysis analysis = Judge(Encoding.Latin1.GetBytes(sb.ToString()), signed.Length, permission);

        Assert.Equal(permitted, analysis.IsCompliant);
    }

    /// <summary>A revision that names an object the signed bytes hold unused as the document's catalog.</summary>
    [Fact]
    public void AdversarialTrailer_NamingAnotherCatalog_IsRefused()
    {
        byte[] signed = Assemble(Signed("<< /Type /Catalog /Pages 8 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>"));

        byte[] forged = Revise(signed, new Dictionary<int, string>(), root: "7 0 R");
        DocMdpAnalysis analysis = Judge(forged, signed.Length, 1);

        Assert.False(analysis.IsCompliant, analysis.Summary);
        Assert.Contains(analysis.Violations, v => v.Contains("/Root", System.StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- what a page and a form list

    private const string PageWithoutAnnots = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>";

    /// <summary>
    /// <b>Dropping an annotation from <c>/Annots</c> frees nothing.</b> Below level 3 an annotation may
    /// not be deleted, however that is done; at level 3 it may.
    /// </summary>
    [Fact]
    public void AdversarialAnnotsEntry_Removed_IsRefusedBelowLevelThree()
    {
        byte[] signed = Assemble(Signed());
        var removed = new Dictionary<int, string> { [3] = PageWithoutAnnots };

        AssertRefused(signed, removed, 2);
        AssertAccepted(signed, removed, 3);
    }

    /// <summary>The same with the list still there: a new field added, the old annotation dropped in passing.</summary>
    [Fact]
    public void AdversarialAnnotsEntry_ReplacedByANewOne_IsRefusedBelowLevelThree()
    {
        AssertRefused(Assemble(Signed()), new Dictionary<int, string>
        {
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [7 0 R] >>",
            [7] = "<< /FT /Sig /Subtype /Widget /T (s) /Rect [0 0 1 1] >>",
        }, 2);
    }

    /// <summary>An object the signed bytes hold unused, drawn on the page by listing it: nothing new to classify.</summary>
    [Fact]
    public void AdversarialAnnotsEntry_ListingAnUnusedSignedObject_IsRefusedBelowLevelThree()
    {
        byte[] signed = Assemble(Signed("<< /FT /Sig /Subtype /Widget /T (shadow) /Rect [0 0 100 100] >>"));
        var listed = new Dictionary<int, string> { [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [6 0 R 7 0 R] >>" };

        AssertRefused(signed, listed, 2);
    }

    /// <summary>The control: a new signature field, listed by its page and its form, at level 2.</summary>
    [Fact]
    public void ANewSignatureFieldIsPermittedAtLevelTwo()
    {
        AssertAccepted(Assemble(Signed()), new Dictionary<int, string>
        {
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [6 0 R 7 0 R] >>",
            [7] = "<< /FT /Sig /Subtype /Widget /T (s) /Rect [0 0 1 1] >>",
        }, 2);
    }

    /// <summary>
    /// A field the classifier admits as a signature field by its <c>/FT</c> alone: an action runs when
    /// the field is touched, and another subtype makes it an annotation level 2 does not permit.
    /// </summary>
    [Theory]
    [InlineData("<< /FT /Sig /Subtype /Widget /T (s) /Rect [0 0 1 1] /A << /S /JavaScript /JS (app.alert(1)) >> >>")]
    [InlineData("<< /FT /Sig /Subtype /Widget /T (s) /Rect [0 0 1 1] /AA << /E << /S /JavaScript /JS (app.alert(1)) >> >> >>")]
    [InlineData("<< /FT /Sig /Subtype /FreeText /T (s) /Rect [0 0 1 1] /Contents (forged) >>")]
    public void AdversarialNewSignatureField_CarryingWhatASignatureFieldDoesNot_IsRefused(string field)
    {
        AssertRefused(Assemble(Signed()), new Dictionary<int, string>
        {
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Annots [6 0 R 7 0 R] >>",
            [7] = field,
        }, 2);
    }

    private static string[] WithForm(string form, params string[] more)
    {
        string[] bodies = Signed(more);
        bodies[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm " + form + " >>";
        return bodies;
    }

    /// <summary>
    /// <b>A signed field's value is the signature.</b> <c>/V</c> is a fill-in key, so a rewrite of it
    /// passed as filling the field: a genuine signature gone, a decoy in its place. A widget taking its
    /// <c>/FT</c> from its parent is the same field.
    /// </summary>
    [Theory]
    [InlineData("<< /FT /Sig /T (s) /V 8 0 R >>", "<< /FT /Sig /T (s) /V 9 0 R >>")]
    [InlineData("<< /Subtype /Widget /T (s) /V 8 0 R /Rect [0 0 1 1] >>", "<< /Subtype /Widget /T (s) /V 9 0 R /Rect [0 0 1 1] >>")]
    [InlineData("<< /FT /Sig /T (s) /V 8 0 R >>", "<< /FT /Sig /T (s) >>")]
    public void AdversarialSignedField_ItsSignatureReplaced_IsRefused(string signed, string rewrite)
    {
        const string Sig = "<< /Type /Sig /ByteRange [0 1 2 3] /Contents <00> >>";

        AssertRefused(Assemble(Signed(signed, Sig, Sig)), new Dictionary<int, string> { [7] = rewrite }, 3);
    }

    /// <summary>The control: signing a field with no signature yet, its appearance with it, is what level 2 is for.</summary>
    [Fact]
    public void AnEmptySignatureField_MayBeSigned()
    {
        byte[] signed = Assemble(Signed("<< /FT /Sig /T (s) >>", "<< /Length 0 >>\nstream\n\nendstream"));

        AssertAccepted(signed, new Dictionary<int, string>
        {
            [7] = "<< /FT /Sig /T (s) /V 9 0 R >>",
            [9] = "<< /Type /Sig /ByteRange [0 1 2 3] /Contents <00> >>",
        }, 2);
    }

    /// <summary>A form's <c>/Fields</c> only grows: dropping a field is no fill-in, whichever way the form is held.</summary>
    [Fact]
    public void AdversarialFormField_DroppedFromFields_IsRefused()
    {
        const string Field = "<< /FT /Tx /T (a) >>";

        byte[] inline = Assemble(WithForm("<< /Fields [7 0 R] >>", Field));
        AssertRefused(inline, new Dictionary<int, string> { [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] >> >>" }, 2);

        byte[] indirect = Assemble(WithForm("8 0 R", Field, "<< /Fields [7 0 R] >>"));
        AssertRefused(indirect, new Dictionary<int, string> { [8] = "<< /Fields [] >>" }, 2);
    }

    /// <summary>The form swapped for another the signed bytes already hold, or a new one without what the old held.</summary>
    [Fact]
    public void AdversarialForm_SwappedThroughTheCatalog_IsRefused()
    {
        const string Field = "<< /FT /Tx /T (a) >>";

        byte[] swapped = Assemble(WithForm("8 0 R", Field, "<< /Fields [7 0 R] >>", "<< /Fields [] /XFA (shadow) >>"));
        AssertRefused(swapped, new Dictionary<int, string> { [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>" }, 2);

        byte[] inline = Assemble(WithForm("<< /Fields [7 0 R] /XFA 8 0 R >>", Field, Stream("<xdp/>")));
        AssertRefused(inline, new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm 9 0 R >>",
            [9] = "<< /Fields [7 0 R] >>",
        }, 2);
    }

    /// <summary>
    /// A document with no form given one the signed bytes hold unused, with its fields: none of them is
    /// written later, so nothing else classifies any of them.
    /// </summary>
    [Fact]
    public void AdversarialForm_AnUnusedSignedOne_IsRefused()
    {
        byte[] signed = Assemble(Signed("<< /Fields [8 0 R] >>", "<< /FT /Sig /T (shadow) /V 9 0 R >>", "<< /Type /Sig /ByteRange [0 1 2 3] /Contents <00> >>"));

        AssertRefused(signed, new Dictionary<int, string> { [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>" }, 2);
    }

    /// <summary>The control: a field added to a form, inline or indirect, at level 2.</summary>
    [Fact]
    public void AFieldAddedToAFormIsPermittedAtLevelTwo()
    {
        const string Field = "<< /FT /Tx /T (a) >>";
        const string Signature = "<< /FT /Sig /Subtype /Widget /T (s) /Rect [0 0 1 1] >>";

        byte[] inline = Assemble(WithForm("<< /Fields [7 0 R] >>", Field));
        AssertAccepted(inline, new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [7 0 R 8 0 R] /SigFlags 3 >> >>",
            [8] = Signature,
        }, 2);

        byte[] indirect = Assemble(WithForm("8 0 R", Field, "<< /Fields [7 0 R] >>"));
        AssertAccepted(indirect, new Dictionary<int, string>
        {
            [8] = "<< /Fields [7 0 R 9 0 R] /SigFlags 3 >>",
            [9] = Signature,
        }, 2);
    }

    /// <summary>A form that did not exist may be added, for a first signature, its new object classified as ever.</summary>
    [Fact]
    public void AFormAddedForAFirstSignatureIsPermittedAtLevelTwo()
    {
        AssertAccepted(Assemble(Signed()), new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm 7 0 R >>",
            [7] = "<< /Fields [8 0 R] /SigFlags 3 >>",
            [8] = "<< /FT /Sig /Subtype /Widget /T (s) /Rect [0 0 1 1] >>",
        }, 2);
    }

    // ---------------------------------------------------------------- the certification

    /// <summary>
    /// <b>A rewritten catalog's <c>/Perms</c> does not outrank what covers less.</b> Named there, a decoy
    /// reaching the end of the file reads as "unmodified since certification".
    /// </summary>
    [Fact]
    public void AdversarialPerms_NamingALaterSignature_DoesNotStandInForTheFirst()
    {
        const string Reference = "/Reference [<< /TransformMethod /DocMDP /TransformParams << /P {0} >> >>]";
        string Sig(int p, int end) => "<< /Type /Sig /ByteRange [0 " + end + " " + end + " 0] /Contents <00> "
            + string.Format(CultureInfo.InvariantCulture, Reference, p) + " >>";

        string[] bodies = Signed("<< /FT /Sig /T (first) /V 8 0 R >>", "@");
        bodies[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [7 0 R] >> >>";
        bodies[7] = Sig(1, 100);
        byte[] certified = Assemble(bodies);

        byte[] forged = Revise(certified, new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [7 0 R] >> /Perms << /DocMDP 9 0 R >> >>",
            [9] = Sig(3, 900000),
        });

        var ms = new MemoryStream(forged);
        using PdfDocument doc = PdfDocument.Open(ms);
        DocMdpAnalysis analysis = DocMdpAnalyzer.Analyze(doc, forged.Length);

        Assert.Equal(1, analysis.PermissionLevel);
    }

    /// <summary>The control: <c>/Perms</c> naming the signature the fields list is the one reading.</summary>
    [Fact]
    public void PermsNamingTheListedSignature_Answers()
    {
        const string Sig = "<< /Type /Sig /ByteRange [0 100 100 0] /Contents <00> /Reference [<< /TransformMethod /DocMDP /TransformParams << /P 2 >> >>] >>";
        string[] bodies = Signed("<< /FT /Sig /T (first) /V 8 0 R >>", Sig);
        bodies[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [7 0 R] >> /Perms << /DocMDP 8 0 R >> >>";

        var ms = new MemoryStream(Assemble(bodies));
        using PdfDocument doc = PdfDocument.Open(ms);
        DocMdpAnalysis analysis = DocMdpAnalyzer.Analyze(doc, ms.Length);

        Assert.True(analysis.HasCertification);
        Assert.Equal(2, analysis.PermissionLevel);
    }

    /// <summary>
    /// <b>The certification is the signature that covers least</b>, so a later one naming a kinder level
    /// from a field listed first does not stand in for it.
    /// </summary>
    [Fact]
    public void AdversarialSecondCertification_ListedFirst_DoesNotStandInForTheFirst()
    {
        const string Reference = "/Reference [<< /TransformMethod /DocMDP /TransformParams << /P {0} >> >>]";
        string Sig(int p, int end) => "<< /Type /Sig /ByteRange [0 " + end + " " + end + " 0] /Contents <00> "
            + string.Format(CultureInfo.InvariantCulture, Reference, p) + " >>";

        string[] bodies = Signed("<< /FT /Sig /T (first) /V 8 0 R >>", "@");
        bodies[0] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [7 0 R] >> >>";
        bodies[7] = Sig(1, 100);
        byte[] certified = Assemble(bodies);

        byte[] forged = Revise(certified, new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [9 0 R 7 0 R] >> >>",
            [9] = "<< /FT /Sig /T (decoy) /V 10 0 R >>",
            [10] = Sig(3, 900000),
        });

        var ms = new MemoryStream(forged);
        using PdfDocument doc = PdfDocument.Open(ms);
        DocMdpAnalysis analysis = DocMdpAnalyzer.Analyze(doc, forged.Length);

        Assert.True(analysis.HasCertification);
        Assert.Equal(1, analysis.PermissionLevel);
    }
}
