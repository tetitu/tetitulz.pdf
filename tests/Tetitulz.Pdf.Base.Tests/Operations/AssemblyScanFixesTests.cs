// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Defects found by the 2026-08 assembly scan in <c>Base.Operations</c>, each pinned by the
/// evidence it produced rather than by "the call returned something".
///
/// <list type="bullet">
///   <item><b>PdfSanitizer</b> — the <c>/Next</c> action-chain sweep was unreachable dead code, and
///     the catalog <c>/Names /EmbeddedFiles</c> sweep removed the key while leaving every byte of
///     the attachment in the output.</item>
///   <item><b>PdfTemplateEngine</b> — an inline image was re-serialised as
///     <c>&lt;&lt; … &gt;&gt; BI</c>, destroying the image and swallowing the rest of the page.</item>
///   <item><b>PdfFormFlattener</b> — a silent no-op on an encrypted document, and it deleted check
///     boxes, radio buttons and every non-widget annotation from the page.</item>
///   <item><b>PdfImageInserter</b> — <c>/DeviceRGB</c> written over a grayscale or CMYK
///     codestream.</item>
///   <item><b>PdfColorSpaceConverter</b> — a JPEG whose <c>/Filter</c> is an array was read as raw
///     RGB and overwritten with static.</item>
/// </list>
/// </summary>
public class AssemblyScanFixesTests
{
    private static string Latin1(byte[] data) => Encoding.Latin1.GetString(data);

    /// <summary>
    /// Pins what the chain fixture actually parses to, before sanitization touches it: a
    /// <c>/GoTo</c> whose own <c>/S</c> is harmless, chaining to a JavaScript action. Without this
    /// the tests would still pass if the nested <c>/Next</c> dictionary were mis-parsed into its
    /// parent — the whole <c>/A</c> would then be removed for the wrong reason, and the /Next sweep
    /// would go on being untested.
    /// </summary>
    private static void AssertFixtureIsAGoToChainingToJavaScript(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        var annot = doc.ResolveObject(annots[0]) as PdfDictionary;
        var action = doc.ResolveObject(annot[PdfName.A]) as PdfDictionary;

        Assert.Equal("GoTo", action.GetName(PdfName.S)?.Value);
        var next = doc.ResolveObject(action[PdfName.Next]) as PdfDictionary;
        Assert.NotNull(next);
        Assert.Equal("JavaScript", next.GetName(PdfName.S)?.Value);
    }

    // ==================================================================
    //  PdfSanitizer — /Next action chains
    // ==================================================================

    private const string ChainedJsPayload = "app.launchURL('http://evil.example')";

    /// <summary>
    /// §12.6.3: an action's <c>/Next</c> continues the chain. <c>SanitizeNextChain</c> borrowed the
    /// shared visited set, which <c>SanitizeDictionary</c> had already added the same dictionary to,
    /// so its first act was always <c>return</c> and <b>no chain was ever severed on any path</b>.
    /// A harmless <c>/GoTo</c> whose <c>/Next</c> is a direct inline JavaScript action therefore
    /// survived sanitization intact: its own <c>/S</c> is clean, and a direct sub-dictionary is
    /// never enumerated as an object either.
    /// </summary>
    [Fact]
    public void Sanitize_SeversANextChainLeadingToJavaScript()
    {
        byte[] pdf = PdfWithChainedNextAction(indirectAction: true);
        Assert.Contains(ChainedJsPayload, Latin1(pdf));
        AssertFixtureIsAGoToChainingToJavaScript(pdf);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions { RemoveJavaScript = true });

        byte[] cleaned = output.ToArray();
        Assert.DoesNotContain(ChainedJsPayload, Latin1(cleaned));

