// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The two rich-media shapes nothing in the tree had a fixture for: a <c>/RichMedia</c> annotation's
/// <c>/Assets</c> name tree, and a <c>/Movie</c> annotation's embedded-file chain.
/// </summary>
/// <remarks>
/// <para><b>Only <c>/Sound</c> was ever built.</b> <see cref="PdfRichMediaExtractor"/> covers three
/// subtypes and the suite reached one of them — the one whose payload is a single stream hanging off
/// the annotation. The two branches that actually walk a structure were unasserted in both
/// directions: which slot of the name tree holds the asset, and which link of the file specification
/// holds the embedded bytes.</para>
///
/// <para><b>The name tree's asset is at an ODD index.</b> §7.9.6 makes <c>/Names</c> a flat array of
/// alternating key/value pairs, so <c>[0]</c> is the asset's <i>name</i> and <c>[1]</c> is the
/// stream. Reading <c>[0]</c> finds no stream, which this extractor reports as "no embedded asset" —
/// the same answer a file carrying no media at all produces, so the wrong slot is indistinguishable
/// from an empty document unless a fixture states the payload's length.</para>
/// </remarks>
public class MediaAnnotationShapeTests
{
    private const string Video = "fake-mp4-payload-bytes";
    private const string Clip = "fake-mov-payload";

    // ==================================================================
    //  /RichMedia — the /Assets name tree
    // ==================================================================

    /// <summary>
    /// The asset comes from the name tree's <b>value</b> slot, and its MIME type from that stream's
    /// <c>/Params /MIMEType</c>.
    /// </summary>
    /// <remarks>
    /// Asserting the decoded length is what separates "read the right slot" from "read the name and
    /// found no stream": the latter reports <c>DataLength 0</c> with a reason, which is also what an
    /// annotation carrying no assets at all reports.
    /// </remarks>
    [Fact]
    public void TheRichMediaAssetComesFromTheNameTreesValueSlot()
    {
        using var pdf = new MemoryStream(RichMediaPdf());

        var only = Assert.Single(PdfRichMediaExtractor.ListRichMedia(pdf));

        Assert.Equal(PdfAnnotationSubtypes.RichMedia, only.Subtype);
        Assert.Equal(Video.Length, only.DataLength);
        Assert.Null(only.DataLengthUnavailableReason);
        Assert.Equal("video/mp4", only.ContentType);
    }

    /// <summary>The same asset comes back as bytes through the extraction entry point.</summary>
    [Fact]
    public void TheRichMediaAssetIsExtractedAsBytes()
    {
        using var pdf = new MemoryStream(RichMediaPdf());

        byte[] data = PdfRichMediaExtractor.ExtractMedia(pdf, 0);

        Assert.Equal(Video, Encoding.ASCII.GetString(data));
        Assert.Null(PdfRichMediaExtractor.LastError);
    }

    /// <summary>
    /// <b>A <c>/Names</c> array holding a key with no value is refused, not indexed past.</b>
    /// </summary>
    /// <remarks>
    /// §7.9.6 pairs the entries, so an odd-length array is malformed — and the length test is the
    /// only thing between it and an index one past the end. The annotation is still listed, with the
    /// reason naming the name tree, because an inventory that drops what it cannot read tells the
    /// caller the file has no media.
    /// </remarks>
    [Fact]
    public void AMalformedNamesArrayCarryingNoValueIsRefusedRatherThanIndexedPastTheEnd()
    {
        using var pdf = new MemoryStream(RichMediaPdf(namesArray: "[(video.mp4)]"));

        var only = Assert.Single(PdfRichMediaExtractor.ListRichMedia(pdf));

        Assert.Equal(PdfAnnotationSubtypes.RichMedia, only.Subtype);
        Assert.Equal(0, only.DataLength);
        Assert.NotNull(only.DataLengthUnavailableReason);
        Assert.Contains("/Assets", only.DataLengthUnavailableReason, StringComparison.Ordinal);
    }

