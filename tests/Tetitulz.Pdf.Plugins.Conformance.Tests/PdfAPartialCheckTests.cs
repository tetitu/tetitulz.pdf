// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// A PDF/A check that did not run in full is an A99 on <see cref="PdfAValidationResult.Informational"/>
/// — and <see cref="PdfAConverter"/> does not claim conformance over one.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> A partial check reports no violation, so a document whose A06 font walk
/// ran out of budget validated exactly like a clean one, and the converter stamped
/// <c>pdfaid:part</c> on it with its fonts never examined. The validator did notice — it wrote
/// <see cref="PdfAValidator.LastValidationError"/> — but the converter never read it, the result had
/// nowhere to carry it, and two of the page scans (A19, A20-A21) did not notice at all.
/// <c>PdfXConverter</c> has refused over X99 since it shipped; this is the same rule.</para>
///
/// <para><b>The fixtures are clean apart from being partial.</b> A partial check hidden behind a
/// real violation proves nothing, since the violation withholds the claim by itself. So the wide
/// dictionary here names a form whose font is <i>embedded</i>, and nothing else in the document is
/// wrong: every blocker the converter reports is an A99.</para>
/// </remarks>
public class PdfAPartialCheckTests
{
    /// <summary>One more resource entry than the validator's walks may examine.</summary>
    private const int PastTheBudget = 200_001;

    // ------------------------------------------------------------------
    //  The validator
    // ------------------------------------------------------------------

    [Fact]
    public void AnOversizedResourceDictionaryIsAnA99NotAViolationAndNotASilentPass()
    {
        var result = Validate(WidePdf(PastTheBudget));

        Assert.True(result.IsPartial);
        Assert.Contains(result.Informational, i => i.Code == "A99"
            && i.Message.Contains("A06 font-embedding check did not run in full", StringComparison.Ordinal));

        // Informational, not a violation: A99 says the check did not run, not that the document is
        // wrong — the font it did reach is embedded, so there is nothing to report against it.
        Assert.DoesNotContain(result.Violations, v => v.Code == "A99");
        Assert.True(result.IsConformant);
    }

    [Fact]
    public void AnOrdinaryDocumentIsNotPartial()
    {
        // The control: an A99 raised unconditionally would pass every test above and below.
        var result = Validate(WidePdf(entries: 3));

        Assert.Empty(result.Informational);
        Assert.False(result.IsPartial);
        Assert.True(result.IsConformant, string.Join(" | ", result.Violations));
    }

