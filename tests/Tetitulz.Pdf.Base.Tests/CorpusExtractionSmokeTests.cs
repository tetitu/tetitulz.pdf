// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Extracts text from every page of every PDF in the developer-local corpus and asserts the
/// extraction counterpart of <see cref="CorpusRenderSmokeTests"/>'s rule: a page whose content
/// stream contains <b>text-showing operators</b> must not extract as empty.
/// </summary>
/// <remarks>
/// <para><b>Why this shape and not "extraction returned a string".</b> An empty string is a
/// perfectly valid extraction result — plenty of real pages carry no text at all — so asserting
/// non-null, or non-empty over a whole document, passes with the extractor deleted. Correlating the
/// output against the <c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c> operators the page actually contains is
/// what separates "this page legitimately has no text" from "we dropped its text", which is the
/// only regression class a corpus smoke test can honestly catch.</para>
/// <para><b>What it cannot catch, stated so a green run is not over-read.</b> It cannot see whether
/// the text is in the <i>right order</i>, whether bidi resolved correctly, or whether the glyphs
/// map to the right characters — all three need reference data this repository does not have. The
/// Arabic-shaping defect recorded in CLAUDE.md is precisely the case that passes here: extraction
/// maps a presentation form back to its base letter just as happily as a correctly shaped one. This
/// finds dropped text, nothing more.</para>
/// <para><b>The operator count is page-stream-only, and that is deliberate.</b> A page whose text
/// lives inside a Form XObject shows <c>Do</c> in its own stream and no text operator, so it is not
/// asserted on. Measured against the 20-document corpus this was written on: 122 pages examined,
/// <b>78</b> counted as showing text while <b>87</b> extracted non-empty — the nine-page gap is
/// exactly that case. Following <c>Do</c> would widen the assertion, but it would also re-implement
/// resource resolution in a test; under-counting keeps the instrument conservative, so a failure
/// here is always a real dropped-text finding rather than an artefact of the scan.</para>
/// <para>Skips <b>visibly</b> without a corpus, via <see cref="CorpusFactAttribute"/> — never an
/// early <c>return</c>, which reports Passed for a test that executed nothing.</para>
/// </remarks>
public sealed class CorpusExtractionSmokeTests
{
    private readonly ITestOutputHelper _out;

    public CorpusExtractionSmokeTests(ITestOutputHelper output) => _out = output;

    /// <summary>Pages examined per document. Bounds runtime on book-length inputs.</summary>
    private const int MaxPagesPerDocument = 10;

    /// <summary>§9.4.3 — the four operators that actually show text.</summary>
    private static readonly HashSet<string> TextShowingOperators =
        new(StringComparer.Ordinal) { "Tj", "TJ", "'", "\"" };

    [CorpusFact]
    public void EveryCorpusPage_ExtractsWithoutThrowing_AndYieldsTextWhenItShowsText()
    {
        IReadOnlyList<string> pdfs = TestCorpus.Pdfs();
        Assert.NotEmpty(pdfs);

        var silent = new List<string>();
        int pagesExamined = 0, pagesWithText = 0, pagesShowingText = 0;

        foreach (string path in pdfs)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException) { continue; }

            using var ms = new MemoryStream(bytes, writable: false);
            PdfDocument doc;
            try { doc = PdfDocument.Open(ms); }
            catch (Exception ex)
            {
                // Opening is the render smoke test's business; a file this one cannot open is not
                // an extraction finding. Recorded so the count is honest.
                _out.WriteLine($"skipped (cannot open): {Path.GetFileName(path)} — {ex.Message}");
                continue;
            }

            using (doc)
            {
                if (doc.IsEncrypted) continue;

                int pageCount = Math.Min(doc.Pages.Count, MaxPagesPerDocument);
                for (int i = 0; i < pageCount; i++)
                {
                    pagesExamined++;

                    int showOps;
                    try
                    {
                        showOps = CorpusRenderSmokeTests.CountOperators(
                            doc.Pages[i].GetContentStreamData(), TextShowingOperators);
                    }
                    catch (Exception ex)
                    {
                        _out.WriteLine(
                            $"skipped (content unreadable): {Path.GetFileName(path)} p{i + 1} — {ex.Message}");
                        continue;
                    }

                    string text;
                    using (var extractStream = new MemoryStream(bytes, writable: false))
                    {
                        // Extraction must never escape: a malformed page degrades, it does not throw.
                        text = PdfOperations.ExtractText(extractStream, i + 1);
                    }

                    if (!string.IsNullOrWhiteSpace(text)) pagesWithText++;
                    if (showOps == 0) continue;

                    pagesShowingText++;
                    if (string.IsNullOrWhiteSpace(text))
                        silent.Add($"{Path.GetFileName(path)} p{i + 1} ({showOps} text-showing operators)");
                }
            }
        }

        _out.WriteLine(
            $"{pdfs.Count} documents, {pagesExamined} pages examined, " +
            $"{pagesShowingText} showing text, {pagesWithText} extracted non-empty.");

        Assert.True(silent.Count == 0,
            "These pages contain text-showing operators but extracted nothing, which means their " +
            "text was dropped:\n  " + string.Join("\n  ", silent));

        // A floor for the test itself: a corpus in which no page shows text would satisfy the
        // assertion above vacuously, and so would a CountOperators that had stopped counting.
        Assert.True(pagesShowingText > 0,
            $"No page in the {pdfs.Count}-document corpus was seen to show text, so the assertion " +
            "above proved nothing. Either the corpus has no text-bearing PDF, or the operator scan " +
            "is broken.");
    }

    /// <summary>
    /// Guards the guard: the operator scan this test depends on must actually distinguish a page
    /// that shows text from one that does not. Runs without a corpus, on purpose — a corpus-only
    /// test cannot protect its own instrument on a clean checkout.
    /// </summary>
    [Fact]
    public void TheTextShowingOperatorScan_DistinguishesShowingFromNotShowing()
    {
        Assert.Equal(1, Count("BT /F1 12 Tf 72 700 Td (hello) Tj ET"));
        Assert.Equal(1, Count("BT /F1 12 Tf [(a) -200 (b)] TJ ET"));
        Assert.Equal(2, Count("BT (x) ' (y) ' ET"));

        // Painting without showing text.
        Assert.Equal(0, Count("0 g 100 100 400 400 re f"));
        Assert.Equal(0, Count(""));

        // The three traps the shared walk exists to handle: an operator name inside a literal
        // string, inside a hex string, and as a name object, are all operands rather than operators.
        Assert.Equal(0, Count("BT /F1 12 Tf (Tj TJ) Sw ET"));
        Assert.Equal(0, Count("<546a> Sw"));
        Assert.Equal(0, Count("/Tj Sw"));

        static int Count(string content) =>
            CorpusRenderSmokeTests.CountOperators(
                Encoding.ASCII.GetBytes(content), TextShowingOperators);
    }
}