    /// <summary><c>/Contents</c> is reported as the annotation's description.</summary>
    [Fact]
    public void TheAnnotationsContentsIsReportedAsItsDescription()
    {
        using var pdf = new MemoryStream(RichMediaPdf(contents: "(Product tour)"));

        var only = Assert.Single(PdfRichMediaExtractor.ListRichMedia(pdf));

        Assert.Equal("Product tour", only.Description);
    }

    /// <summary>
    /// The listing ceiling refuses rather than truncates on the rich-media side too — the same rule
    /// <see cref="Pdf3DExtractor"/> already had a fixture for.
    /// </summary>
    /// <remarks>
    /// The twins share this rule and only one of them was asserted, which is the shape CLAUDE.md
    /// warns about: consolidating part of a cluster is more dangerous than consolidating none of it.
    /// The reason, the zero length and the diagnostic are all asserted, because the guard's evidence
    /// is what distinguishes a refusal from an asset that really is empty.
    /// </remarks>
    [Fact]
    public void AnOversizedRichMediaAssetIsReportedAsUnmeasuredRatherThanInflated()
    {
        using var pdf = new MemoryStream(RichMediaPdf());

        var only = Assert.Single(PdfRichMediaExtractor.ListRichMedia(pdf, maxStreamBytes: 4));

        Assert.Equal(0, only.DataLength);
        Assert.NotNull(only.DataLengthUnavailableReason);
        Assert.NotNull(PdfRichMediaExtractor.LastError);
    }

    /// <summary>A non-positive ceiling is refused by name, on both twins.</summary>
    [Fact]
    public void ANonPositiveListingCeilingIsRefusedByName()
    {
        using var a = new MemoryStream(RichMediaPdf());
        using var b = new MemoryStream(RichMediaPdf());

        var rm = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfRichMediaExtractor.ListRichMedia(a, maxStreamBytes: 0));
        var td = Assert.Throws<ArgumentOutOfRangeException>(
            () => Pdf3DExtractor.List3DAnnotations(b, maxStreamBytes: 0));