    [Fact]
    public void ATransparencyWalkCutShortNamesTheTransparencyCheckNotA06()
    {
        // Both resource walks spend the same budget on the same dictionary. The transparency walk's
        // report used to end "the A06 font-embedding check is partial" — about itself.
        var result = Validate(WidePdf(PastTheBudget));

        var transparency = Assert.Single(result.Informational,
            i => i.Message.Contains("A09-A14 transparency check did not run in full", StringComparison.Ordinal));
        Assert.DoesNotContain("A06", transparency.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPageScanTheOversizedDictionaryCutsShortSaysSo()
    {
        // The page scan the three content-level checks share has its own entry budget, and hits it
        // on the same dictionary. A19's and A20-A21's scans read their partial flag nowhere.
        var result = Validate(WidePdf(PastTheBudget));

        Assert.Contains(result.Informational,
            i => i.Message.StartsWith("Page 1: the A16-A18 colour-consistency check did not run in full", StringComparison.Ordinal));
        Assert.Contains(result.Informational,
            i => i.Message.StartsWith("Page 1: the A19 glyph-width check did not run in full", StringComparison.Ordinal));
        Assert.Contains(result.Informational,
            i => i.Message.StartsWith("Page 1: the A20-A21 ICC profile check did not run in full", StringComparison.Ordinal));
    }

    [Fact]
    public void ABudgetSpentOnOnePageIsReportedOnceNotAgainForEveryPageAfterIt()
    {
        // Page 2 names one font of its own. By the time the font walk reaches it the budget is gone,
        // so the walk stops at that page's first entry with the same message — one fact, one entry.
        var result = Validate(WidePdf(PastTheBudget, secondPageWithAFont: true));

        Assert.Single(result.Informational,
            i => i.Message.Contains("A06 font-embedding check did not run in full", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUndecodableIccProfileIsAnA99ForTheIccCheck()
    {
        var result = Validate(UndecodableIccProfilePdf());

        Assert.Contains(result.Informational, i => i.Code == "A99"
            && i.Message.StartsWith("Page 1: the A20-A21 ICC profile check did not run in full", StringComparison.Ordinal)
            && i.Message.Contains("could not be decoded", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePluginForwardsA99AsInformationalAndLeavesIsConformantAlone()
    {
        var plugin = new PdfAValidationPlugin(PdfAConformancePart.Part1);
        using var input = new MemoryStream(WidePdf(PastTheBudget));

        PluginValidationResult result = plugin.Validate(input);

        Assert.True(result.IsPartial);
        Assert.Contains(result.Informational, i => i.Code == "A99");
        Assert.DoesNotContain(result.Violations, v => v.Code == "A99");
        Assert.True(result.IsConformant);
    }

    // ------------------------------------------------------------------
    //  The converter
    // ------------------------------------------------------------------

    [Fact]
    public void APartialFontWalkWithholdsThePdfAIdentifier()
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA1b(
            new MemoryStream(WidePdf(PastTheBudget)), output,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.False(result.IsConformant);
        Assert.False(result.DeclaredPdfAIdentifier);
        Assert.Contains(result.Blockers,
            b => b.StartsWith("A99:", StringComparison.Ordinal)
                 && b.Contains("A06 font-embedding check did not run in full", StringComparison.Ordinal));

        // Every blocker is an A99: the fixture is otherwise clean, so it is the partial checks alone
        // that withheld the claim, not a violation standing beside them.
        Assert.All(result.Blockers, b => Assert.StartsWith("A99:", b, StringComparison.Ordinal));
    }

    [Fact]
    public void APartialRunOfACheckTheConverterSetsAside_IsNotABlocker()
    {
        // A page whose content will not decode. Five checks read content and each is partial:
        // A16-A18's colour scan, A30's inline-image LZW scan, A50-A53's content-stream rules,
        // A58-A59's inline images and A65's content operands. The converter does not judge A16-A18 before writing its output
        // intent, so their partial run is not a blocker. The others are: the converter changes none
        // of them, so it cannot claim a page it could not read is free of them. (Until A30 existed the colour scan was the only
        // content reader, and the converter declared.)
        byte[] pdf = UndecodablePageContentPdf();

        // The premise, asserted: the fixture reaches the five partial paths, and only those.
        var pre = Validate(pdf);
        Assert.Equal(5, pre.Informational.Count);
        Assert.Contains(pre.Informational, i => i.Message.StartsWith(
            "Page 1: the A16-A18 colour-consistency check did not run in full", StringComparison.Ordinal));
        Assert.Contains(pre.Informational, i => i.Message.StartsWith(
            "Page 1: the A30 inline-image LZW check did not run in full", StringComparison.Ordinal));
        Assert.Contains(pre.Informational, i => i.Message.StartsWith(
            "Page 1: the A50-A53 content-stream checks did not run in full", StringComparison.Ordinal));
        Assert.Contains(pre.Informational, i => i.Message.StartsWith(
            "Page 1: the A58-A59 XObject checks did not run in full", StringComparison.Ordinal));
        Assert.Contains(pre.Informational, i => i.Message.StartsWith(
            "Page 1: the A65 implementation-limit check did not run in full", StringComparison.Ordinal));

        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA1b(
            new MemoryStream(pdf), output,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.Equal(4, result.Blockers.Count);
        Assert.Contains(result.Blockers, b => b.Contains("A30 inline-image LZW check did not run in full", StringComparison.Ordinal));
        Assert.Contains(result.Blockers, b => b.Contains("A50-A53 content-stream checks did not run in full", StringComparison.Ordinal));
        Assert.Contains(result.Blockers, b => b.Contains("A58-A59 XObject checks did not run in full", StringComparison.Ordinal));
        Assert.Contains(result.Blockers, b => b.Contains("A65 implementation-limit check did not run in full", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Blockers, b => b.Contains("A16-A18", StringComparison.Ordinal));

        // The converter validates its SANITIZED copy, not this input — so the premise is pinned on
        // the output, which is that copy plus the identifier and the intent. Had the sanitizer
        // rewritten the page into something decodable, the partial scan would never have reached the
        // converter and this test would pass over a rule it never exercised.
        Assert.Contains(Validate(output.ToArray()).Informational,
            i => i.Message.StartsWith("Page 1: the A16-A18 colour-consistency check did not run in full", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static PdfAValidationResult Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, PdfAConformancePart.Part1);
    }

    /// <summary>Not a real profile — the converter embeds the bytes verbatim.</summary>
    private static byte[] FakeIccProfile() => IccProfileFixtures.Rgb();

    private const string PageContent = "/Fm0 Do";
    private const string FormContent = "BT /F1 12 Tf (x) Tj ET";

    /// <summary>
    /// The PDF/A-1b identifier, in the attribute form A07 reads — so the wide fixture is conformant
    /// in full, and a test can show an A99 leaves <c>IsConformant</c> alone.
    /// </summary>
    private const string Xmp =
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" " +
        "pdfaid:part=\"1\" pdfaid:conformance=\"B\"/></rdf:RDF></x:xmpmeta>";

    /// <summary>PDF/A-1's largest dictionary (ISO 19005-1 6.1.12, A65): the wide fixture is built of dictionaries no larger.</summary>
    private const int LargestDictionary = 4_095;

    /// <summary>
    /// One page whose resources name the same form <paramref name="entries"/> times: in its own
    /// <c>/XObject</c> dictionary when that fits PDF/A-1's dictionary limit, else through forms each
    /// naming it as often as the limit allows — the walks' budgets are spent per entry across
    /// dictionaries, so the document reaches them without breaking A65. The form draws text in a
    /// font whose program is <b>embedded</b> and the catalog carries the PDF/A-1b identifier, so the
    /// document is conformant — and past the budget, partial too.
    /// </summary>
    private static byte[] WidePdf(int entries, bool secondPageWithAFont = false)
    {
        string Names(int from, int count)
        {
            var names = new StringBuilder("<< ");
            for (int i = from; i < from + count; i++)
                names.Append("/X").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" 5 0 R ");
            return names.Append(">>").ToString();
        }

        int firstGroup = secondPageWithAFont ? 11 : 10;
        var groups = new List<string>();
        string xobjects;
        if (entries <= LargestDictionary)
            xobjects = Names(0, entries);
        else
        {
            var page = new StringBuilder("<< /Fm0 5 0 R ");
            for (int from = 0; from < entries; from += LargestDictionary)
            {
                int count = Math.Min(LargestDictionary, entries - from);
                page.Append("/G").Append(groups.Count.ToString(CultureInfo.InvariantCulture)).Append(' ')
                    .Append((firstGroup + groups.Count).ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
                groups.Add("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject " +
                           Names(from, count) + " >> /Length 0 >>\nstream\n\nendstream");
            }
            xobjects = page.Append(">>").ToString();
        }

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 9 0 R >>",
            secondPageWithAFont
                ? "<< /Type /Pages /Kids [3 0 R 10 0 R] /Count 2 >>"
                : "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /XObject " + xobjects + " >> >>",
            $"<< /Length {PageContent.Length} >>\nstream\n{PageContent}\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] " +
            "/Resources << /Font << /F1 6 0 R >> >> " +
            $"/Length {FormContent.Length} >>\nstream\n{FormContent}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FontDescriptor 7 0 R >>",
            "<< /Type /FontDescriptor /FontName /Helvetica /Flags 32 /FontFile3 8 0 R >>",
            "<< /Subtype /Type1C /Length 4 >>\nstream\nCFF!\nendstream",
            $"<< /Type /Metadata /Subtype /XML /Length {Xmp.Length} >>\nstream\n{Xmp}\nendstream",
        };
        if (secondPageWithAFont)
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                       "/Resources << /Font << /F2 6 0 R >> >> >>");
        bodies.AddRange(groups);
        return Assemble(bodies);
    }

    /// <summary>
    /// A page drawing in an <c>[/ICCBased]</c> space whose profile stream claims
    /// <c>/FlateDecode</c> over bytes that are not deflate — so the profile cannot be read.
    /// </summary>
    private static byte[] UndecodableIccProfilePdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
        "/Resources << /ColorSpace << /CS0 [/ICCBased 5 0 R] >> >> >>",
        "<< /Length 7 >>\nstream\n/CS0 cs\nendstream",
        "<< /N 3 /Filter /FlateDecode /Length 12 >>\nstream\nNOT-DEFLATE!\nendstream",
    });

    /// <summary>A page whose only content stream claims <c>/FlateDecode</c> over bytes that are not deflate.</summary>
    private static byte[] UndecodablePageContentPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        "<< /Filter /FlateDecode /Length 12 >>\nstream\nNOT-DEFLATE!\nendstream",
    });

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.4\n").Append("%\u00E2\u00E3\u00CF\u00D3\n");   // with ISO 19005 6.1.2's binary comment
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
