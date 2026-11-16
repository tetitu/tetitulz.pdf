// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Public-facade findings from the assembly scan: OCR's five silent nulls, the duplicated
/// conformance-validation pair, the missing password overloads, the file-path readers that never
/// decrypted, and <c>RotateAllPages</c> stacking one revision per page.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class FacadeFindingsTests : IDisposable
{
    public FacadeFindingsTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    // ==================================================================
    //  RecognizeTextWithOcr — five causes, one null, now one reason each
    // ==================================================================

    [Fact]
    public void RecognizeTextWithOcr_NoPluginRegistered_SaysNoEngineWasConsulted()
    {
        // A host that ships Tetitulz.Pdf.Plugins.Ocr but forgets RegisterFromLoadedAssemblies()
        // got the same bare null as a genuinely blank scan, and logged "no text on this page" —
        // the wrong conclusion, and one the API surface offered no way to correct.
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());

        string text = PdfOperations.RecognizeTextWithOcr(input, 0);

        Assert.Null(text);
        Assert.NotNull(PdfOperations.LastOcrError);
        Assert.Contains("IOcrPlugin", PdfOperations.LastOcrError, StringComparison.Ordinal);
        Assert.Contains("RegisterFromLoadedAssemblies", PdfOperations.LastOcrError,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// With no engine registered the page is not rendered first to learn so: a page the renderer
    /// cannot reach is answered with the missing engine, not with the page — for the word and the
    /// text recognisers alike. A scan of two thousand pages was rasterised, encoded and decoded
    /// page by page before each was told no engine could read it.
    /// </summary>
    [Fact]
    public void WithNoEngineThePageIsNotRenderedFirst()
    {
        using (var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()))
            Assert.Null(PdfOperations.RecognizeWordsWithOcr(input, 99));
        Assert.Contains("IOcrWordPlugin", PdfOperations.LastOcrError, StringComparison.Ordinal);

        using (var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()))
            Assert.Null(PdfOperations.RecognizeTextWithOcr(input, 99));
        Assert.Contains("IOcrPlugin", PdfOperations.LastOcrError, StringComparison.Ordinal);
    }

    [Fact]
    public void RecognizeTextWithOcr_PluginFindsNothing_IsDistinguishableFromNoPlugin()
    {
        // "The engine ran and found nothing" and "no engine ran" are opposite conclusions about
        // a scan, and used to be the same value.
        PdfPluginHost.Instance.Register(new FakeOcrPlugin(result: null));
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.Null(PdfOperations.RecognizeTextWithOcr(input, 0));

        Assert.NotNull(PdfOperations.LastOcrError);
        Assert.Contains("returned no text", PdfOperations.LastOcrError, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterFromLoadedAssemblies", PdfOperations.LastOcrError,
                              StringComparison.Ordinal);
    }

    [Fact]
    public void RecognizeTextWithOcr_LastOcrError_IsClearedAtTheStartOfEveryCall()
    {
        using (var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()))
            PdfOperations.RecognizeTextWithOcr(input, 0);
        Assert.NotNull(PdfOperations.LastOcrError);

        PdfPluginHost.Instance.Register(new FakeOcrPlugin(result: "recognised"));
        using (var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()))
            Assert.Equal("recognised", PdfOperations.RecognizeTextWithOcr(input, 0));

        // A slot read after an operation must be cleared at its start, or the previous page's
        // reason answers this page's question.
        Assert.Null(PdfOperations.LastOcrError);
    }

    private sealed class FakeOcrPlugin : IOcrPlugin
    {
        private readonly string _result;
        public FakeOcrPlugin(string result) => _result = result;

        public string Name => "Test.Ocr";
        public string Description => "test OCR plugin";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public bool CanProcess(int components, int bitsPerComponent) =>
            components == 3 && bitsPerComponent == 8;

        public string RecognizeText(byte[] pixels, int width, int height, int components) => _result;
    }

    // ==================================================================
    //  The duplicated conformance-validation pair
    // ==================================================================

    [Fact]
    public void ValidateConformance_IsMarkedObsoleteAndPointsAtValidate()
    {
        // Two public methods for one dispatch with opposite failure contracts means no single
        // wrapper can be correct for both. The survivor is the pair CLAUDE.md documents.
        MethodInfo m = typeof(PdfOperations).GetMethod(
            nameof(PdfOperations.ValidateConformance), new[] { typeof(Stream), typeof(string) });
        Assert.NotNull(m);

        var obsolete = m.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obsolete);
        Assert.Contains("Validate", obsolete.Message, StringComparison.Ordinal);

        MethodInfo levels = typeof(PdfOperations).GetMethod(
            nameof(PdfOperations.GetAvailableConformanceLevels), Type.EmptyTypes);
        Assert.NotNull(levels.GetCustomAttribute<ObsoleteAttribute>());
    }

    [Fact]
    public void ValidateConformance_NoPluginRegistered_ThrowsWithValidatesOwnReason()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());

