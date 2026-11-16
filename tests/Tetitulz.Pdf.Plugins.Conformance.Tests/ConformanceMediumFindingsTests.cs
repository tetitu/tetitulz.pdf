// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The MEDIUM assembly-scan findings for the PDF/A converter and validator, the output-intent
/// writer, the PDF/UA checker and the two plugin adapters.
/// </summary>
public class ConformanceMediumFindingsTests
{
    // ------------------------------------------------------------------
    //  OutputIntentWriter — an indirect /OutputIntents array
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertToPdfA1b_ExistingIndirectOutputIntents_AreKeptAlongsideTheNewOne()
    {
        // `/OutputIntents 9 0 R` is legal, and PdfDictionary's indexer does not resolve it — so the
        // preservation loop was skipped and a press file's GTS_PDFX intent, printing condition and
        // embedded profile were all dropped by a conversion that only meant to add one entry.
        using var input = new MemoryStream(PdfWithIndirectOutputIntent());
        using var output = new MemoryStream();

        PdfAConverter.ConvertToPdfA1b(input, output, new PdfAConversionOptions
        {
            IccProfile = IccProfileFixtures.Rgb(),
        });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var intents = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OutputIntents]) as PdfArray;
        Assert.NotNull(intents);
        Assert.Equal(2, intents.Count);

        var subtypes = new List<string>();
        for (int i = 0; i < intents.Count; i++)
            subtypes.Add((doc.ResolveObject(
                (doc.ResolveObject(intents[i]) as PdfDictionary)[PdfName.S]) as PdfName)?.Value);

        Assert.Contains("GTS_PDFX", subtypes);
        Assert.Contains("GTS_PDFA1", subtypes);
    }

    // ------------------------------------------------------------------
    //  PdfAConverter — the XMP date, and the two input conditions
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertToPdfA1b_UnderABuddhistCalendarCulture_WritesAGregorianModifyDate()
    {
        // A custom format string is resolved against CurrentCulture's CALENDAR. Under th-TH the
        // packet carried <xmp:ModifyDate>25xx-…</>, which the published profiles rejects — in the very file the
        // converter had just stamped pdfaid:part=1 into. Invisible on an en-US machine and on CI.
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");

            using var input = new MemoryStream(PlainPdf());
            using var output = new MemoryStream();
            PdfAConverter.ConvertToPdfA1b(input, output, new PdfAConversionOptions
            {
                IccProfile = IccProfileFixtures.Rgb(),
            });

            string modifyDate = XmpValue(output.ToArray(), "xmp:ModifyDate");
            Assert.NotNull(modifyDate);
            Assert.Equal(DateTime.UtcNow.Year, int.Parse(modifyDate.Substring(0, 4), CultureInfo.InvariantCulture));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", modifyDate);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ConvertToPdfA1b_EncryptedInput_ReportsA01AsABlockerRatherThanThrowing()
    {
        // PdfSanitizer throws PdfEncryptedDocumentException — out of a type the caller never
        // invoked — while the result object built to carry blockers was never returned. A01 forbids
        // encryption, so this is a conformance blocker, which is what the result is for.
        using var input = new MemoryStream(EncryptedPdf());
        using var output = new MemoryStream();

        var result = PdfAConverter.ConvertToPdfA1b(input, output, new PdfAConversionOptions());

        Assert.False(result.IsConformant);
        Assert.False(result.DeclaredPdfAIdentifier);
        Assert.Contains(result.Blockers, b => b.Contains("A01", StringComparison.Ordinal));
        Assert.Contains(result.Blockers, b => b.Contains("encrypted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConvertToPdfA1b_SignedInput_RefusesUnlessInvalidationIsAllowed()
    {
        using var input = new MemoryStream(SignedPdf());
        using var output = new MemoryStream();

        var refused = PdfAConverter.ConvertToPdfA1b(input, output, new PdfAConversionOptions());

        Assert.False(refused.IsConformant);
        Assert.Contains(refused.Blockers, b => b.Contains("signature", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, output.Length);

        // …and converts, with the invalidation reported, when the caller says so.
        using var input2 = new MemoryStream(SignedPdf());
        using var output2 = new MemoryStream();
        var allowed = PdfAConverter.ConvertToPdfA1b(input2, output2, new PdfAConversionOptions
        {
            AllowSignatureInvalidation = true,
            IccProfile = IccProfileFixtures.Rgb(),
        });

        Assert.Contains(allowed.Warnings, w => w.Contains("signature", StringComparison.OrdinalIgnoreCase));
        Assert.True(output2.Length > 0);
    }

    [Fact]
    public void ConvertToPdfA1b_UnsignedPlainInput_IsNotRefused()
    {
        // The control for both refusals above.
        using var input = new MemoryStream(PlainPdf());
        using var output = new MemoryStream();

        var result = PdfAConverter.ConvertToPdfA1b(input, output, new PdfAConversionOptions
        {
            IccProfile = IccProfileFixtures.Rgb(),
        });

        Assert.DoesNotContain(result.Blockers, b => b.Contains("signature", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Blockers, b => b.Contains("encrypted", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.Warnings);
    }

    // ------------------------------------------------------------------
    //  PdfAValidator — an undecodable XMP stream is an A07, not an exception
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_CorruptXmpStream_ReportsA07InsteadOfThrowing()
    {
        using var input = new MemoryStream(CorruptXmpPdf());

        var result = PdfAValidator.Validate(input);

        Assert.Contains(result.Violations, v => v.Code == "A07");
        Assert.NotNull(PdfAValidator.LastValidationError);
        Assert.Contains("XMP", PdfAValidator.LastValidationError, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    //  PdfUaValidationPlugin — UA99 is informational
    // ------------------------------------------------------------------

    [Fact]
    public void UaPlugin_Ua99_LandsInInformationalAndDoesNotFlipIsConformant()
    {
        // An encrypted document raises UA99 (its content streams are ciphertext, so UA50 cannot
        // run). Copying every entry into Violations made a legal, encrypted PDF/UA document report
        // IsConformant == false over a "violation" that says the audit was incomplete — while the
        // sibling X plugin, next door, put its X99 in Informational.
        var plugin = new PdfUaValidationPlugin();
        using var input = new MemoryStream(EncryptedTaggedPdf());

        PluginValidationResult result = plugin.Validate(input);

        Assert.Contains(result.Informational, v => v.Code == "UA99");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
    }

    // ------------------------------------------------------------------
    //  PdfUaChecker — UA50 and the /Stm skip
    // ------------------------------------------------------------------

    [Fact]
    public void UaChecker_MarkedContentReferenceWithStm_ReportsThatUa50DidNotCheckIt()
    {
        // /Stm names a content stream other than the page's, which UA50 does not decode. The count
        // existed for exactly this and was read by nothing, so a document whose references ALL use
        // /Stm produced a report identical to one where UA50 ran and found everything present.
        using var input = new MemoryStream(StmMarkedContentPdf());

        var result = PdfUaChecker.Validate(input);

        Assert.Contains(result.Violations,
            v => v.Code == "UA99" && v.Message.Contains("/Stm", StringComparison.Ordinal));
    }

    [Fact]
    public void UaChecker_EncryptedDocument_SaysUa50DidNotRun()
    {
        using var input = new MemoryStream(EncryptedTaggedPdf());

        var result = PdfUaChecker.Validate(input);

        Assert.Contains(result.Violations,
            v => v.Code == "UA99" && v.Message.Contains("UA50", StringComparison.Ordinal));
    }

    [Fact]
    public void UaChecker_PlainTaggedDocument_ReportsNoUa50Notice()
    {
        // The control: neither notice may fire on a document whose MCIDs UA50 really did check.
        using var input = new MemoryStream(TaggedPdf());

        var result = PdfUaChecker.Validate(input);

        Assert.DoesNotContain(result.Violations,
            v => v.Code == "UA99" && v.Message.Contains("UA50", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Violations,
            v => v.Code == "UA99" && v.Message.Contains("/Stm", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    //  AccessibilityPlugin — the two options it cannot honour
    // ------------------------------------------------------------------

    [Fact]
    public void AccessibilityPlugin_TagContentAndAltText_AreReportedAsNotAttempted()
    {
        var plugin = new AccessibilityPlugin();
        var reported = new List<string>();
        void Handler(string message) => reported.Add(message);

        DocumentEditDiagnostics.Clear();
        DocumentEditDiagnostics.OnEvent += Handler;
        try
        {
            using var input = new MemoryStream(PlainPdf());
            using var output = new MemoryStream();
            plugin.Fix(input, output, new PluginAccessibilityFixOptions
            {
                TagContent = true,
                AddAltText = true,
                SetLanguage = false,
            });
        }
        finally
        {
            DocumentEditDiagnostics.OnEvent -= Handler;
        }

        Assert.Contains(reported, m => m.Contains("TagContent", StringComparison.Ordinal));
        Assert.Contains(reported, m => m.Contains("AddAltText", StringComparison.Ordinal));
    }

    [Fact]
    public void AccessibilityPlugin_OptionsItCanHonour_AreNotReported()
    {
        // The control: a report on every call would make the channel worthless.
        var plugin = new AccessibilityPlugin();
        var reported = new List<string>();
        void Handler(string message) => reported.Add(message);

        DocumentEditDiagnostics.OnEvent += Handler;
        try
        {
            using var input = new MemoryStream(PlainPdf());
            using var output = new MemoryStream();
            plugin.Fix(input, output, new PluginAccessibilityFixOptions
            {
                TagContent = false,
                AddAltText = false,
                SetLanguage = true,
            });
        }
        finally
        {
            DocumentEditDiagnostics.OnEvent -= Handler;
        }

        Assert.DoesNotContain(reported, m => m.Contains("TagContent", StringComparison.Ordinal));
        Assert.DoesNotContain(reported, m => m.Contains("AddAltText", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static string XmpValue(byte[] pdf, string element)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int open = text.IndexOf("<" + element + ">", StringComparison.Ordinal);
        if (open < 0) return null;
        open += element.Length + 2;
        int close = text.IndexOf("</" + element + ">", open, StringComparison.Ordinal);
        return close < 0 ? null : text.Substring(open, close - open);
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (hi) Tj ET";
    private const string TaggedContent = "/P <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (hi) Tj ET EMC";

    private static byte[] PlainPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
    });

    private static byte[] PdfWithIndirectOutputIntent() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /OutputIntents 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "[6 0 R]",
        "<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (CGATS TR 001) >>",
    });

    private static byte[] EncryptedPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Filter /Standard /V 1 /R 2 /O <0102030405060708090A0B0C0D0E0F10" +
        "1112131415161718191A1B1C1D1E1F20> /U <0102030405060708090A0B0C0D0E0F10" +
        "1112131415161718191A1B1C1D1E1F20> /P -1 >>",
    }, trailerExtras: "/Encrypt 5 0 R ");

    // Encrypted AND tagged: the structure tree names an MCID, so UA50 has something to check and
    // must say that it could not.
    private static byte[] EncryptedTaggedPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {TaggedContent.Length} >>\nstream\n{TaggedContent}\nendstream",
        "<< /Type /StructTreeRoot /K 6 0 R >>",
        "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K 0 >>",
        "<< /Filter /Standard /V 1 /R 2 /O <0102030405060708090A0B0C0D0E0F10" +
        "1112131415161718191A1B1C1D1E1F20> /U <0102030405060708090A0B0C0D0E0F10" +
        "1112131415161718191A1B1C1D1E1F20> /P -1 >>",
    }, trailerExtras: "/Encrypt 7 0 R ");

    private static byte[] TaggedPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> /Lang (en) >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {TaggedContent.Length} >>\nstream\n{TaggedContent}\nendstream",
        "<< /Type /StructTreeRoot /K 6 0 R >>",
        "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K 0 >>",
    });

    // The structure element points at its content through an /MCR carrying /Stm.
    private static byte[] StmMarkedContentPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> /Lang (en) >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << /XObject << /Fm0 7 0 R >> >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /StructTreeRoot /K 6 0 R >>",
        "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R " +
        "/K << /Type /MCR /Pg 3 0 R /Stm 7 0 R /MCID 0 >> >>",
        "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 0 >>\nstream\n\nendstream",
    });

    private static byte[] SignedPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << >> /Annots [5 0 R] >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /V 6 0 R /Rect [0 0 10 10] >>",
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached " +
        "/ByteRange [0 100 200 300] /Contents <00> >>",
    });

    // /Metadata points at a FlateDecode stream whose payload is not deflate data at all.
    private static byte[] CorruptXmpPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
        "<< /Type /Metadata /Subtype /XML /Filter /FlateDecode /Length 12 >>\n" +
        "stream\nNOT-DEFLATE!\nendstream",
    });

    private static byte[] Assemble(List<string> bodies, string trailerExtras = "")
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R {trailerExtras}>>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
