// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What reading <see cref="PdfAttachmentManager"/> found, beyond the name-tree rewrite
/// (<see cref="CatalogNameTreeRewriteTests"/>): it wrote into encrypted documents and read them as
/// ciphertext, left removed and replaced files in the catalog <c>/AF</c> array, trusted a
/// non-integer <c>/Size</c>, and said nothing when a list was cut short. The destination writers
/// shared the encrypted-input defect.
/// </summary>
public class AttachmentWriterFindingsTests
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("payload");

    // ------------------------------------------------------------------ encrypted input

    /// <summary>
    /// A document with an attachment and a named destination, optionally carrying an
    /// <c>/Encrypt</c> entry. The encryption dictionary need not be valid: the refusal is decided
    /// before anything is decrypted.
    /// </summary>
    private static byte[] DocumentWithAttachment(bool encrypted)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 4 0 R /Dests 7 0 R >> >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
            .Obj(4, "<< /Names [(notes.txt) 5 0 R] >>")
            .Obj(5, "<< /Type /Filespec /F (notes.txt) /EF << /F 6 0 R >> >>")
            .StreamObj(6, "<< /Type /EmbeddedFile", "hello")
            .Obj(7, "<< /Names [(Chapter1) [3 0 R /Fit]] >>");
        if (!encrypted) return b.Finish();

        return b.Obj(8, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
                        "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                        "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>")
                .Finish(" /Encrypt 8 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>]");
    }

    /// <summary>
    /// Adding or removing an attachment in an encrypted document is refused before anything is
    /// written. The file, its name and the rebuilt tree went into the file in clear text, where a
    /// reader decrypts them into garbage.
    /// </summary>
    [Fact]
    public void AttachmentWrites_RefuseAnEncryptedDocument()
    {
        byte[] encrypted = DocumentWithAttachment(encrypted: true);

        using var addOutput = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfAttachmentManager.AddAttachment(new MemoryStream(encrypted), addOutput, "a.txt", Payload));
        Assert.Equal(0, addOutput.Length);

        using var removeOutput = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfAttachmentManager.RemoveAttachment(new MemoryStream(encrypted), removeOutput, "notes.txt"));
        Assert.Equal(0, removeOutput.Length);
    }

    /// <summary>The control: the same document without <c>/Encrypt</c> takes both edits.</summary>
    [Fact]
    public void AttachmentWrites_AcceptTheSameDocumentUnencrypted()
    {
        byte[] plain = DocumentWithAttachment(encrypted: false);

        using var added = new MemoryStream();
        PdfAttachmentManager.AddAttachment(new MemoryStream(plain), added, "a.txt", Payload);
        Assert.Equal(new[] { "a.txt", "notes.txt" },
            PdfAttachmentManager.ListAttachments(new MemoryStream(added.ToArray())).Select(a => a.Filename));

        using var removed = new MemoryStream();
        PdfAttachmentManager.RemoveAttachment(new MemoryStream(plain), removed, "notes.txt");
        Assert.Empty(PdfAttachmentManager.ListAttachments(new MemoryStream(removed.ToArray())));
    }

    /// <summary>
    /// Reading an encrypted document without its password is refused. The catalog is reachable
    /// through the trailer without decryption, so the list named attachments by ciphertext and an
    /// extraction returned encrypted bytes as the file.
    /// </summary>
    [Fact]
    public void AttachmentReads_RefuseAnEncryptedDocument_RatherThanAnswerWithCiphertext()
    {
        byte[] encrypted = DocumentWithAttachment(encrypted: true);

        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfAttachmentManager.ListAttachments(new MemoryStream(encrypted)));
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfAttachmentManager.ExtractAttachment(new MemoryStream(encrypted), "notes.txt"));
    }

    /// <summary>The control: unencrypted, the same reads answer.</summary>
    [Fact]
    public void AttachmentReads_AnswerForTheSameDocumentUnencrypted()
    {
        byte[] plain = DocumentWithAttachment(encrypted: false);

        Assert.Equal("notes.txt", Assert.Single(PdfAttachmentManager.ListAttachments(new MemoryStream(plain))).Filename);
        Assert.Equal("hello", Encoding.ASCII.GetString(
            PdfAttachmentManager.ExtractAttachment(new MemoryStream(plain), "notes.txt")));
    }

    /// <summary>
    /// The destination writers refuse an encrypted document. <c>SetDestination</c> threw a
    /// <see cref="NullReferenceException"/> (no catalog loads for an encrypted file), and
    /// <c>RemoveDestination</c> returned <c>false</c>, "not found", for a destination the file
    /// holds.
    /// </summary>
    [Fact]
    public void DestinationWrites_RefuseAnEncryptedDocument()
    {
        byte[] encrypted = DocumentWithAttachment(encrypted: true);

        using var setOutput = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfNamedDestinations.SetDestination(new MemoryStream(encrypted), setOutput, "zz", 0));
        Assert.Equal(0, setOutput.Length);

        using var removeOutput = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfNamedDestinations.RemoveDestination(new MemoryStream(encrypted), removeOutput, "Chapter1"));
        Assert.Equal(0, removeOutput.Length);
    }

    /// <summary>The control: unencrypted, both destination writes succeed.</summary>
    [Fact]
    public void DestinationWrites_AcceptTheSameDocumentUnencrypted()
    {
        byte[] plain = DocumentWithAttachment(encrypted: false);

        using var set = new MemoryStream();
        PdfNamedDestinations.SetDestination(new MemoryStream(plain), set, "zz", 0);
        Assert.Equal(new[] { "Chapter1", "zz" },
            PdfNamedDestinations.ListDestinations(new MemoryStream(set.ToArray())).Select(d => d.Name));

        using var removed = new MemoryStream();
        Assert.True(PdfNamedDestinations.RemoveDestination(new MemoryStream(plain), removed, "Chapter1"));
        Assert.Empty(PdfNamedDestinations.ListDestinations(new MemoryStream(removed.ToArray())));
    }

    // ------------------------------------------------------------------ catalog /AF

    private static byte[] MinimalDocument(string catalogExtras = "", Func<PdfFixtureBuilder, PdfFixtureBuilder>? more = null)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R " + catalogExtras + ">>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>");
        return (more?.Invoke(b) ?? b).Finish();
    }

    private static byte[] Add(byte[] pdf, string name, string? relationship)
    {
        using var output = new MemoryStream();
        PdfAttachmentManager.AddAttachment(new MemoryStream(pdf), output, name, Payload, null, relationship);
        return output.ToArray();
    }

    /// <summary>The catalog /AF array's file specifications, resolved; null when there is none.</summary>
    private static PdfDictionary[]? AssociatedFiles(byte[] pdf)
    {
        var doc = PdfDocument.Open(new MemoryStream(pdf));
        if (doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AF]) is not PdfArray af) return null;
        return af.Select(item => (PdfDictionary)doc.ResolveObject(item)).ToArray();
    }

    /// <summary>
    /// Replacing an associated file leaves one <c>/AF</c> entry, the new one. The replaced file
    /// specification stayed in the array, so a reader presenting associated files listed both.
    /// </summary>
    [Fact]
    public void ReplacingAnAssociatedFile_LeavesOneAfEntry_TheNewOne()
    {
        byte[] once = Add(MinimalDocument(), "data.xml", "Data");
        Assert.Single(AssociatedFiles(once)!);

        byte[] twice = Add(once, "data.xml", "Source");

        var af = Assert.Single(AssociatedFiles(twice)!);
        Assert.Equal("Source", ((PdfName)af[PdfName.AFRelationship]).Value);
    }

    /// <summary>
    /// Removing an associated file takes it out of <c>/AF</c>, and the array goes with its last
    /// entry. It stayed, so the removed file was still presented as an associated file.
    /// </summary>
    [Fact]
    public void RemovingAnAssociatedFile_TakesItOutOfAf()
    {
        byte[] withTwo = Add(Add(MinimalDocument(), "a.xml", "Data"), "b.xml", "Source");
        Assert.Equal(2, AssociatedFiles(withTwo)!.Length);

        using var one = new MemoryStream();
        PdfAttachmentManager.RemoveAttachment(new MemoryStream(withTwo), one, "a.xml");
        var left = Assert.Single(AssociatedFiles(one.ToArray())!);
        Assert.Equal("Source", ((PdfName)left[PdfName.AFRelationship]).Value);

        using var none = new MemoryStream();
        PdfAttachmentManager.RemoveAttachment(new MemoryStream(one.ToArray()), none, "b.xml");
        Assert.Null(AssociatedFiles(none.ToArray()));
    }

    /// <summary>An indirect <c>/AF</c> array is superseded where it lives and stays indirect.</summary>
    [Fact]
    public void AnIndirectAfArrayIsSupersededInPlace()
    {
        byte[] pdf = MinimalDocument("/Names << /EmbeddedFiles << /Names [(old.xml) 5 0 R] >> >> /AF 4 0 R ",
            b => b.Obj(4, "[5 0 R]")
                  .Obj(5, "<< /Type /Filespec /F (old.xml) /AFRelationship /Data >>"));

        byte[] written = Add(pdf, "new.xml", "Source");

        using var doc = PdfDocument.Open(new MemoryStream(written));
        Assert.Equal(4, Assert.IsType<PdfReference>(doc.Catalog.Dictionary[PdfName.AF]).ReferenceObjectNumber);
        Assert.Equal(new[] { "Data", "Source" },
            AssociatedFiles(written)!.Select(f => ((PdfName)f[PdfName.AFRelationship]).Value));
    }

    // ------------------------------------------------------------------ /Params /Size

    private static long ListedSize(string sizeEntry)
    {
        byte[] pdf = MinimalDocument("/Names << /EmbeddedFiles << /Names [(f.txt) 4 0 R] >> >> ",
            b => b.Obj(4, "<< /Type /Filespec /F (f.txt) /EF << /F 5 0 R >> >>")
                  .StreamObj(5, "<< /Type /EmbeddedFile /Params << " + sizeEntry + " >>", "hello"));
        return Assert.Single(PdfAttachmentManager.ListAttachments(new MemoryStream(pdf))).SizeBytes;
    }

    /// <summary>
    /// A <c>/Size</c> that is not a non-negative integer is not a size: the stored length is
    /// reported instead. A negative one was reported as is, and a <c>(long)</c> cast of an
    /// out-of-range real is <see cref="long.MinValue"/>.
    /// </summary>
    [Theory]
    [InlineData("/Size -5")]
    [InlineData("/Size 100000000000000000000000.0")]
    [InlineData("/Size 3.5")]
    public void ASizeThatIsNotANonNegativeInteger_FallsBackToTheStoredLength(string sizeEntry)
        => Assert.Equal(5, ListedSize(sizeEntry));

    /// <summary>The control: an integer <c>/Size</c> is what is reported.</summary>
    [Fact]
    public void AnIntegerSizeIsReported()
        => Assert.Equal(42, ListedSize("/Size 42"));

    // ------------------------------------------------------------------ truncated list

    [Fact]
    public void AnAttachmentListCutShortByTheTreeDepthCapIsReported()
    {
        byte[] deep = CatalogNameTreeRewriteTests.DeepAttachmentTree(PdfLimits.MaxTreeDepth + 20);

        var listed = PdfAttachmentManager.ListAttachments(new MemoryStream(deep));

        Assert.Equal(new[] { "shallow.txt" }, listed.Select(a => a.Filename));
        Assert.Contains("incomplete", PdfAttachmentManager.LastListError ?? "", StringComparison.Ordinal);
    }

    /// <summary>The control, and the reset: a complete read after a truncated one clears the report.</summary>
    [Fact]
    public void ACompleteAttachmentReadReportsNothing_EvenAfterATruncatedOne()
    {
        PdfAttachmentManager.ListAttachments(new MemoryStream(
            CatalogNameTreeRewriteTests.DeepAttachmentTree(PdfLimits.MaxTreeDepth + 20)));
        Assert.NotNull(PdfAttachmentManager.LastListError);

        var listed = PdfAttachmentManager.ListAttachments(new MemoryStream(
            CatalogNameTreeRewriteTests.DeepAttachmentTree(3)));

        Assert.Equal(new[] { "shallow.txt", "deep.txt" }, listed.Select(a => a.Filename));
        Assert.Null(PdfAttachmentManager.LastListError);
    }

    /// <summary>An extraction that misses past the limit says so; it used to be a plain "not found".</summary>
    [Fact]
    public void AnExtractionThatMissesPastTheLimitIsReported()
    {
        byte[] deep = CatalogNameTreeRewriteTests.DeepAttachmentTree(PdfLimits.MaxTreeDepth + 20);

        Assert.Null(PdfAttachmentManager.ExtractAttachment(new MemoryStream(deep), "deep.txt"));
        Assert.Contains("incomplete", PdfAttachmentManager.LastListError ?? "", StringComparison.Ordinal);

        PdfAttachmentManager.ExtractAttachment(new MemoryStream(
            CatalogNameTreeRewriteTests.DeepAttachmentTree(3)), "deep.txt");
        Assert.Null(PdfAttachmentManager.LastListError);
    }
}
