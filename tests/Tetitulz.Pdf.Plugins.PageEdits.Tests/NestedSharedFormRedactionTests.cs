// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Redaction and a form nested inside a form that several pages draw — the shape GAPS §6 recorded
/// as <b>OPEN, not yet reproduced</b>, with the instruction to write the failing fixture first and
/// change nothing if it passes.
///
/// <para><b>The concern was that <c>SubstituteClone</c> writes into whichever <c>/Resources</c> the
/// <c>Do</c> resolved against, with no check that the page owns it.</b> For a nested form that
/// dictionary belongs to the <i>parent</i> form, which is shared with every page drawing the
/// parent — so a page the caller never named could start drawing the redacted inner form, which
/// would contradict the type's own promise that cloning confines the edit to the named page.</para>
///
/// <para>These are the fixtures that decide it. They assert what an <b>unnamed page</b> still
/// shows, which is the only thing the concern is about — a test that looked at the redacted page
/// alone would pass whether or not the bug is real.</para>
/// </summary>
public class NestedSharedFormRedactionTests
{
    private const string SecretWord = "SECRETNESTED";
    private const string PublicWord = "PUBLICOUTER";

    /// <summary>
    /// Page 0 and page 1 both draw parent form <c>P</c>, which draws inner form <c>I</c> carrying
    /// the secret. Redacting on page 0 must leave <b>page 1 unchanged</b>.
    ///
    /// <para>This is the reproduction the register asked for. If it fails, the reading is right and
    /// <c>SubstituteClone</c> needs the resource-copy treatment <c>PageContentRewriter</c> uses.</para>
    /// </summary>
    [Fact]
    public void RedactingANestedFormSharedByTwoPages_LeavesTheUnnamedPageIntact()
    {
        byte[] pdf = BuildNestedSharedFormPdf();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
        });

        output.Position = 0;
        var pages = PdfOperations.ExtractStructuredText(output);
        Assert.Equal(2, pages.Count);

        // The NAMED page lost it -- without this the test says nothing about whether redaction ran
        // at all, and "page 1 still has the secret" would be satisfied by doing nothing.
        Assert.DoesNotContain(SecretWord, TextOf(pages[0]));

        string pageTwo = TextOf(pages[1]);
        Assert.Contains(SecretWord, pageTwo);
        Assert.Contains(PublicWord, pageTwo);
    }

    /// <summary>
    /// The control the case above needs: without it, an implementation that dropped page 1's
    /// content entirely — or never drew the forms at all — would satisfy "page 1 still has the
    /// secret" vacuously by having nothing to compare against.
    /// </summary>
    [Fact]
    public void TheUnnamedPageRendersTheSameTextItDidBeforeRedaction()
    {
        byte[] pdf = BuildNestedSharedFormPdf();

        using var before = new MemoryStream(pdf);
        string originalPageTwo = TextOf(PdfOperations.ExtractStructuredText(before)[1]);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
        });

        output.Position = 0;
        string afterPageTwo = TextOf(PdfOperations.ExtractStructuredText(output)[1]);

        Assert.Equal(originalPageTwo, afterPageTwo);
        Assert.Contains(SecretWord, originalPageTwo);
    }

    /// <summary>
    /// The other half of what redaction promises here, and the reason the shared case is a
    /// documented reduction rather than a failure: because the parent is drawn twice, the inner
    /// form's original object must survive, so the secret DOES remain in the file. The caller is
    /// told through the shared-form report rather than left to assume otherwise.
    /// </summary>
    [Fact]
    public void TheSecretSurvivesInTheFile_BecauseAnUnnamedPageStillDrawsIt()
    {
        byte[] pdf = BuildNestedSharedFormPdf();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
        });

        // Not a defect: page 1 legitimately draws it, so removing it would corrupt a page the
        // caller never named. Redaction removes content from the pages you name, not from the
        // document, whenever a named page shares a form with an unnamed one.
        Assert.True(ContainsAscii(output.ToArray(), SecretWord),
            "the shared inner form's content must survive for the page still drawing it");
    }

    /// <summary>
    /// The case that decides the register's actual claim, and it is the one that cannot happen.
    ///
    /// <para>The concern was about the <b>repoint</b> branch — <c>SubstituteClone</c> overwriting
    /// the existing name in a parent form's <c>/Resources</c>, which other pages share. But that
    /// branch is guarded by <c>FormInvocationCounter</c>, whose uniqueness test is
    /// <b>transitive</b>: an inner form counts as uniquely drawn only when its one invocation site
    /// is page content <i>or a form which is itself uniquely drawn</i>. So reaching the repoint
    /// branch on a nested form REQUIRES the whole chain up to the page to be drawn once — and a
    /// parent drawn once has a <c>/Resources</c> no other page can reach. <b>The premise and the
    /// branch cannot co-occur.</b></para>
    ///
    /// <para>This fixture is that safe shape: one page, one parent, one inner form. The secret is
    /// removed completely, which is what the repoint branch is for.</para>
    /// </summary>
    [Fact]
    public void ANestedFormWhoseWholeChainIsDrawnOnce_IsRedactedCompletely()
    {
        byte[] pdf = BuildNestedSharedFormPdf(secondPage: false);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
        });

        byte[] outBytes = output.ToArray();
        output.Position = 0;
        string text = PdfOperations.ExtractText(output);

        Assert.DoesNotContain(SecretWord, text);
        Assert.Contains(PublicWord, text);
        // The whole point of the repoint branch: the original object becomes unreachable and the
        // rewriter drops it, so the secret is in NO revision of the output.
        Assert.False(ContainsAscii(outBytes, SecretWord),
            "a uniquely drawn nested form's content must not survive anywhere in the output");
    }
    /// <summary>
    /// The leak is <b>reported</b>, not merely real. Asserted together with the surviving bytes,
    /// because the claim is a correlation: the warning must fire exactly when the data stays.
    /// </summary>
    /// <remarks>
    /// Before this, <c>FormCloneContext.SharedFormLeftIntact</c> recorded the forms and nothing
    /// carried them out - the one outcome where redaction does not remove the data was the one
    /// outcome it could not report, while a comment in the engine claimed otherwise. A caller
    /// could not tell this case from a complete removal.
    /// </remarks>
    [Fact]
    public void TheSharedFormLeftIntact_IsReportedAndItsContentIsStillInTheFile()
    {
        byte[] pdf = BuildNestedSharedFormPdf();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
        });

        string warning = PdfRedactionEngine.LastSharedFormWarning;

        Assert.False(string.IsNullOrEmpty(warning),
            "the surviving form must be reported, not left for the caller to assume");
        // The inner form is the one carrying the secret, so it is the one a caller must be told
        // about. Naming only the parent would be a report that misses the leak it is about.
        Assert.Contains("Fi", warning);

        // The correlation: the warning is true. Without this the test passes for an engine that
        // warns unconditionally.
        Assert.True(ContainsAscii(output.ToArray(), SecretWord),
            "the warning claims the content survived, so it must have");
    }

    /// <summary>
    /// <b>One form, named once — however many times it was drawn.</b> The record is kept per
    /// <i>invocation</i>, so a form drawn on two of the redacted pages is added twice; the report
    /// de-duplicates because a caller reading it wants the forms it must go and redact, not the
    /// draws. A doubled list also doubles the count at the front of the message, which is the part
    /// a caller is most likely to act on.
    /// </summary>
    [Fact]
    public void ASharedFormDrawnOnTwoRedactedPages_IsNamedOnceNotOncePerDraw()
    {
        byte[] pdf = BuildNestedSharedFormPdf();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
            new RedactionRegion { PageIndex = 1, X = 95, Y = 652, Width = 160, Height = 22 },
        });

        string warning = PdfRedactionEngine.LastSharedFormWarning;
        Assert.False(string.IsNullOrEmpty(warning), "the surviving form must still be reported");

        // Two forms are genuinely left intact here - the outer one and the nested one it draws -
        // and each is invoked from both redacted pages. So the de-duplicated count is TWO, and
        // the un-deduplicated one would be four.
        Assert.StartsWith("2 Form XObject(s)", warning, StringComparison.Ordinal);
        // ...and each name appears once in the list, not once per invocation.
        Assert.Equal(1, Occurrences(warning, "Fi"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>
    /// The acceptance control. A refusal-shaped diagnostic that fires on the conformant case is
    /// one a caller learns to ignore, so the uniquely-drawn shape must report <b>nothing</b> - and
    /// its secret must genuinely be gone, or "no warning" would be correct for the wrong reason.
    /// </summary>
    [Fact]
    public void AUniquelyDrawnForm_ReportsNothingAndItsContentIsGone()
    {
        byte[] pdf = BuildNestedSharedFormPdf(secondPage: false);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfRedactor.RedactRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
        });

        Assert.Null(PdfRedactionEngine.LastSharedFormWarning);
        Assert.False(ContainsAscii(output.ToArray(), SecretWord),
            "nothing was reported, so nothing may have survived");
    }

    /// <summary>
    /// The slot is cleared at the <b>start</b> of a redaction, so a warning can never outlive the
    /// call that produced it. Both redactions run here in one test, in this order, because that is
    /// the only way to assert the reset - a test that runs the clean case alone passes whether or
    /// not the slot is ever cleared.
    /// </summary>
    [Fact]
    public void AWarningDoesNotSurviveIntoTheNextRedaction()
    {
        using (var leakIn = new MemoryStream(BuildNestedSharedFormPdf()))
        using (var leakOut = new MemoryStream())
        {
            PdfRedactor.RedactRegions(leakIn, leakOut, new[]
            {
                new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
            });
        }

        Assert.False(string.IsNullOrEmpty(PdfRedactionEngine.LastSharedFormWarning),
            "the fixture must actually leak, or the reset below proves nothing");

        using (var cleanIn = new MemoryStream(BuildNestedSharedFormPdf(secondPage: false)))
        using (var cleanOut = new MemoryStream())
        {
            PdfRedactor.RedactRegions(cleanIn, cleanOut, new[]
            {
                new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
            });
        }

        Assert.Null(PdfRedactionEngine.LastSharedFormWarning);
    }

    /// <summary>
    /// The early return for a call naming no regions clears the slot too. That path leaves
    /// <c>Redact</c> before the walk, so a reset placed beside the report rather than at the top
    /// of the method would miss it and hand the caller the previous call's warning.
    /// </summary>
    [Fact]
    public void ARedactionWithNoRegionsClearsThePreviousWarning()
    {
        using (var leakIn = new MemoryStream(BuildNestedSharedFormPdf()))
        using (var leakOut = new MemoryStream())
        {
            PdfRedactor.RedactRegions(leakIn, leakOut, new[]
            {
                new RedactionRegion { PageIndex = 0, X = 95, Y = 652, Width = 160, Height = 22 },
            });
        }

        Assert.False(string.IsNullOrEmpty(PdfRedactionEngine.LastSharedFormWarning));

        using var input = new MemoryStream(BuildNestedSharedFormPdf());
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(input, output, Array.Empty<PdfRedactionArea>());

        Assert.Null(PdfRedactionEngine.LastSharedFormWarning);
    }

    // ------------------------------------------------------------------

    private static string TextOf(Base.Operations.Extraction.PdfPageTextLayout page)
    {
        var sb = new StringBuilder();
        foreach (var line in page.Lines)
            foreach (var word in line.Words)
                sb.Append(word.Text).Append(' ');
        return sb.ToString();
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        byte[] n = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i + n.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < n.Length; j++)
                if (haystack[i + j] != n[j]) { hit = false; break; }
            if (hit) return true;
        }
        return false;
    }

    /// <summary>
    /// Two pages, each drawing parent form <c>P</c> (object 6). <c>P</c> draws inner form
    /// <c>I</c> (object 7) through <b>its own</b> <c>/Resources</c> — which is the dictionary the
    /// concern is about, since it is reached from both pages.
    /// </summary>
    private static byte[] BuildNestedSharedFormPdf(bool secondPage = true)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        const string pageContent = "q 1 0 0 1 0 0 cm /Fp Do Q";
        // The parent draws the public word itself and delegates the secret to the inner form, so a
        // test can tell "the parent survived" from "the inner form survived".
        string parentContent =
            $"BT /F1 12 Tf 1 0 0 1 100 700 Tm ({PublicWord}) Tj ET q /Fi Do Q";
        string innerContent =
            $"BT /F1 12 Tf 1 0 0 1 100 660 Tm ({SecretWord}) Tj ET";

        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(secondPage
            ? "2 0 obj\n<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>\nendobj\n"
            : "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Contents 4 0 R /Resources << /XObject << /Fp 6 0 R >> /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj($"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> /XObject << /Fi 7 0 R >> >> " +
            $"/Length {parentContent.Length} >>\nstream\n{parentContent}\nendstream\nendobj\n");
        Obj($"7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> " +
            $"/Length {innerContent.Length} >>\nstream\n{innerContent}\nendstream\nendobj\n");
        if (secondPage)
        {
            Obj("8 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                "/Contents 9 0 R /Resources << /XObject << /Fp 6 0 R >> /Font << /F1 5 0 R >> >> >>\nendobj\n");
            Obj($"9 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");
        }

        int total = offsets.Count + 1;
        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", total);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", total);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
