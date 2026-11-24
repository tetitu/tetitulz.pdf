// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// The <c>PdfOperations</c> readers converted for plan 08 item 50 read an <b>encrypted</b> document
/// when given the password, and answer with the decrypted content rather than with an empty result.
/// </summary>
/// <remarks>
/// <para><b>Every assertion names the thing the document actually contains</b>, and every one of
/// those things is a PDF <i>string</i> or <i>stream</i> — so it is ciphertext in the encrypted file.
/// "The list has one entry" would also pass for a reader that enumerated the object graph without
/// decrypting its leaves; "the layer is called Watermark" would not. Where a reader returns no
/// string at all (<c>GetColorSpaces</c> reports the family <c>CalRGB</c>, <c>List3DAnnotations</c>
/// a byte count) the assertion still cannot pass without decryption — the page tree is unreachable
/// otherwise — and each is paired with a second call whose result is the decoded stream payload.</para>
/// <para>The complement matters as much: <b>without</b> the password each call must fail loudly.
/// Before the conversion they returned an empty list — the same answer a document that genuinely has
/// no layers, no attachments and no fields gives, which is the silent-wrong-answer shape the whole
/// <c>WithDecrypted</c> route exists to prevent.</para>
/// <para>The guard that says which readers still lack this is
/// <c>TestInfrastructure/FacadePasswordCoverageTests</c>; its burn-down list is what remains.</para>
/// </remarks>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.DecryptorHolderCollection.Name)]
public sealed class EncryptedFacadeReadOverloadTests
{
    private const string UserPassword = "s3cret";

    private const string AttachmentPayload = "attachment-plaintext";
    private const string ThreeDPayload = "U3D-PAYLOAD-BYTES";

    public EncryptedFacadeReadOverloadTests() => PdfEncryptionOperations.Register();

    /// <summary>
    /// One page carrying, deliberately, one observable per converted entry point: text, a font
    /// resource, a CalRGB colour space, a URI action, an AcroForm text field, a named destination,
    /// an embedded file, an optional-content group and a 3D annotation.
    /// </summary>
    private static byte[] RichPdf()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var offsets = new int[15];
        sb.Append("%PDF-1.7\n");

