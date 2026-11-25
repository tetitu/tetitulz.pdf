// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Font resolution for text the standard-14 WinAnsi fonts cannot encode.
///
/// <para>These are <b>host-independent</b>: every case that needs a covering font injects a
/// synthetic one through <see cref="PdfExportOptions.FontProvider"/> and turns host substitution
/// off, so they assert the same thing on a developer's Windows box and on a bare Linux container.
/// The alternative — depending on whatever fonts the runner happens to have — is what made the
/// only pre-existing Hebrew test an <c>if (!HasHebrewFont()) return;</c> early exit that reported
/// <i>Passed</i> while executing nothing.</para>
/// </summary>
public class FontResolutionTests
{
    private const string Hebrew = "שלום";
    private const string Cjk = "漢字";

    private static Document DocWith(params string[] paragraphs)
    {
        var doc = new Document();
        var section = new Section();
        foreach (string text in paragraphs)
            section.Children.Add(new Paragraph { Runs = { new TextRun { Text = text } } });
        doc.Sections.Add(section);
        return doc;
    }

    private static PdfExportOptions Injected(string coveredCharacters, string family = null) =>
        new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build(coveredCharacters),
        };

    private static string Raw(byte[] pdf) => PdfContentProbe.Raw(pdf);

    // Content streams are Flate-compressed, so a drawn string is not in the raw bytes.
    private static string Drawn(byte[] pdf) => PdfContentProbe.DecodeStreams(pdf);

    // ------------------------------------------------------------------ the provider path

    [Fact]
    public void FontProvider_SuppliesTheFont_AndACompositeFontIsEmbedded()
    {
        byte[] pdf = DocWith(Hebrew).ToPdf(Injected(Hebrew));
        string raw = Raw(pdf);

        Assert.Contains("/Type0", raw);
        Assert.Contains("/Identity-H", raw);
        Assert.Contains("/FontFile2", raw);   // the program really reached the file
    }

    [Fact]
    public void FontProvider_IsAskedPerRun_WithThatRunsCodePoints()
    {
        var requests = new List<BuilderFontRequest>();
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = req => { requests.Add(req); return MinimalTrueType.Build(Hebrew + Cjk); },
        };

        DocWith(Hebrew, Cjk).ToPdf(options);

        Assert.NotEmpty(requests);
        // Every request carries only the code points of the run that triggered it — never the
        // document's union, so resolution does not demand one font cover the whole document.
        foreach (var req in requests)
        {
            var cps = req.RequiredCodePoints.ToHashSet();
            bool isHebrew = cps.All(c => c >= 0x0590 && c <= 0x05FF);
            bool isCjk = cps.All(c => c >= 0x4E00 && c <= 0x9FFF);
            Assert.True(isHebrew || isCjk,
                $"a request mixed scripts: {string.Join(",", cps.Select(c => $"U+{c:X4}"))}");
        }
    }

    [Fact]
    public void HebrewAndCjkInOneDocument_BothRender()
    {
        // The case one-font-for-the-whole-document breaks: no single synthetic font covers both,
        // so resolution must succeed per run with a different program each time.
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = req =>
            {
                var cps = req.RequiredCodePoints;
                bool hebrew = cps.All(c => c >= 0x0590 && c <= 0x05FF);
                return MinimalTrueType.Build(hebrew ? Hebrew : Cjk);
            },
        };

        byte[] pdf = DocWith(Hebrew, Cjk).ToPdf(options);
        string raw = Raw(pdf);

        // Two distinct composite fonts were embedded, one per script.
        int fontFiles = System.Text.RegularExpressions.Regex.Matches(raw, "/FontFile2").Count;
        Assert.True(fontFiles >= 2, $"expected a font per script, found {fontFiles}");
    }

    // ------------------------------------------------------------------ one embed per program (2:5)

    /// <summary>A Hebrew paragraph whose run names <paramref name="family"/>.</summary>
    private static Paragraph InFamily(string family, string text) =>
        new Paragraph { Runs = { new TextRun { Text = text, FontName = family } } };

    /// <summary>
    /// Two families that resolve to the same face embed it once (Builder-2:5), not once per family:
    /// <c>AddEmbeddedFont</c> does not dedupe, and each family would mint its own handle. The provider hands both families the
    /// same bytes, so the two programs are content-equal and share one embedded font.
    /// </summary>
    [Fact]
    public void TwoFamiliesResolvingToTheSameProgram_EmbedOneFontFile()
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(InFamily("FamilyA", Hebrew));
        section.Children.Add(InFamily("FamilyB", Hebrew));
        doc.Sections.Add(section);

        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build(Hebrew),   // the same bytes for every family
        };

        int fontFiles = System.Text.RegularExpressions.Regex.Matches(Raw(doc.ToPdf(options)), "/FontFile2").Count;
        Assert.Equal(1, fontFiles);
    }

    /// <summary>
    /// The control: two families that resolve to <i>different</i> faces still embed both — the dedup
    /// keys on the program's bytes, not on "any run that needs a composite font". A Hebrew run and a
    /// CJK run take different synthetic fonts, so a fix that merged on anything coarser would drop one.
    /// </summary>
    [Fact]
    public void TwoFamiliesResolvingToDifferentPrograms_EmbedTwoFontFiles()
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(InFamily("FamilyA", Hebrew));
        section.Children.Add(InFamily("FamilyB", Cjk));
        doc.Sections.Add(section);

        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = req => MinimalTrueType.Build(
                req.RequiredCodePoints.All(c => c >= 0x0590 && c <= 0x05FF) ? Hebrew : Cjk),
        };

        int fontFiles = System.Text.RegularExpressions.Regex.Matches(Raw(doc.ToPdf(options)), "/FontFile2").Count;
        Assert.Equal(2, fontFiles);
    }

    [Fact]
    public void FontProvider_IsNotAskedForPureAsciiText()
    {
        // The document-wide "is there any composite text at all?" fast path must survive the move
        // to per-run resolution, or every plain-Latin export starts scanning the host's fonts.
        int calls = 0;
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => { calls++; return null; },
        };

        DocWith("Plain ASCII only").ToPdf(options);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void FontProvider_ThatThrows_IsTreatedAsDeclining_AndReported()
    {
        // Collected through OnDiagnostic rather than read from LastError: one export reports
        // several distinct failures here — the provider throwing, then the run falling back —
        // and a single Last* slot keeps only the last of them. That is the whole reason this is
        // an event-carrying sink rather than one property.
        var seen = new List<BuilderDiagnostic>();
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => throw new InvalidOperationException("no font service"),
            OnDiagnostic = seen.Add,
        };

        // A provider is caller code; a throw from it must not take the export down. Both halves
        // of that are asserted: the document still renders its other text (not merely "bytes came
        // back" — a blank page is non-empty too), and a diagnostic names the cause.
        byte[] pdf = DocWith(Hebrew + " tail").ToPdf(options);

        Assert.Contains("tail", Drawn(pdf), StringComparison.Ordinal);

        var rejected = seen.SingleOrDefault(d => d.Kind == BuilderDiagnosticKind.FontFormatRejected);
        Assert.NotNull(rejected);
        Assert.Contains("no font service", rejected.Detail, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(rejected.Exception);
    }

    [Fact]
    public void FontProvider_ReturningAFontThatDoesNotCoverTheRun_IsRejectedAndReported()
    {
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build("abc"),   // Latin only — no Hebrew
        };

        DocWith(Hebrew).ToPdf(options);

        var d = BuilderDiagnostics.LastError;
        Assert.NotNull(d);
        Assert.Contains("U+05", d.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ honest failure

    [Fact]
    public void NoFontCoversTheText_RaisesADiagnosticNamingTheCodePoint()
    {
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = null,
        };

        DocWith(Hebrew).ToPdf(options);

        var d = BuilderDiagnostics.LastError;
        Assert.NotNull(d);
        Assert.Equal(BuilderDiagnosticKind.FontNotResolved, d.Kind);
        // The message must say which code point, not just "a font was missing" — silent, '????' on
        // the page would be indistinguishable from an empty document.
        Assert.Contains("U+05E9", d.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OnDiagnosticCallback_ReceivesTheSameReport()
    {
        var seen = new List<BuilderDiagnostic>();
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            OnDiagnostic = seen.Add,
        };

        DocWith(Hebrew).ToPdf(options);

        Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.FontNotResolved);
    }

    [Fact]
    public void UnrenderableTextPolicyThrow_Throws()
    {
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            UnrenderableText = UnrenderableTextPolicy.Throw,
        };

        var ex = Assert.Throws<UnrenderableTextException>(() => DocWith(Hebrew).ToPdf(options));
        Assert.Contains("FontProvider", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnrenderableTextPolicyOmit_DrawsNothingRatherThanQuestionMarks()
    {
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            UnrenderableText = UnrenderableTextPolicy.Omit,
        };

        string drawn = Drawn(DocWith(Hebrew).ToPdf(options));

        Assert.DoesNotContain("????", drawn);
    }

    [Fact]
    public void UnrenderableTextPolicyReport_KeepsTheOldPixels()
    {
        // Report is the default precisely because it must not change what existing callers get:
        // the run still goes down the WinAnsi path and still writes '?'. Only the diagnostic
        // is new.
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            UnrenderableText = UnrenderableTextPolicy.Report,
        };

        string drawn = Drawn(DocWith(Hebrew).ToPdf(options));

        Assert.Contains("????", drawn);
        Assert.NotNull(BuilderDiagnostics.LastError);
    }

    [Fact]
    public void Diagnostics_AreClearedAtTheStartOfEveryExport()
    {
        // A slot that is only ever written keeps a previous document's answer, so a caller
        // checking "did anything go wrong?" gets a stale yes.
        var failing = new PdfExportOptions { AllowSystemFontSubstitution = false };
        DocWith(Hebrew).ToPdf(failing);
        Assert.NotNull(BuilderDiagnostics.LastError);

        DocWith("plain ascii").ToPdf(failing);
        Assert.Null(BuilderDiagnostics.LastError);
    }

    // ------------------------------------------------------------------ compatibility

    [Fact]
    public void DefaultOptions_ProduceTheSameBytesAsTheParameterlessOverload()
    {
        // The options overload must be additive: a caller that supplies nothing gets exactly what
        // the original API gave. (The trailer /ID is 16 random bytes per build, so it is masked.)
        var doc = DocWith("Hello world", "Second paragraph");
        string a = Mask(doc.ToPdf());
        string b = Mask(doc.ToPdf(new PdfExportOptions()));

        Assert.Equal(a, b);
    }

    private static string Mask(byte[] pdf) => PdfContentProbe.NormalizedRaw(pdf);
}