        Assert.Equal("maxStreamBytes", rm.ParamName);
        Assert.Equal("maxStreamBytes", td.ParamName);
    }

    // ==================================================================
    //  /Movie — the embedded-file chain
    // ==================================================================

    /// <summary>
    /// A <c>/Movie</c> annotation's payload is reached through <c>/Movie → /F → /EF → /F</c>, and the
    /// <c>/EF</c> step is what distinguishes an embedded movie from an externally referenced one.
    /// </summary>
    [Fact]
    public void AMovieAnnotationsEmbeddedFileIsFound()
    {
        using var listing = new MemoryStream(MoviePdf(embedded: true));
        using var extract = new MemoryStream(MoviePdf(embedded: true));

        var only = Assert.Single(PdfRichMediaExtractor.ListRichMedia(listing));
        Assert.Equal(PdfAnnotationSubtypes.Movie, only.Subtype);
        Assert.Equal(Clip.Length, only.DataLength);

        Assert.Equal(Clip, Encoding.ASCII.GetString(
            PdfRichMediaExtractor.ExtractMedia(extract, 0)));
    }

    /// <summary>
    /// The control, and the reason string's own claim: a movie referenced as an external file has no
    /// <c>/EF</c>, and the listing says so rather than reporting an empty asset.
    /// </summary>
    [Fact]
    public void AnExternallyReferencedMovieIsReportedAsNotEmbedded()
    {
        using var pdf = new MemoryStream(MoviePdf(embedded: false));

        var only = Assert.Single(PdfRichMediaExtractor.ListRichMedia(pdf));

        Assert.Equal(0, only.DataLength);
        Assert.NotNull(only.DataLengthUnavailableReason);
        Assert.Contains("/EF", only.DataLengthUnavailableReason, StringComparison.Ordinal);
    }

    // ==================================================================
    //  Indexing, and the diagnostic slot
    // ==================================================================

    /// <summary>
    /// <b><c>ExtractMedia</c> addresses the <i>n</i>th annotation.</b> A one-annotation fixture
    /// cannot see an off-by-one, because index 0 is the only index there is.
    /// </summary>
    [Fact]
    public void ExtractMediaAddressesTheAnnotationTheCallerNamed()
    {
        using var first = new MemoryStream(TwoSoundsPdf());
        using var second = new MemoryStream(TwoSoundsPdf());

        Assert.Equal("first-sound", Encoding.ASCII.GetString(
            PdfRichMediaExtractor.ExtractMedia(first, 0)));
        Assert.Equal("second-sound", Encoding.ASCII.GetString(
            PdfRichMediaExtractor.ExtractMedia(second, 1)));
    }

    /// <summary>An index past the last annotation is refused by name, not answered with no bytes.</summary>
    [Fact]
    public void AnIndexPastTheLastAnnotationIsRefusedByName()
    {
        using var pdf = new MemoryStream(TwoSoundsPdf());

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfRichMediaExtractor.ExtractMedia(pdf, 2));

        Assert.Equal("annotationIndex", ex.ParamName);
    }

    /// <summary>
    /// <b>The diagnostic slot is cleared by the next call</b>, so a stale reason cannot be read as
    /// this call's.
    /// </summary>
    /// <remarks>
    /// The first assertion is the potency half: without it, "the slot is null" is also what a
    /// fixture that never set it produces. The slot is <c>[ThreadStatic]</c> and both halves run on
    /// one thread, which is the condition under which a missing reset is observable at all.
    /// </remarks>
    [Fact]
    public void TheRichMediaDiagnosticSlotIsClearedAtTheStartOfTheNextCall()
    {
        using var bad = new MemoryStream(MoviePdf(embedded: false));
        PdfRichMediaExtractor.ListRichMedia(bad);
        Assert.NotNull(PdfRichMediaExtractor.LastError);

        using var good = new MemoryStream(RichMediaPdf());
        PdfRichMediaExtractor.ListRichMedia(good);

        Assert.Null(PdfRichMediaExtractor.LastError);
    }

    /// <summary>The same, for the 3D twin's extraction entry point.</summary>
    /// <remarks>
    /// A 3D annotation with no <c>/3DD</c> returns no bytes, and the slot is the only thing that
    /// separates that from a stream which decoded to nothing.
    /// </remarks>
    [Fact]
    public void The3DDiagnosticSlotIsClearedAtTheStartOfTheNextCall()
    {
        using var bad = new MemoryStream(ThreeDPdf(withStream: false));
        Assert.Empty(Pdf3DExtractor.Extract3DStream(bad, 0));
        Assert.NotNull(Pdf3DExtractor.LastError);

        using var good = new MemoryStream(ThreeDPdf(withStream: true));
        Assert.NotEmpty(Pdf3DExtractor.Extract3DStream(good, 0));

        Assert.Null(Pdf3DExtractor.LastError);
    }

    // ==================================================================
    //  AnnotationSubtypeWalk — the shared walk beneath both twins
    // ==================================================================

    /// <summary>
    /// <b>A subtype filter with no entries matches nothing, not everything</b> — whether it is an
    /// empty array or a null one. The walk's two entry points exist because of this: an inventory
    /// needs <c>All</c>, and cannot be spelled as <c>BySubtype()</c> with no arguments.
    /// </summary>
    /// <remarks>
    /// <para>The <c>All</c> count beside it is the potency assertion — without it, "the filtered
    /// walk yielded nothing" is equally satisfied by a document the walk could not read at all.</para>
    ///
    /// <para><b>The null spelling is the half that is load-bearing.</b> Mutating the whole
    /// <c>subtypes == null || subtypes.Length == 0</c> guard away survives every test including this
    /// one's first assertion, because for an <i>empty</i> array the loop below it iterates zero
    /// times and yields nothing anyway — the length clause is a fast path, not a guard. A
    /// <c>null</c> filter is the only input on which the two differ, and there it is the difference
    /// between an empty walk and a <see cref="NullReferenceException"/>.</para>
    /// </remarks>
    [Fact]
    public void ASubtypeFilterWithNoEntriesMatchesNothingWhileAllMatchesEverything()
    {
        using var ms = new MemoryStream(TwoSoundsPdf());
        using var doc = PdfDocument.Open(ms);

        Assert.Empty(AnnotationSubtypeWalk.BySubtype(doc));
        Assert.Empty(AnnotationSubtypeWalk.BySubtype(doc, (string[])null));
        Assert.Equal(2, AnnotationSubtypeWalk.All(doc).Count());
    }

    /// <summary>
    /// An annotation whose <c>/Subtype</c> will not resolve is dropped by the walk rather than
    /// yielded with a null subtype, which every caller's gate would then compare against.
    /// </summary>
    [Fact]
    public void AnAnnotationWithNoSubtypeIsDroppedByTheWalk()
    {
        byte[] pdf = OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 5 0 R] >>",
            Obj(4, "<< /Type /Annot /Rect [0 0 10 10] >>"),
            Obj(5, "<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] >>"));

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var all = AnnotationSubtypeWalk.All(doc).ToList();

        Assert.Equal("Square", Assert.Single(all).Subtype);
    }

    // ==================================================================
    //  Fixture assembly
    // ==================================================================

    /// <summary>
    /// One page carrying one <c>/RichMedia</c> annotation whose asset is an embedded stream reached
    /// through <c>/RichMediaContent /Assets /Names</c>.
    /// </summary>
    private static byte[] RichMediaPdf(string namesArray = "[(video.mp4) 7 0 R]",
                                       string contents = "(A clip)")
        => OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /RichMedia /Rect [0 0 10 10] " +
                   $"/Contents {contents} /RichMediaContent 5 0 R >>"),
            Obj(5, "<< /Type /RichMediaContent /Assets 6 0 R >>"),
            Obj(6, $"<< /Names {namesArray} >>"),
            StreamObj(7, " /Type /Filespec /Params << /MIMEType (video/mp4) >>", Video));

    /// <summary>
    /// One page carrying one <c>/Movie</c> annotation. When <paramref name="embedded"/> the file
    /// specification carries an <c>/EF</c> stream; otherwise it names an external file, which is the
    /// case the reason string exists for.
    /// </summary>
    private static byte[] MoviePdf(bool embedded)
        => OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /Movie /Rect [0 0 10 10] /Movie 5 0 R >>"),
            Obj(5, "<< /F 6 0 R >>"),
            Obj(6, embedded
                ? "<< /Type /Filespec /F (clip.mov) /EF << /F 7 0 R >> >>"
                : "<< /Type /Filespec /F (clip.mov) >>"),
            StreamObj(7, "", Clip));

    /// <summary>One page carrying two <c>/Sound</c> annotations with distinguishable payloads.</summary>
    private static byte[] TwoSoundsPdf()
        => OnePage(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R 5 0 R] >>",
            Obj(4, "<< /Type /Annot /Subtype /Sound /Rect [0 0 10 10] /Sound 6 0 R >>"),
            Obj(5, "<< /Type /Annot /Subtype /Sound /Rect [20 0 30 10] /Sound 7 0 R >>"),
            StreamObj(6, " /Type /Sound /R 22050 /E /Raw", "first-sound"),
            StreamObj(7, " /Type /Sound /R 22050 /E /Raw", "second-sound"));

    /// <summary>One page carrying one <c>/3D</c> annotation, with or without its <c>/3DD</c>.</summary>
    private static byte[] ThreeDPdf(bool withStream)
        => withStream
            ? OnePage(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
                Obj(4, "<< /Type /Annot /Subtype /3D /Rect [0 0 10 10] /3DD 5 0 R >>"),
                StreamObj(5, " /Type /3D /Subtype /U3D", "u3d-mesh"))
            : OnePage(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
                Obj(4, "<< /Type /Annot /Subtype /3D /Rect [0 0 10 10] >>"));

    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    private static string StreamObj(int n, string dictExtras, string content)
        => $"{n} 0 obj\n<< /Length {content.Length}{dictExtras} >>\nstream\n{content}\nendstream\nendobj\n";

    /// <summary>A one-page document whose page dictionary body the caller supplies.</summary>
    private static byte[] OnePage(string pageDictBody, params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, pageDictBody),
        };
        bodies.AddRange(extraObjects);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string b in bodies) { offsets.Add(sb.Length); sb.Append(b); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