        // …and the surviving action really is the GoTo, not a document the sanitizer emptied.
        using var doc = PdfDocument.Open(new MemoryStream(cleaned));
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        var annot = doc.ResolveObject(annots[0]) as PdfDictionary;
        var action = doc.ResolveObject(annot[PdfName.A]) as PdfDictionary;
        Assert.Equal("GoTo", action.GetName(PdfName.S).Value);
        Assert.False(action.ContainsKey(PdfName.Next), "the dangerous /Next link must be gone");
    }

    /// <summary>
    /// The same chain written as a <b>direct</b> <c>/A</c> dictionary on the annotation. Nothing
    /// enumerates it as an object, so the only visit it can get is the recursion from the
    /// annotation — which <c>/AA</c> has always done and <c>/A</c> did not.
    /// </summary>
    [Fact]
    public void Sanitize_SeversANextChainReachedThroughADirectActionDictionary()
    {
        byte[] pdf = PdfWithChainedNextAction(indirectAction: false);
        Assert.Contains(ChainedJsPayload, Latin1(pdf));
        AssertFixtureIsAGoToChainingToJavaScript(pdf);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions { RemoveJavaScript = true });

        byte[] cleaned = output.ToArray();
        Assert.DoesNotContain(ChainedJsPayload, Latin1(cleaned));

        // The GoTo must SURVIVE with its /Next severed. Asserting only on the payload string would
        // also pass if the whole /A had been removed — a different (and wrong) outcome that would
        // leave the chain sweep untested.
        using var doc = PdfDocument.Open(new MemoryStream(cleaned));
        var annots = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]) as PdfArray;
        var annot = doc.ResolveObject(annots[0]) as PdfDictionary;
        var action = doc.ResolveObject(annot[PdfName.A]) as PdfDictionary;
        Assert.NotNull(action);
        Assert.Equal("GoTo", action.GetName(PdfName.S)?.Value);
        Assert.False(action.ContainsKey(PdfName.Next), "the dangerous /Next link must be gone");
    }

    /// <summary>
    /// A <c>/Next</c> <b>cycle</b> — A chains to B and B back to A. The dedicated visited set still
    /// has to break it; without a cycle guard this walk would not terminate.
    /// </summary>
    [Fact]
    public void Sanitize_ANextChainCycle_TerminatesAndStillStripsThePayload()
    {
        byte[] pdf = PdfWithNextCycle();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions { RemoveJavaScript = true });

        byte[] cleaned = output.ToArray();
        Assert.DoesNotContain(ChainedJsPayload, Latin1(cleaned));
        using var doc = PdfDocument.Open(new MemoryStream(cleaned));
        Assert.Single(doc.Pages);
    }

    // ==================================================================
    //  PdfSanitizer — catalog /Names /EmbeddedFiles
    // ==================================================================

    private const string CatalogAttachmentPayload = "CATALOG-EMBEDDED-FILE-PAYLOAD";

    /// <summary>
    /// The mirror image of <c>Sanitize_RemovesFileAttachmentEmbeddedFile</c>. Step 5 writes every
    /// object the parser enumerated rather than a reachability walk, so removing only the
    /// <c>/EmbeddedFiles</c> key left the name-tree nodes, the <c>/Filespec</c> and the
    /// <c>/Type /EmbeddedFile</c> stream in the "clean" output with the payload intact — a mail
    /// gateway that sanitized and then trusted the result passed the file through.
    /// </summary>
    [Fact]
    public void Sanitize_RemovesTheBytesBehindTheCatalogEmbeddedFilesNameTree()
    {
        byte[] pdf = PdfWithCatalogEmbeddedFile(withKidsNode: false);
        Assert.Contains(CatalogAttachmentPayload, Latin1(pdf));

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions { RemoveEmbeddedFiles = true });

        byte[] cleaned = output.ToArray();
        Assert.DoesNotContain(CatalogAttachmentPayload, Latin1(cleaned));

        using var doc = PdfDocument.Open(new MemoryStream(cleaned));
        var names = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]) as PdfDictionary;
        Assert.False(names != null && names.ContainsKey(PdfName.EmbeddedFiles),
            "the /EmbeddedFiles entry must be gone too");
    }

    /// <summary>
    /// The same through an intermediate <c>/Kids</c> node, which is the shape a real name tree takes
    /// once it has more than a handful of entries. A leaf-only walk would miss it.
    /// </summary>
    [Fact]
    public void Sanitize_RemovesTheBytesThroughAnIntermediateNameTreeNode()
    {
        byte[] pdf = PdfWithCatalogEmbeddedFile(withKidsNode: true);
        Assert.Contains(CatalogAttachmentPayload, Latin1(pdf));

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions { RemoveEmbeddedFiles = true });

        Assert.DoesNotContain(CatalogAttachmentPayload, Latin1(output.ToArray()));
    }

    /// <summary>
    /// The control: with <c>RemoveEmbeddedFiles</c> off the attachment must survive, so the two
    /// tests above cannot be satisfied by a sanitizer that drops streams unconditionally.
    /// </summary>
    [Fact]
    public void Sanitize_WithoutRemoveEmbeddedFiles_KeepsTheAttachment()
    {
        byte[] pdf = PdfWithCatalogEmbeddedFile(withKidsNode: false);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions
        {
            RemoveEmbeddedFiles = false,
            RemoveJavaScript = true,
        });

        Assert.Contains(CatalogAttachmentPayload, Latin1(output.ToArray()));
    }

    // ==================================================================
    //  PdfTemplateEngine — inline images
    // ==================================================================

    private static readonly byte[] InlineImagePayload = { 0xDE, 0xAD, 0xBE, 0xEF };

    /// <summary>
    /// <c>ContentStreamParser</c> returns an inline image as one operation whose single operand is a
    /// <see cref="PdfStream"/>. The generic re-serialiser has no stream case and
    /// <see cref="PdfStream"/> derives from <see cref="PdfDictionary"/>, so it wrote
    /// <c>&lt;&lt; /W 2 … &gt;&gt; BI</c> — operand and operator inverted, the sample bytes dropped,
    /// no <c>ID</c> and no <c>EI</c>. A reader hitting that bare <c>BI</c> consumes the rest of the
    /// page looking for <c>ID</c>, so the image and the surviving text both vanished.
    /// </summary>
    [Fact]
    public void Replace_KeepsAnInlineImageByteForByte()
    {
        byte[] pdf = PdfWithInlineImageAndToken();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var result = PdfTemplateEngine.Replace(input, output,
            new Dictionary<string, string> { ["name"] = "Bob" },
            new TemplateReplaceOptions { VerifyErasure = false });

        Assert.Contains("name", result.ReplacedKeys);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        byte[] content = doc.Pages[0].GetContentStreamData();

        // Parsed, not substring-matched: the claim is that the rewritten stream still contains a
        // well-formed inline image carrying the original samples.
        var ops = ContentStreamParser.Parse(content);
        var inline = ops.SingleOrDefault(o => o.Operator == PdfOperators.BeginInlineImage);
        Assert.NotNull(inline);
        var stream = Assert.IsType<PdfStream>(Assert.Single(inline.Operands));
        Assert.Equal(InlineImagePayload, stream.RawData);
        Assert.Equal(2, (int)(stream.GetDouble(new PdfName("W")) ?? 0));

        // …and the token really was erased, so this is the rewrite path and not a copy-through.
        Assert.DoesNotContain("{{name}}", Latin1(content));
    }

    // ==================================================================
    //  PdfFormFlattener
    // ==================================================================

    /// <summary>
    /// An encrypted document loads no catalog, so its <c>AcroForm</c> always reads as absent and the
    /// flattener took its "nothing to flatten" branch: it copied the input through verbatim and
    /// returned void. A workflow flattening a filled form before archiving it archived a
    /// still-editable one and was told nothing.
    /// </summary>
    [Fact]
    public void Flatten_OnAnEncryptedDocument_FailsClosedWithAReason()
    {
        byte[] encrypted = Encrypt(PdfWithCheckBoxAndLink());

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfFormFlattener.Flatten(input, output));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);   // and nothing was written as if it had worked
    }

    /// <summary>
    /// §12.5.5 makes a check box's <c>/AP /N</c> a sub-dictionary of appearance states selected by
    /// <c>/AS</c>, not a stream. Reading it as <c>apDict[/N] as PdfReference</c> yielded null, the
    /// widget was skipped by the stamping loop — and then erased anyway, because the page dictionary
    /// was rebuilt without <c>/Annots</c> unconditionally. The tick mark disappeared from the
    /// flattened document entirely.
    /// </summary>
    [Fact]
    public void Flatten_StampsACheckedCheckBoxAppearance()
    {
        byte[] pdf = PdfWithCheckBoxAndLink();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfFormFlattener.Flatten(input, output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var page = doc.Pages[0];

        // The /Yes appearance is now invoked from the page's own content …
        var xobjects = doc.ResolveObject(
            (doc.ResolveObject(page.Dictionary[PdfName.Resources]) as PdfDictionary)?[PdfName.XObject])
            as PdfDictionary;
        Assert.NotNull(xobjects);
        bool stampsTheOnState = xobjects.Keys.Any(k =>
            (doc.ResolveObject(xobjects[k]) as PdfStream)?.RawData is byte[] d &&
            Latin1(d).Contains("CHECKED-APPEARANCE"));
        Assert.True(stampsTheOnState,
            "the /AS-selected /Yes appearance stream should have been stamped onto the page");

        string content = Latin1(page.GetContentStreamData());
        Assert.Contains(" Do", content);
    }

    /// <summary>
    /// Flattening a form has no business deleting a hyperlink. <c>/Annots</c> was rebuilt without
    /// the key at all, so every Link, Text, Popup and Stamp annotation on the page went with the
    /// widgets.
    /// </summary>
    [Fact]
    public void Flatten_KeepsEveryNonWidgetAnnotation()
    {
        byte[] pdf = PdfWithCheckBoxAndLink();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfFormFlattener.Flatten(input, output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var annots = doc.Pages[0].GetAnnotations();
        Assert.NotNull(annots);

        var subtypes = new List<string>();
        for (int i = 0; i < annots.Count; i++)
            subtypes.Add((doc.ResolveObject(annots[i]) as PdfDictionary)?.GetName(PdfName.Subtype)?.Value);

        Assert.Contains(PdfAnnotationSubtypes.Link, subtypes);
        // …and the widget really was flattened away, so this is not "nothing was touched".
        Assert.DoesNotContain(PdfAnnotationSubtypes.Widget, subtypes);
        Assert.False(doc.Catalog.Dictionary.ContainsKey(PdfName.AcroForm));
    }

    // ==================================================================
    //  PdfImageInserter
    // ==================================================================

    /// <summary>
    /// <c>InsertJpeg</c> wrote <c>/ColorSpace /DeviceRGB</c> unconditionally, so a grayscale JPEG
    /// produced an XObject declaring three components per pixel over a one-component frame. This
    /// library's own renderer trusts the codestream and shows nothing wrong; a conforming reader
    /// that honours the declared space reports a damaged image.
    /// </summary>
    [Theory]
    [InlineData(1, "DeviceGray")]
    [InlineData(3, "DeviceRGB")]
    public void InsertJpeg_TakesTheColourSpaceFromTheCodestream(int components, string expected)
    {
        byte[] jpeg = Jpeg(width: 8, height: 4, components: components, adobe: false);
        byte[] pdf = MinimalPageOnly();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfImageInserter.InsertJpeg(input, output, 0, jpeg, 8, 4, 10, 10, 40, 20);

        PdfStream image = SingleImageXObject(output.ToArray());
        Assert.Equal(expected, image.GetName(PdfName.ColorSpace).Value);
        Assert.Equal(8, (int)(image.GetDouble(PdfName.Width) ?? 0));
        Assert.Equal(4, (int)(image.GetDouble(PdfName.Height) ?? 0));
    }

    /// <summary>
    /// An Adobe-marked CMYK JPEG stores its components inverted, so it needs
    /// <c>/Decode [1 0 1 0 1 0 1 0]</c>. Without it the photo renders colour-negative.
    /// </summary>
    [Fact]
    public void InsertJpeg_EmitsTheInvertedDecodeForAnAdobeCmykJpeg()
    {
        byte[] jpeg = Jpeg(width: 8, height: 4, components: 4, adobe: true);

        using var input = new MemoryStream(MinimalPageOnly());
        using var output = new MemoryStream();
        PdfImageInserter.InsertJpeg(input, output, 0, jpeg, 8, 4, 10, 10, 40, 20);

        PdfStream image = SingleImageXObject(output.ToArray());
        Assert.Equal("DeviceCMYK", image.GetName(PdfName.ColorSpace).Value);
        var decode = image.GetArray(PdfName.Decode);
        Assert.NotNull(decode);
        Assert.Equal(new double[] { 1, 0, 1, 0, 1, 0, 1, 0 },
            Enumerable.Range(0, decode.Count).Select(i => ((PdfNumber)decode[i]).DoubleValue).ToArray());
    }

    /// <summary>
    /// Four components with no Adobe marker: the polarity is unknowable from the codestream, and
    /// guessing produces an inverted image that still opens fine. Refused rather than guessed.
    /// </summary>
    [Fact]
    public void InsertJpeg_RefusesACmykJpegWithNoAdobeMarker()
    {
        byte[] jpeg = Jpeg(width: 8, height: 4, components: 4, adobe: false);

        using var input = new MemoryStream(MinimalPageOnly());
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(
            () => PdfImageInserter.InsertJpeg(input, output, 0, jpeg, 8, 4, 10, 10, 40, 20));
        Assert.Contains("Adobe", ex.Message);
    }

    /// <summary>
    /// Caller-supplied dimensions are validated against the SOF rather than trusted: a mismatch used
    /// to be written into the dictionary and silently corrupt the page geometry.
    /// </summary>
    [Fact]
    public void InsertJpeg_RefusesDimensionsThatDisagreeWithTheFrameHeader()
    {
        byte[] jpeg = Jpeg(width: 8, height: 4, components: 3, adobe: false);

        using var input = new MemoryStream(MinimalPageOnly());
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(
            () => PdfImageInserter.InsertJpeg(input, output, 0, jpeg, 800, 600, 10, 10, 40, 20));
        Assert.Contains("8x4", ex.Message);
    }

    // ==================================================================
    //  PdfColorSpaceConverter
    // ==================================================================

    /// <summary>
    /// The "only convert what we can decode losslessly" guard read <c>/Filter</c> with
    /// <c>GetName</c>, which is <c>v as PdfName</c> and so returns null for an array — the same
    /// value that means "unfiltered, convert it". A DCTDecode image therefore passed the guard, its
    /// codestream was read as RGB triples, and the photo was permanently replaced with flate
    /// -compressed static while the call reported success.
    /// </summary>
    /// <remarks>
    /// The array cases are the regression; the bare <c>/DCTDecode</c> name is the control the old
    /// guard did handle, kept so the theory shows which spelling was the difference. Each payload
    /// is long enough to satisfy the <c>width * height * 3</c> length check on its own, so the
    /// <c>/Filter</c> guard is what does the skipping and not an accidental short buffer.
    /// </remarks>
    [Theory]
    [InlineData("[/DCTDecode]", false)]
    [InlineData("[/ASCII85Decode /DCTDecode]", true)]
    [InlineData("/DCTDecode", false)]
    public void ConvertRgbToGrayscale_LeavesAJpegUntouched(string filter, bool ascii85)
    {
        byte[] stored = ascii85 ? Ascii85(JpegLikeBytes()) : JpegLikeBytes();
        byte[] pdf = PdfWithRgbImage(filter, stored);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToGrayscale(input, output);

        Assert.Equal(0, converted);

        PdfStream image = SingleImageXObject(output.ToArray());
        Assert.Equal("DeviceRGB", image.GetName(PdfName.ColorSpace).Value);
        Assert.Equal(stored, image.RawData);
    }

    /// <summary>
    /// The control: an actually-unfiltered DeviceRGB image must still convert, so the test above
    /// cannot be satisfied by a converter that now skips everything.
    /// </summary>
    [Fact]
    public void ConvertRgbToGrayscale_StillConvertsAnUnfilteredRgbImage()
    {
        byte[] samples = { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 };   // 4 px
        byte[] pdf = PdfWithRgbImage(filter: null, samples: samples, width: 4, height: 1);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToGrayscale(input, output);

        Assert.Equal(1, converted);

        PdfStream image = SingleImageXObject(output.ToArray());
        Assert.Equal("DeviceGray", image.GetName(PdfName.ColorSpace).Value);
        byte[] gray = image.GetDecodedData();
        Assert.Equal(4, gray.Length);
        Assert.Equal(255, gray[3]);                       // white stays white
        Assert.True(gray[0] > 0 && gray[0] < 128, $"red should luminance to a dark grey, was {gray[0]}");
    }

    // ==================================================================
    //  Fixtures
    // ==================================================================

    private static byte[] Encrypt(byte[] pdf)
    {
        PdfEncryptionOperations.Register();
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = "s3cret",
            OwnerPassword = "owner-only",
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }

    /// <summary>The single image XObject of the first page.</summary>
    private static PdfStream SingleImageXObject(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var res = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary
                  ?? doc.Pages[0].Resources;
        var xobjects = doc.ResolveObject(res[PdfName.XObject]) as PdfDictionary;
        Assert.NotNull(xobjects);

        var images = xobjects.Keys
            .Select(k => doc.ResolveObject(xobjects[k]) as PdfStream)
            .Where(s => s != null && s.GetName(PdfName.Subtype)?.Value == "Image")
            .ToList();
        return Assert.Single(images);
    }

    /// <summary>
    /// A JPEG carrying nothing but the markers <c>JpegHeader</c> reads: SOI, an optional Adobe
    /// APP14, a SOF0 with the requested geometry, and EOI. Nothing decodes these bytes — they are
    /// embedded verbatim as <c>DCTDecode</c> — so a synthetic frame header is the whole fixture and
    /// keeps it independent of whatever Skia's encoder chooses to emit.
    /// </summary>
    private static byte[] Jpeg(int width, int height, int components, bool adobe)
    {
        var b = new List<byte> { 0xFF, 0xD8 };

        if (adobe)
        {
            byte[] tag = Encoding.ASCII.GetBytes("Adobe");
            int len = 2 + tag.Length + 2 + 2 + 2 + 1;      // length + tag + version + flags0/1 + transform
            b.AddRange(new byte[] { 0xFF, 0xEE, (byte)(len >> 8), (byte)len });
            b.AddRange(tag);
            b.AddRange(new byte[] { 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x02 });   // YCCK transform
        }

        int sofLen = 8 + components * 3;
        b.AddRange(new byte[] { 0xFF, 0xC0, (byte)(sofLen >> 8), (byte)sofLen, 0x08 });
        b.AddRange(new byte[] { (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width });
        b.Add((byte)components);
        for (int i = 0; i < components; i++) b.AddRange(new byte[] { (byte)(i + 1), 0x11, 0x00 });

        b.AddRange(new byte[] { 0xFF, 0xD9 });
        return b.ToArray();
    }

    /// <summary>
    /// Bytes that begin like a JPEG, to stand in for a real codestream's payload. Deliberately
    /// longer than <c>2 * 2 * 3</c>, so a converter that reads them as RGB triples gets far enough
    /// to overwrite the image rather than bailing out on a short buffer.
    /// </summary>
    private static byte[] JpegLikeBytes() => new byte[]
    {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43,
        0xFF, 0xD9,
    };

    /// <summary>ASCII85 (§7.4.3), so the two-stage <c>/Filter</c> case really does decode.</summary>
    private static byte[] Ascii85(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint word = 0;
            for (int j = 0; j < 4; j++)
                word = (word << 8) | (j < n ? data[i + j] : 0u);

            if (word == 0 && n == 4) { sb.Append('z'); continue; }

            var group = new char[5];
            for (int j = 4; j >= 0; j--) { group[j] = (char)('!' + word % 85); word /= 85; }
            sb.Append(group, 0, n + 1);
        }
        sb.Append("~>");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] MinimalPageOnly() => Build(
        catalogExtras: "",
        pageExtras: "/MediaBox [0 0 200 200]",
        content: "0 0 0 rg\n",
        extraObjects: null);

    /// <summary>
    /// A page whose annotation carries a <c>/GoTo</c> action chaining, via a direct inline
    /// <c>/Next</c>, to a JavaScript action.
    /// </summary>
    private static byte[] PdfWithChainedNextAction(bool indirectAction)
    {
        string chain = "<< /S /GoTo /D [3 0 R /Fit] /Next << /S /JavaScript /JS ("
                     + ChainedJsPayload + ") >> >>";

        return indirectAction
            ? Build("", "/MediaBox [0 0 200 200] /Annots [5 0 R]", "0 0 0 rg\n",
                    new[]
                    {
                        "5 0 obj\n<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A 6 0 R >>\nendobj\n",
                        $"6 0 obj\n{chain}\nendobj\n",
                    })
            : Build("", "/MediaBox [0 0 200 200] /Annots [5 0 R]", "0 0 0 rg\n",
                    new[]
                    {
                        $"5 0 obj\n<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A {chain} >>\nendobj\n",
                    });
    }

    /// <summary>Two indirect actions whose <c>/Next</c> entries point at each other.</summary>
    private static byte[] PdfWithNextCycle() => Build(
        "", "/MediaBox [0 0 200 200] /Annots [5 0 R]", "0 0 0 rg\n",
        new[]
        {
            "5 0 obj\n<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A 6 0 R >>\nendobj\n",
            "6 0 obj\n<< /S /GoTo /Next 7 0 R >>\nendobj\n",
            $"7 0 obj\n<< /S /GoTo /Next 6 0 R /JS ({ChainedJsPayload}) >>\nendobj\n",
        });

    /// <summary>
    /// Catalog → <c>/Names /EmbeddedFiles</c> → (optionally a <c>/Kids</c> node →) a leaf whose
    /// value is a <c>/Filespec</c> pointing at the payload stream.
    /// </summary>
    private static byte[] PdfWithCatalogEmbeddedFile(bool withKidsNode)
    {
        string catalogExtras = "/Names << /EmbeddedFiles 5 0 R >>";
        var objects = new List<string>();

        if (withKidsNode)
        {
            objects.Add("5 0 obj\n<< /Kids [8 0 R] >>\nendobj\n");
            objects.Add("6 0 obj\n<< /Type /Filespec /F (payload.exe) /EF << /F 7 0 R >> >>\nendobj\n");
            objects.Add($"7 0 obj\n<< /Type /EmbeddedFile /Length {CatalogAttachmentPayload.Length} >>\n"
                        + $"stream\n{CatalogAttachmentPayload}\nendstream\nendobj\n");
            objects.Add("8 0 obj\n<< /Names [(payload.exe) 6 0 R] /Limits [(payload.exe) (payload.exe)] >>\nendobj\n");
        }
        else
        {
            objects.Add("5 0 obj\n<< /Names [(payload.exe) 6 0 R] >>\nendobj\n");
            objects.Add("6 0 obj\n<< /Type /Filespec /F (payload.exe) /EF << /F 7 0 R >> >>\nendobj\n");
            objects.Add($"7 0 obj\n<< /Type /EmbeddedFile /Length {CatalogAttachmentPayload.Length} >>\n"
                        + $"stream\n{CatalogAttachmentPayload}\nendstream\nendobj\n");
        }

        return Build(catalogExtras, "/MediaBox [0 0 200 200]", "0 0 0 rg\n", objects.ToArray());
    }

    /// <summary>
    /// A page drawing a 2x2 grayscale inline image and then a text object holding the template
    /// token. The image's payload is exactly <c>/W * /H * (/BPC / 8)</c> bytes, so the parser
    /// settles its boundary by computation rather than by scanning for <c>EI</c>.
    /// </summary>
    private static byte[] PdfWithInlineImageAndToken()
    {
        var content = new List<byte>();
        content.AddRange(Encoding.Latin1.GetBytes("q 20 0 0 20 10 160 cm\nBI /W 2 /H 2 /BPC 8 /CS /G ID "));
        content.AddRange(InlineImagePayload);
        content.AddRange(Encoding.Latin1.GetBytes("\nEI Q\nBT /F1 12 Tf 20 100 Td ({{name}}) Tj ET\n"));

        return BuildBinary(
            catalogExtras: "",
            pageExtras: "/MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >>",
            content: content.ToArray(),
            extraObjects: new[]
            {
                "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
            });
    }

    /// <summary>
    /// A checked check box (<c>/AP /N &lt;&lt; /Yes … /Off … &gt;&gt;</c> with <c>/AS /Yes</c>)
    /// beside a Link annotation, both on the page's <c>/Annots</c>.
    /// </summary>
    private static byte[] PdfWithCheckBoxAndLink()
    {
        const string onAp = "CHECKED-APPEARANCE 0 0 0 rg 0 0 20 20 re f";
        const string offAp = "0 0 20 20 re S";

        return Build(
            catalogExtras: "/AcroForm << /Fields [5 0 R] >>",
            pageExtras: "/MediaBox [0 0 200 200] /Annots [5 0 R 8 0 R]",
            content: "0 0 0 rg\n",
            extraObjects: new[]
            {
                "5 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Btn /T (agree) /Rect [20 20 40 40] "
                    + "/AS /Yes /AP << /N << /Yes 6 0 R /Off 7 0 R >> >> >>\nendobj\n",
                $"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length {onAp.Length} >>\n"
                    + $"stream\n{onAp}\nendstream\nendobj\n",
                $"7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length {offAp.Length} >>\n"
                    + $"stream\n{offAp}\nendstream\nendobj\n",
                "8 0 obj\n<< /Type /Annot /Subtype /Link /Rect [100 100 150 120] "
                    + "/A << /S /URI /URI (https://example.test/terms) >> >>\nendobj\n",
            });
    }

    private static byte[] PdfWithRgbImage(string filter, byte[] samples, int width = 2, int height = 2)
    {
        string filterEntry = filter == null ? string.Empty : $"/Filter {filter} ";
        var image = new List<byte>();
        image.AddRange(Encoding.Latin1.GetBytes(
            $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} "
            + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 {filterEntry}/Length {samples.Length} >>\nstream\n"));
        image.AddRange(samples);
        image.AddRange(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));

        return BuildBinary(
            catalogExtras: "",
            pageExtras: "/MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R >> >>",
            content: Encoding.Latin1.GetBytes("q 20 0 0 20 10 10 cm /Im0 Do Q\n"),
            rawObjects: new[] { image.ToArray() });
    }

    private static byte[] Build(string catalogExtras, string pageExtras, string content,
                                string[] extraObjects)
        => BuildBinary(catalogExtras, pageExtras, Encoding.Latin1.GetBytes(content), extraObjects);

    private static byte[] BuildBinary(string catalogExtras, string pageExtras, byte[] content,
                                      string[] extraObjects = null, byte[][] rawObjects = null)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R {catalogExtras} >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R {pageExtras} /Contents 4 0 R >>\nendobj\n");

        offsets.Add(acc.Count);
        Emit($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        acc.AddRange(content);
        Emit("\nendstream\nendobj\n");

        foreach (string body in extraObjects ?? Array.Empty<string>()) Obj(body);
        foreach (byte[] body in rawObjects ?? Array.Empty<byte[]>())
        {
            offsets.Add(acc.Count);
            acc.AddRange(body);
        }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