        void Obj(int n, string body)
        {
            offsets[n] = sb.Length;
            sb.Append(n.ToString(ci)).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        void StreamObj(int n, string dict, string payload)
        {
            offsets[n] = sb.Length;
            sb.AppendFormat(ci, "{0} 0 obj\n<< {1} /Length {2} >>\nstream\n{3}\nendstream\nendobj\n",
                            n, dict, payload.Length, payload);
        }

        Obj(1, "<< /Type /Catalog /Pages 2 0 R"
              + " /AcroForm << /Fields [8 0 R] >>"
              + " /Names << /Dests 9 0 R /EmbeddedFiles 10 0 R >>"
              + " /OCProperties << /OCGs [13 0 R] /D << /ON [13 0 R] >> >> >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
              + " /Annots [7 0 R 8 0 R 14 0 R]"
              + " /Resources << /Font << /F1 5 0 R >>"
              + " /ColorSpace << /CS0 [/CalRGB << /WhitePoint [0.9505 1 1.089] >>] >> >> >>");
        StreamObj(4, "", "BT /F1 24 Tf 72 700 Td (Locked away) Tj ET");
        Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        Obj(6, "<< /Type /Filespec /F (notes.txt) /UF (notes.txt) /EF << /F 11 0 R >> >>");
        Obj(7, "<< /Type /Annot /Subtype /Link /Rect [72 700 300 720] /Border [0 0 0]"
              + " /A << /S /URI /URI (https://example.test/locked) >> >>");
        // The field and the link both carry the URI action, and PdfActionDispatcher lists both: the
        // field's as the field's, the link's as an annotation's (it walks the annotations too).
        Obj(8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (surname) /V (Cohen)"
              + " /A << /S /URI /URI (https://example.test/locked) >>"
              + " /Rect [72 600 300 620] /P 3 0 R >>");
        Obj(9, "<< /Names [(Chapter1Dest) [3 0 R /Fit]] >>");
        Obj(10, "<< /Names [(notes.txt) 6 0 R] >>");
        StreamObj(11, "/Type /EmbeddedFile", AttachmentPayload);
        StreamObj(12, "/Type /3D /Subtype /U3D", ThreeDPayload);
        Obj(13, "<< /Type /OCG /Name (Watermark) >>");
        Obj(14, "<< /Type /Annot /Subtype /3D /Rect [100 100 300 300] /3DD 12 0 R >>");

        int xref = sb.Length;
        sb.Append("xref\n0 15\n0000000000 65535 f \n");
        for (int n = 1; n <= 14; n++) sb.AppendFormat(ci, "{0:D10} 00000 n \n", offsets[n]);
        sb.Append("trailer\n<< /Size 15 /Root 1 0 R >>\n");
        sb.AppendFormat(ci, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] EncryptedRichPdf()
    {
        using var input = new MemoryStream(RichPdf());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });
        return output.ToArray();
    }

    private static MemoryStream Open(byte[] pdf) => new MemoryStream(pdf);

    /// <summary>
    /// The fixture's two URI actions, the field's and the link's, each read as plaintext. The target
    /// is a PDF string, so an undecrypted read would list ciphertext here.
    /// </summary>
    private static void AssertTheTwoUriActions(IReadOnlyList<PdfAction> actions)
    {
        Assert.Equal(2, actions.Count);
        Assert.All(actions, a => Assert.Equal("https://example.test/locked", a.Target));
        Assert.Equal(new[] { PdfActionSource.Field, PdfActionSource.Annotation }, actions.Select(a => a.Source));
    }

    // ------------------------------------------------------------------ the control

    /// <summary>
    /// Unencrypted, the fixture yields every observable the encrypted tests below look for. Without
    /// this the encrypted assertions could pass for the wrong reason — a fixture that never carried
    /// the feature makes "decrypted and found nothing" indistinguishable from "there was nothing".
    /// </summary>
    [Fact]
    public void PlainDocument_YieldsEveryObservableTheEncryptedAssertionsLookFor()
    {
        byte[] pdf = RichPdf();

        using (var s = Open(pdf)) Assert.True(PdfOperations.ContainsText(s, "Locked away"));
        using (var s = Open(pdf)) AssertTheTwoUriActions(PdfOperations.ExtractActions(s));
        using (var s = Open(pdf)) Assert.Equal("Chapter1Dest",
            PdfOperations.ListDestinations(s).Single().Name);
        using (var s = Open(pdf)) Assert.Equal("Watermark", PdfOperations.ListLayers(s).Single().Name);
        using (var s = Open(pdf)) Assert.Equal("surname",
            PdfOperations.GetFormFields(s).Single().FullyQualifiedName);
        using (var s = Open(pdf)) Assert.Equal("notes.txt",
            PdfOperations.ListAttachments(s).Single().Filename);
        using (var s = Open(pdf)) Assert.Equal(AttachmentPayload,
            Encoding.ASCII.GetString(PdfOperations.ExtractAttachment(s, "notes.txt")));
        using (var s = Open(pdf)) Assert.Equal("Helvetica", PdfOperations.ListFonts(s).Single().BaseFont);
        using (var s = Open(pdf)) Assert.Contains(PdfOperations.GetColorSpaces(s), c => c.Name == "CalRGB");
        using (var s = Open(pdf)) Assert.Equal(ThreeDPayload.Length,
            PdfOperations.List3DAnnotations(s).Single().DataLength);
        using (var s = Open(pdf)) Assert.Equal(ThreeDPayload,
            Encoding.ASCII.GetString(PdfOperations.Extract3DStream(s, 0)));
    }

    /// <summary>
    /// Routing a reader through <c>WithDecrypted</c> must not change what it does with a null
    /// stream. Each of these rejected null with <see cref="ArgumentNullException"/> when it opened
    /// the document itself; the shared helper reached <c>EnsureSeekable</c> first and raised
    /// <see cref="NullReferenceException"/>, which is never a contract.
    /// </summary>
    /// <remarks>
    /// Three of the converted readers already had a null-stream test of their own, and those three
    /// are what caught this. The rest had none — so this covers the whole batch, which is the point:
    /// the check belongs to the helper, not to each of the twelve call sites.
    /// </remarks>
    [Fact]
    public void EveryConvertedReader_StillRejectsANullStreamWithArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PdfOperations.SearchText((Stream)null, "q"));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ContainsText((Stream)null, "q"));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ExtractActions((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ListDestinations((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ListLayers((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.GetFormFields((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ListAttachments((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ExtractAttachment((Stream)null, "f"));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ListFonts((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.GetColorSpaces((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.List3DAnnotations((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.Extract3DStream((Stream)null, 0));

        // Second conversion batch (2026-08-14, after plan 08 decision D-8).
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ExtractGraphics((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ExtractGraphics((Stream)null, 0));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ReadStructureTree((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.IsTaggedPdf((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ReadUsageRights((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.IsPortfolio((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ListPortfolioItems((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.ExportOutlineJson((Stream)null));
        Assert.Throws<ArgumentNullException>(() => PdfOperations.GetCalculationOrder((Stream)null));
    }

    /// <summary>
    /// The second conversion batch really routes through <c>WithDecrypted</c>: the same call
    /// <b>without</b> a password fails loudly instead of answering empty.
    /// </summary>
    /// <remarks>
    /// <para><b>The negative is the load-bearing assertion, not the positive.</b> An overload that
    /// accepted a <c>password</c> and ignored it would still return a page list here — a page COUNT
    /// needs no decryption, since §7.6.2 encrypts strings and streams and not the dictionaries
    /// around them. But it would also not throw when the password is omitted, and that is what the
    /// second test catches. Before the conversion the no-password call answered <b>empty</b> —
    /// indistinguishable from a page that genuinely draws nothing, which is the silent-wrong-answer
    /// shape <c>WithDecrypted</c> exists to prevent.</para>
    /// <para><b>Honest coverage note.</b> Of the nine readers converted in this batch, this pair
    /// covers one. The other eight — <c>ReadStructureTree</c>,
    /// <c>IsTaggedPdf</c>, <c>ReadUsageRights</c>, <c>IsPortfolio</c>, <c>ListPortfolioItems</c>,
    /// <c>ExportOutlineJson</c>, <c>GetCalculationOrder</c> and the paged <c>ExtractGraphics</c> —
    /// each need a document carrying the feature they read (a structure tree, a collection, an
    /// outline, a <c>/CO</c> array), and a fixture that has none of those would let all of them
    /// "pass" by returning the same empty answer they returned before. They are covered by the
    /// argument checks above and by <c>FacadePasswordCoverageTests</c>, and a per-feature fixture is
    /// the honest way to cover them properly. Recorded rather than papered over.</para>
    /// </remarks>
    [Fact]
    public void ExtractGraphics_WithThePassword_ReadsTheEncryptedDocument()
    {
        byte[] encrypted = EncryptedRichPdf();

        var pages = PdfOperations.ExtractGraphics(new MemoryStream(encrypted), UserPassword);

        // Assert the parsed result, not that something came back. `Assert.NotEmpty` alone was the
        // first attempt here and NoUnfalsifiableAssertionsTests rejected it — correctly, and on the
        // author of this batch, which is the guard earning its place. The fixture is one page
        // (object 2 declares /Count 1), so the count and the index are facts about what the
        // extractor produced.
        Assert.Single(pages);
        Assert.Equal(0, pages[0].PageIndex);
    }

    [Fact]
    public void ExtractGraphics_WithoutThePassword_FailsLoudlyRatherThanAnsweringEmpty()
    {
        byte[] encrypted = EncryptedRichPdf();

        Assert.ThrowsAny<Exception>(
            () => PdfOperations.ExtractGraphics(new MemoryStream(encrypted)));
    }

    // ------------------------------------------------------------------ with the password

    [Fact]
    public void SearchText_AndContainsText_ReadAnEncryptedDocumentWhenGivenThePassword()
    {
        byte[] pdf = EncryptedRichPdf();

        using (var s = Open(pdf))
        {
            var hit = Assert.Single(PdfOperations.SearchText(s, "Locked", password: UserPassword));
            Assert.Equal("Locked", hit.MatchText);
            Assert.Contains("Locked away", hit.ContextSnippet);
        }

        using (var s = Open(pdf))
            Assert.True(PdfOperations.ContainsText(s, "Locked away", password: UserPassword));

        // A word the document does not contain still answers false — the password does not turn the
        // search into "did we manage to decrypt".
        using (var s = Open(pdf))
            Assert.False(PdfOperations.ContainsText(s, "Unlocked", password: UserPassword));
    }

    [Fact]
    public void ObjectGraphReaders_ReadAnEncryptedDocumentWhenGivenThePassword()
    {
        byte[] pdf = EncryptedRichPdf();

        using (var s = Open(pdf))
            AssertTheTwoUriActions(PdfOperations.ExtractActions(s, UserPassword));

        using (var s = Open(pdf))
            Assert.Equal("Chapter1Dest", PdfOperations.ListDestinations(s, UserPassword).Single().Name);

        using (var s = Open(pdf))
            Assert.Equal("Watermark", PdfOperations.ListLayers(s, UserPassword).Single().Name);

        using (var s = Open(pdf))
        {
            var field = Assert.Single(PdfOperations.GetFormFields(s, UserPassword));
            Assert.Equal("surname", field.FullyQualifiedName);
            Assert.Equal("Cohen", field.Value);
        }

        using (var s = Open(pdf))
            Assert.Equal("Helvetica", PdfOperations.ListFonts(s, UserPassword).Single().BaseFont);

        using (var s = Open(pdf))
            Assert.Contains(PdfOperations.GetColorSpaces(s, UserPassword), c => c.Name == "CalRGB");
    }

    /// <summary>
    /// The two readers whose payload is a <b>stream</b> rather than a string: the decoded bytes must
    /// come back as plaintext, which no amount of object-graph walking produces on its own.
    /// </summary>
    [Fact]
    public void StreamPayloadReaders_ReturnDecryptedBytesWhenGivenThePassword()
    {
        byte[] pdf = EncryptedRichPdf();

        using (var s = Open(pdf))
            Assert.Equal("notes.txt", PdfOperations.ListAttachments(s, UserPassword).Single().Filename);

        using (var s = Open(pdf))
            Assert.Equal(AttachmentPayload, Encoding.ASCII.GetString(
                PdfOperations.ExtractAttachment(s, "notes.txt", UserPassword)));

        using (var s = Open(pdf))
            Assert.Equal(ThreeDPayload.Length,
                PdfOperations.List3DAnnotations(s, UserPassword).Single().DataLength);

        using (var s = Open(pdf))
            Assert.Equal(ThreeDPayload, Encoding.ASCII.GetString(
                PdfOperations.Extract3DStream(s, 0, UserPassword)));
    }

    // ------------------------------------------------------------------ without it

    /// <summary>
    /// With a decryptor registered and no password, every converted reader fails loudly. A silent
    /// empty list here is the defect: it is the same answer as "this document has no layers".
    /// </summary>
    [Fact]
    public void WithoutThePassword_EveryConvertedReaderFailsLoudlyRatherThanReturningNothing()
    {
        byte[] pdf = EncryptedRichPdf();

        void Refuses(Action<Stream> call)
        {
            using var s = Open(pdf);
            Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => call(s));
        }

        Refuses(s => PdfOperations.SearchText(s, "Locked"));
        Refuses(s => PdfOperations.ContainsText(s, "Locked"));
        Refuses(s => PdfOperations.ExtractActions(s));
        Refuses(s => PdfOperations.ListDestinations(s));
        Refuses(s => PdfOperations.ListLayers(s));
        Refuses(s => PdfOperations.GetFormFields(s));
        Refuses(s => PdfOperations.ListAttachments(s));
        Refuses(s => PdfOperations.ExtractAttachment(s, "notes.txt"));
        Refuses(s => PdfOperations.ListFonts(s));
        Refuses(s => PdfOperations.GetColorSpaces(s));
        Refuses(s => PdfOperations.List3DAnnotations(s));
        Refuses(s => PdfOperations.Extract3DStream(s, 0));
    }

    /// <summary>
    /// With <b>no decryptor registered at all</b> — the state of a host that never referenced
    /// <c>Tetitulz.Pdf.Encryption</c> — the converted readers inherit
    /// <c>WithDecrypted</c>'s diagnostic, and each refuses the encrypted document itself
    /// (<c>Base.Operations.EncryptedRead</c>) instead of reporting it as an empty one.
    /// </summary>
    /// <remarks>
    /// Asserted on two readers rather than one, because the diagnostic comes from the shared
    /// helper and is therefore inherited by every method routed through it, rather than
    /// remembered per method — which is how the original eight ended up being the only eight. The
    /// helper's reason stands beside each reader's refusal, as it does for the attachment readers below.
    /// </remarks>
    [Fact]
    public void WithNoDecryptorRegistered_ConvertedReadersRefuseAndSayWhy()
    {
        byte[] pdf = EncryptedRichPdf();     // registers, so save and restore around the calls
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;

            using (var s = Open(pdf))
            {
                Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(() => PdfOperations.ListLayers(s));
                Assert.Contains("encrypted", PdfOperations.LastExtractionError,
                    StringComparison.OrdinalIgnoreCase);
            }

            using (var s = Open(pdf))
            {
                Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(() => PdfOperations.ContainsText(s, "Locked away"));
                Assert.Contains("encrypted", PdfOperations.LastExtractionError,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { PdfDecryptorHolder.Current = saved; }
    }

    /// <summary>
    /// With no decryptor registered, the attachment readers <b>refuse</b> rather than answer. The
    /// attachment name tree is reachable through the trailer without decryption, so they used to
    /// come back with an entry whose filename was ciphertext, and an extraction returned encrypted
    /// bytes as the file: a plausible-looking wrong answer, told apart only by the diagnostic.
    /// </summary>
    [Fact]
    public void WithNoDecryptorRegistered_AttachmentReadersRefuseRatherThanAnswerWithCiphertext()
    {
        byte[] pdf = EncryptedRichPdf();     // registers, so save and restore around the calls
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;

            using (var s = Open(pdf))
                Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(() => PdfOperations.ListAttachments(s));
            using (var s = Open(pdf))
                Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(
                    () => PdfOperations.ExtractAttachment(s, "notes.txt"));
        }
        finally { PdfDecryptorHolder.Current = saved; }

        // The control: with the decryptor back and the password, the same document answers.
        using (var s = Open(pdf))
            Assert.Equal("notes.txt", PdfOperations.ListAttachments(s, UserPassword).Single().Filename);
    }

    /// <summary>
    /// A converted reader leaves <c>LastExtractionError</c> null on a document that needed no
    /// decryption — the slot describes this call, not some earlier one.
    /// </summary>
    [Fact]
    public void ConvertedReader_ClearsTheDiagnosticOnASuccessfulRead()
    {
        byte[] encrypted = EncryptedRichPdf();
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;
            using var locked = Open(encrypted);
            Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(() => PdfOperations.ListLayers(locked));
        }
        finally { PdfDecryptorHolder.Current = saved; }
        Assert.NotNull(PdfOperations.LastExtractionError);

        using var plain = Open(RichPdf());
        Assert.Equal("Watermark", PdfOperations.ListLayers(plain).Single().Name);
        Assert.Null(PdfOperations.LastExtractionError);
    }
}