#pragma warning disable CS0618 // the obsolete member is exactly what is under test
        var ex = Assert.Throws<NotSupportedException>(
            () => PdfOperations.ValidateConformance(input, "PDF/UA-1"));
#pragma warning restore CS0618

        // Reimplemented on top of Validate, so it now reports the same reason rather than a
        // separately worded one that could drift.
        Assert.Equal(PdfOperations.LastValidationError, ex.Message);
        Assert.Contains("PDF/UA-1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConformance_MidPositionStream_ReachesThePluginRewound()
    {
        // It used to skip the PdfHelpers.EnsureSeekable that Validate applies, so a stream the
        // caller had already read from arrived at the plugin unrewound.
        var plugin = new FakeValidationPlugin("PDF/X-1a");
        PdfPluginHost.Instance.Register(plugin);

        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        input.Position = 17;

#pragma warning disable CS0618
        PdfOperations.ValidateConformance(input, "PDF/X-1a");
#pragma warning restore CS0618

        Assert.Equal(0L, plugin.PositionSeen);
    }

    [Fact]
    public void GetAvailableConformanceLevels_IsTheSupportedListWithoutTheEmptyEntries()
    {
        PdfPluginHost.Instance.Register(new FakeValidationPlugin("PDF/X-1a"));
        PdfPluginHost.Instance.Register(new FakeValidationPlugin(string.Empty, "Test.Blank"));

#pragma warning disable CS0618
        var available = PdfOperations.GetAvailableConformanceLevels();
#pragma warning restore CS0618
        var supported = PdfOperations.GetSupportedConformanceLevels();

        Assert.Equal(supported.Where(l => !string.IsNullOrEmpty(l)).ToList(), available);
        Assert.Contains("PDF/X-1a", available);
    }

    private sealed class FakeValidationPlugin : IValidationPlugin
    {
        public FakeValidationPlugin(string level, string name = "Test.Validator")
        { ConformanceLevel = level; Name = name; }

        public string Name { get; }
        public string Description => "test validation plugin";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public string ConformanceLevel { get; }
        public long PositionSeen { get; private set; } = -1;

        public PluginValidationResult Validate(Stream pdfStream)
        {
            PositionSeen = pdfStream.Position;
            return new PluginValidationResult { IsConformant = true };
        }
    }

    // ==================================================================
    //  The file-path readers now decrypt and reset the diagnostic
    // ==================================================================

    [Fact]
    public void GetMetadata_FilePathOverload_ClearsThePreviousDocumentsExtractionError()
    {
        // LastExtractionError's own contract is "reset at the start of every call, on every path,
        // so a value never survives from an earlier document". The path overloads opened the file
        // directly and reset nothing, so a caller who had just read an encrypted document
        // rejected a perfectly good one.
        SetExtractionErrorFromAnEncryptedRead();
        Assert.NotNull(PdfOperations.LastExtractionError);

        using var temp = new TempFile(MinimalPdfBuilder.CreatePdfWithMetadata("Clean Document"));
        var metadata = PdfOperations.GetMetadata(temp.Path);

        Assert.Equal("Clean Document", metadata.Title);
        Assert.Null(PdfOperations.LastExtractionError);
    }

    [Fact]
    public void GetPageCount_FilePathOverload_ClearsThePreviousDocumentsExtractionError()
    {
        SetExtractionErrorFromAnEncryptedRead();
        Assert.NotNull(PdfOperations.LastExtractionError);

        using var temp = new TempFile(MinimalPdfBuilder.CreateMultiPagePdf(3));
        Assert.Equal(3, PdfOperations.GetPageCount(temp.Path));
        Assert.Null(PdfOperations.LastExtractionError);
    }

    [Fact]
    public void GetVersion_FilePathOverload_ClearsThePreviousDocumentsExtractionError()
    {
        SetExtractionErrorFromAnEncryptedRead();
        Assert.NotNull(PdfOperations.LastExtractionError);

        using var temp = new TempFile(MinimalPdfBuilder.CreateMinimalPdf());
        Assert.False(string.IsNullOrEmpty(PdfOperations.GetVersion(temp.Path)));
        Assert.Null(PdfOperations.LastExtractionError);
    }

    /// <summary>
    /// Leaves <see cref="PdfOperations.LastExtractionError"/> set, by reading an encrypted
    /// document with no decryptor registered.
    /// </summary>
    private static void SetExtractionErrorFromAnEncryptedRead()
    {
        byte[] encrypted = EncryptedPdf(MinimalPdfBuilder.CreateMinimalPdf());
        using var _ = new NoDecryptorRegistered();
        using var input = new MemoryStream(encrypted);
        PdfOperations.GetMetadata(input);
    }

    /// <summary>
    /// Removes the process-wide decryptor for the duration of a test, and puts it back.
    /// </summary>
    /// <remarks>
    /// <c>PdfDecryptorHolder.Current</c> is a process-wide singleton that any test class calling
    /// <c>PdfEncryptionOperations.Register()</c> installs, so "no decryptor is registered" — the
    /// state in which a read reports through <see cref="PdfOperations.LastExtractionError"/>
    /// rather than throwing — is not a property of this test unless it is asserted here. Without
    /// this, these tests passed alone and failed in the full run, which is the shared-state trap
    /// CLAUDE.md warns about. This class is in the serialised plugin-host collection, so nothing
    /// else runs while the holder is nulled.
    /// </remarks>
    private sealed class NoDecryptorRegistered : IDisposable
    {
        private readonly Base.Encryption.IPdfDecryptor _saved;
        public NoDecryptorRegistered()
        {
            _saved = Base.Encryption.PdfDecryptorHolder.Current;
            Base.Encryption.PdfDecryptorHolder.Current = null;
        }
        public void Dispose() => Base.Encryption.PdfDecryptorHolder.Current = _saved;
    }

    private const string UserPassword = "s3cret";

    /// <summary>
    /// Encrypts <paramref name="plain"/> with <see cref="UserPassword"/>.
    /// </summary>
    /// <remarks>
    /// A real encryption round trip rather than a hand-built /Encrypt dictionary, so the fixture
    /// behaves the same whether or not another test class has registered a decryptor: reading it
    /// back with no password fails password validation either way. A fixture that is only
    /// unreadable when nothing is registered makes the assertion depend on test order — which is
    /// exactly how these tests passed alone and failed in the full run.
    /// </remarks>
    private static byte[] EncryptedPdf(byte[] plain)
    {
        global::Tetitulz.Pdf.Encryption.PdfEncryptionOperations.Register();

        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        global::Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(input, output, new Base.Encryption.EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm = Base.Encryption.EncryptionAlgorithm.AES128,
            Permissions = Base.Encryption.PdfPermissions.All,
        });
        return output.ToArray();
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile(byte[] content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName() + ".pdf");
            File.WriteAllBytes(Path, content);
        }
        public void Dispose() { try { File.Delete(Path); } catch { /* best effort */ } }
    }

    // ==================================================================
    //  Password overloads reach the facade
    // ==================================================================

    [Fact]
    public void GetFormFields_WithThePassword_ReadsTheRealFieldNames()
    {
        // PdfFormReader.GetFields(Stream, string) is the documented read-only encrypted-input
        // path and was unreachable from the facade: a consumer using only PdfOperations had to
        // drop down to the operations class. A /T field name is a PDF *string*, so reading it
        // back is evidence of decryption rather than of enumeration.
        byte[] encrypted = EncryptedPdf(
            Tests.Operations.FormPdfFixtures.TextField(fieldName: "invoice.total"));

        using var withPassword = new MemoryStream(encrypted);
        var fields = PdfOperations.GetFormFields(withPassword, UserPassword);

        Assert.Contains(fields, f => f.FullyQualifiedName == "invoice.total");
        Assert.Null(PdfOperations.LastExtractionError);
    }

    [Fact]
    public void GetFormFields_EncryptedDocumentWithoutThePassword_RefusesAndSaysWhy()
    {
        // The no-password form opened the still-encrypted document: the structure resolves on
        // name keys, but every /T field name is ciphertext, so the caller got a list of mojibake
        // names with no exception and no diagnostic — and then matched nothing in FillForm. The
        // facade then said why it found nothing; the reader now refuses, and the reason stays.
        byte[] encrypted = EncryptedPdf(
            Tests.Operations.FormPdfFixtures.TextField(fieldName: "invoice.total"));

        using var noDecryptor = new NoDecryptorRegistered();
        using var input = new MemoryStream(encrypted);
        Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(() => PdfOperations.GetFormFields(input));

        Assert.NotNull(PdfOperations.LastExtractionError);
        Assert.Contains("encrypted", PdfOperations.LastExtractionError,
                        StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindImages_WithThePassword_ReadsTheImagesAndWithoutItSaysSo()
    {
        byte[] encrypted = EncryptedPdf(MinimalPdfBuilder.CreatePdfWithImage(40, 30));

        using var withPassword = new MemoryStream(encrypted);
        var images = PdfOperations.FindImages(withPassword, UserPassword);
        Assert.NotEmpty(images);
        Assert.Null(PdfOperations.LastExtractionError);

        using var noDecryptor = new NoDecryptorRegistered();
        using var withoutPassword = new MemoryStream(encrypted);
        PdfOperations.FindImages(withoutPassword);
        Assert.NotNull(PdfOperations.LastExtractionError);
        Assert.Contains("encrypted", PdfOperations.LastExtractionError,
                        StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================
    //  RotateAllPages is one edit, not one per page
    // ==================================================================

    [Fact]
    public void RotateAllPages_WritesASingleIncrementalRevision()
    {
        // The loop called RotatePage once per page, each call re-serialising the whole document,
        // so the output carried one revision per page and every intermediate half-rotated state
        // was recoverable through ExtractRevision — surprising for what the caller thinks of as
        // one edit.
        const int pageCount = 5;
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(pageCount));
        using var output = new MemoryStream();

        PdfOperations.RotateAllPages(input, output, 90);

        byte[] result = output.ToArray();
        using var revisionStream = new MemoryStream(result);
        var revisions = PdfOperations.GetRevisions(revisionStream);
        Assert.Equal(2, revisions.Count);   // the original, plus this one edit

        using var doc = PdfDocument.Open(new MemoryStream(result));
        Assert.Equal(pageCount, doc.Pages.Count);
        foreach (var page in doc.Pages)
            Assert.Equal(90, (int)((doc.ResolveObject(page.Dictionary[PdfName.Rotate]) as PdfNumber).LongValue));
    }

    [Fact]
    public void RotateAllPages_RejectsARotationThatIsNotAMultipleOf90()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => PdfOperations.RotateAllPages(input, output, 45));
    }
}
