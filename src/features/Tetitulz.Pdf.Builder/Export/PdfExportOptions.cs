// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>What to do about text no available font can render.</summary>
    public enum UnrenderableTextPolicy
    {
        /// <summary>
        /// Draw it anyway through the standard-14 fallback — where every code point above U+00FF
        /// becomes <c>'?'</c> — and raise a <see cref="BuilderDiagnosticKind.FontNotResolved"/>
        /// diagnostic. The default, because it is what the exporter has always done: changing the
        /// pixels for existing callers would be a silent regression on any host without a
        /// covering font.
        /// </summary>
        Report = 0,

        /// <summary>Omit the unrenderable run entirely, and report it. Missing beats mojibake.</summary>
        Omit = 1,

        /// <summary>Throw <see cref="UnrenderableTextException"/>. For callers that would rather fail than ship wrong output.</summary>
        Throw = 2,
    }

    /// <summary>Thrown when <see cref="UnrenderableTextPolicy.Throw"/> is selected and no font can render a run.</summary>
    public sealed class UnrenderableTextException : PdfException
    {
        public UnrenderableTextException(string message) : base(message) { }
    }

    /// <summary>
    /// A font the exporter needs, described to <see cref="PdfExportOptions.FontProvider"/>.
    /// </summary>
    public sealed class BuilderFontRequest
    {
        public BuilderFontRequest(string familyName, bool bold, bool italic, IReadOnlyList<int> requiredCodePoints)
        {
            FamilyName = familyName ?? "";
            Bold = bold;
            Italic = italic;
            RequiredCodePoints = requiredCodePoints ?? Array.Empty<int>();
        }

        /// <summary>
        /// The family the run asked for, verbatim — e.g. <c>"David"</c> or <c>"Noto Sans Hebrew"</c>.
        /// May be empty when the run named none.
        /// </summary>
        public string FamilyName { get; }

        public bool Bold { get; }
        public bool Italic { get; }

        /// <summary>
        /// The code points <b>this run</b> needs, not the document's.
        ///
        /// <para>The distinction matters: demanding that a single font cover every non-Latin-1
        /// character in the whole document, one CJK glyph in a Hebrew report would make resolution
        /// fail for the Hebrew as well.</para>
        /// </summary>
        public IReadOnlyList<int> RequiredCodePoints { get; }

        public override string ToString()
        {
            string style = Bold ? (Italic ? " bold italic" : " bold") : (Italic ? " italic" : "");
            return $"'{FamilyName}'{style} covering {RequiredCodePoints.Count} code point(s)";
        }
    }

    /// <summary>
    /// Per-export options for <see cref="PdfExporter"/>: where fonts come from, and what to do
    /// when none can render the text.
    ///
    /// <para>Reached through <c>Document.SaveAsPdf(Stream, PdfExportOptions)</c> /
    /// <c>Document.ToPdf(PdfExportOptions)</c>, which are <b>overloads</b> rather than optional
    /// parameters: this assembly ships as a NuGet package, and an optional parameter changes the
    /// compiled signature, so a consumer built against the earlier one would take a
    /// <see cref="MissingMethodException"/>.</para>
    /// </summary>
    public sealed class PdfExportOptions
    {
        /// <summary>
        /// Supplies a font program (sfnt TrueType with <c>glyf</c> outlines, or OpenType with CFF
        /// outlines) for a run. Return <c>null</c> to decline and let resolution continue.
        ///
        /// <para><b>Licensing:</b> whatever this returns is embedded in the output and therefore
        /// redistributed. The caller is responsible for holding redistribution rights to it. This
        /// is the deterministic path — it is the only one where the caller knowingly chooses the
        /// program that ships, which is what a server that must produce identical output on every
        /// host needs.</para>
        /// </summary>
        public Func<BuilderFontRequest, byte[]?>? FontProvider { get; set; }

        /// <summary>
        /// Fall back to fonts installed on the host when <see cref="FontProvider"/> declines.
        /// <b>Default true</b>.
        ///
        /// <para>The default differs from
        /// <c>Accessibility.FontEmbeddingOptions.AllowSystemFontSubstitution</c>, which is false,
        /// and the difference is deliberate: there, embedding a font is a new action the caller
        /// opts into. Here it is the exporter's only existing behaviour, so defaulting to false
        /// would silently stop every current caller from rendering Hebrew, Arabic or CJK at
        /// all.</para>
        /// </summary>
        public bool AllowSystemFontSubstitution { get; set; } = true;

        /// <summary>
        /// Kern text with its font's own kerning — an embedded font's tables, a standard-14 face's AFM
        /// pairs — for every run that does not say otherwise (<see cref="Model.TextRun.Kerning"/>). Off by default, so an existing document
        /// keeps its exact layout; the HTML converter turns it on per run as a browser does.
        /// </summary>
        public bool Kerning { get; set; }

        /// <summary>
        /// Draw text in an embedded font with the font's contextual alternates (<c>GSUB</c> <c>calt</c>),
        /// for every run that does not say otherwise (<see cref="Model.TextRun.ContextualAlternates"/>).
        /// Off by default, as Word leaves it and so an existing document keeps its glyphs; the HTML
        /// converter turns it on per run as a browser does.
        /// </summary>
        public bool ContextualAlternates { get; set; }

        /// <summary>
        /// Form the standard ligatures of an embedded font (<c>GSUB</c> <c>liga</c>: fi, fl and the
        /// rest; several glyphs becoming one, written back in <c>/ToUnicode</c> as the letters), for
        /// every run that does not say otherwise (<see cref="Model.TextRun.Ligatures"/>). Off by
        /// default, as Word leaves it and so an existing document keeps its glyphs; the HTML converter
        /// turns it on per run as a browser does. The required ligatures (<c>rlig</c>) are formed
        /// either way.
        /// </summary>
        public bool Ligatures { get; set; }

        /// <summary>
        /// Form the discretionary ligatures of an embedded font (<c>GSUB</c> <c>dlig</c>) for every run
        /// that does not say otherwise (<see cref="Model.TextRun.DiscretionaryLigatures"/>). Off by
        /// default; CSS's <c>discretionary-ligatures</c> and a DOCX run's discretional
        /// <c>w14:ligatures</c> turn it on per run.
        /// </summary>
        public bool DiscretionaryLigatures { get; set; }

        /// <summary>
        /// Form the historical ligatures of an embedded font (<c>GSUB</c> <c>hlig</c>) for every run that
        /// does not say otherwise (<see cref="Model.TextRun.HistoricalLigatures"/>). Off by default.
        /// </summary>
        public bool HistoricalLigatures { get; set; }

        /// <summary>
        /// The most pages the export writes; <b>0, the default, for no limit</b>. A document that would run
        /// longer ends at the last whole page within the limit, and a
        /// <see cref="BuilderDiagnosticKind.PageLimitReached"/> diagnostic says so.
        /// <para>Every page an exporter writes is linear in its input, but the ratio is the input's to choose —
        /// a font size of a million points over 2000 words writes 2001 pages — so a host converting what it was
        /// handed sets the ceiling it can afford. Truncating rather than refusing is the PostScript converter's
        /// choice for the same cap (<c>PsLimits.MaxPages</c>). A negative value is refused.</para>
        /// </summary>
        public int MaxPages
        {
            get => _maxPages;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "MaxPages is 0 for no limit, or a positive page count.");
                _maxPages = value;
            }
        }

        private int _maxPages;

        /// <summary>What to do when no font can render a run. See <see cref="UnrenderableTextPolicy"/>.</summary>
        public UnrenderableTextPolicy UnrenderableText { get; set; } = UnrenderableTextPolicy.Report;

        /// <summary>
        /// Per-export diagnostic callback, in addition to the process-wide
        /// <see cref="BuilderDiagnostics.OnEvent"/>. Handlers must not throw.
        /// </summary>
        public Action<BuilderDiagnostic>? OnDiagnostic { get; set; }

        /// <summary>
        /// Uniform scale applied to the exported page. Default <c>1.0</c>; values outside
        /// <c>[0.1, 2.0]</c> are brought into range by <see cref="ClampScale"/>.
        ///
        /// <para><b>The paper is scaled, not the layout.</b> The exporter multiplies each page's
        /// <c>/MediaBox</c> by this factor and prepends one <c>s 0 0 s 0 0 cm</c> to the page's
        /// content; every <see cref="Model.Section"/> dimension keeps its meaning, so pagination,
        /// wrapping and the header/footer offsets are identical to an unscaled export. The
        /// <c>cm</c> is a pure scale with no translation, so logical 0 maps to 0 and the logical
        /// top edge maps to the scaled top edge — both stay put.</para>
        ///
        /// <para>A caller that wants the browser's print semantics instead — paper fixed, content
        /// shrinks, more of it per page — divides its own Section by the same factor, which is
        /// exactly what <c>Tetitulz.Pdf.HtmlToPdf</c>'s managed engine does for
        /// <c>HtmlToPdfOptions.Scale</c>.</para>
        /// </summary>
        public double Scale { get; set; } = 1.0;

        // Chromium's supported print-scale band. Out of range is clamped rather than thrown,
        // because the browser engine has always clamped here (an out-of-range value otherwise
        // fails deep inside Playwright with an opaque error) and the two engines must not
        // disagree about what a given Scale means.
        private const double MinScale = 0.1;
        private const double MaxScale = 2.0;

        /// <summary>
        /// <paramref name="scale"/> brought into the supported <c>[0.1, 2.0]</c> band.
        ///
        /// <para>Public because three places need the <i>effective</i> factor — the exporter's
        /// <c>cm</c>, the link-annotation <c>/Rect</c> that the content matrix cannot reach, and
        /// the HTML mapper's Section division — and three matching literals in three files is how
        /// they come to disagree.</para>
        ///
        /// <para>The <c>NaN</c> arm is load-bearing rather than defensive: <see cref="Math.Max(double,double)"/>
        /// and <see cref="Math.Min(double,double)"/> both propagate NaN, so an unguarded clamp
        /// reaches <c>PdfDocumentBuilder.AddPage</c> — whose <c>widthPt &lt;= 0</c> guard is
        /// <c>false</c> for NaN — and writes <c>NaN</c> into the <c>/MediaBox</c>, producing a
        /// file no reader can open.</para>
        /// </summary>
        public static double ClampScale(double scale)
        {
            if (double.IsNaN(scale)) return 1.0;
            return Math.Max(MinScale, Math.Min(MaxScale, scale));
        }

        /// <summary>
        /// Largest font program to embed when it cannot be subsetted, in bytes. Default 4 MiB.
        ///
        /// <para>CFF/OpenType programs are embedded <b>whole</b> — the subsetter only handles
        /// <c>glyf</c> outlines — so a CJK <c>.otf</c> would add 10–20 MB to every document that
        /// touched one character of it. Past this ceiling the candidate is declined with a
        /// <see cref="BuilderDiagnosticKind.FontFormatRejected"/> diagnostic instead.</para>
        /// </summary>
        public int MaxUnsubsettableFontBytes { get; set; } = 4 * 1024 * 1024;

        /// <summary>
        /// Places each line's text in its line box as CSS does: the leading — the line height less
        /// the text's own height — split half above and half below it (CSS 2.1 §10.8.1). <b>Default
        /// false</b>: the baseline sits on the line box's bottom edge, all the leading above the
        /// text.
        /// </summary>
        /// <remarks>
        /// <para>Only where the text sits inside each line moves — never a line's height, so
        /// wrapping, pagination and every measured size are identical either way.</para>
        /// <para>At the default line height the text moves up about a quarter of its size; in a
        /// tall line, by half the extra height — CSS <c>line-height: 31px</c> on 14px text sat a
        /// third of a line low, its last line on the box's bottom edge. The HTML converter turns
        /// this on; a word-processor document keeps the default.</para>
        /// </remarks>
        public bool HalfLeading { get; set; }

        /// <summary>
        /// The PDF version the file's header names. <b><c>null</c>, the default, writes the lowest
        /// version the document's content needs, and never below 1.4.</b>
        /// </summary>
        /// <remarks>
        /// <para>The header is never lower than the content: a version below what an object or a
        /// content stream uses (a JPEG 2000 image is 1.5, an OpenType font program 1.6) is raised to
        /// it, and a <see cref="BuilderDiagnosticKind.VersionRaised"/> report names the feature. A
        /// higher version is written as asked — <c>PdfVersion.Pdf17</c> for a reader that wants it,
        /// <c>PdfVersion.Pdf20</c> for PDF 2.0. A version no edition of the standard publishes (1.0
        /// to 1.7, and 2.0) is refused.</para>
        /// <para>The version is the header alone: nothing is left out or written differently to fit
        /// it. A file for PDF/A is made by <c>Plugins.Conformance.PdfAConverter</c>, not by this
        /// option.</para>
        /// </remarks>
        public Tetitulz.Pdf.Core.Document.PdfVersion? Version
        {
            get => _version;
            set
            {
                if (value.HasValue && !value.Value.IsPublished)
                    throw new ArgumentOutOfRangeException(nameof(value), value.Value.ToString(),
                        "A PDF version is 1.0 to 1.7, or 2.0.");
                _version = value;
            }
        }

        private Tetitulz.Pdf.Core.Document.PdfVersion? _version;

        internal static readonly PdfExportOptions Default = new PdfExportOptions();
    }
}
