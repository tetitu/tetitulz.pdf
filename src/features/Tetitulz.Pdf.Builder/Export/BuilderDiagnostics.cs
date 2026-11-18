// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>What went wrong during an export that the exporter tolerated rather than threw for.</summary>
    public enum BuilderDiagnosticKind
    {
        /// <summary>
        /// No font available to this export covers a run's characters, so the run fell back to a
        /// standard-14 WinAnsi font — where every code point above U+00FF is written as
        /// <c>'?'</c>. This is the difference between "the text is missing" and "the text is
        /// there but unreadable", which look identical in the output bytes.
        /// </summary>
        FontNotResolved = 1,

        /// <summary>A font was found, but it has no glyph for some character in the run.</summary>
        GlyphMissing = 2,

        /// <summary>A candidate font program was rejected — wrong container, unparseable, or too large to embed unsubsetted.</summary>
        FontFormatRejected = 3,

        /// <summary>An image's bytes could not be decoded, so it was not drawn.</summary>
        ImageDecodeFailed = 4,

        /// <summary>
        /// The model carried something this export format cannot express, so it was dropped.
        ///
        /// <para>Distinct from every kind above, which are all failures to <i>read</i> an input:
        /// this one is a limit of the <i>output</i>. A DOCX has no paragraph background image and
        /// an ODT's is a style this exporter does not write, so the content is gone from those
        /// files while being present in the PDF and the HTML — and "the same model exported to
        /// four formats" is precisely when a caller needs to be told which one lost something.</para>
        /// </summary>
        UnsupportedByExportFormat = 5,

        /// <summary>
        /// The content <b>was</b> drawn, but not exactly as the model described it — a bounded
        /// approximation, made because the exact form would have cost unbounded work.
        ///
        /// <para>Distinct from <see cref="UnsupportedByExportFormat"/>, which means nothing was
        /// drawn at all. The distinction is worth keeping for the same reason the renderer keeps
        /// <c>PatternFillUnresolved</c> apart from <c>PatternApproximated</c>: "missing" and
        /// "present but wrong" send a caller looking in completely different places. Its first
        /// user is <c>PdfExporter.MaxBackgroundTiles</c>.</para>
        /// </summary>
        ContentApproximated = 6,

        /// <summary>
        /// The document reached <see cref="PdfExportOptions.MaxPages"/>: the pages within the limit were
        /// written whole, and the content after them was not laid out.
        /// </summary>
        PageLimitReached = 7,

        /// <summary>
        /// The file was written at a higher PDF version than <see cref="PdfExportOptions.Version"/>
        /// asked for, because the content uses a feature the asked-for version does not have; the
        /// detail names the feature and both versions. Reported only when a version was asked for.
        /// </summary>
        VersionRaised = 8,
    }

    /// <summary>One tolerated failure, with enough detail to act on.</summary>
    public sealed class BuilderDiagnostic
    {
        public BuilderDiagnostic(BuilderDiagnosticKind kind, string detail, Exception? exception = null)
        {
            Kind = kind;
            Detail = detail ?? "";
            Exception = exception;
        }

        public BuilderDiagnosticKind Kind { get; }

        /// <summary>Human-readable detail: which font, which code points, which run.</summary>
        public string Detail { get; }

        /// <summary>The underlying exception, when there was one.</summary>
        public Exception? Exception { get; }

        public override string ToString() => $"{Kind}: {Detail}";
    }

    /// <summary>
    /// The export's diagnostic sink — modelled on
    /// <c>Tetitulz.Pdf.Accessibility.Internal.AccessibilityDiagnostics</c>, which
    /// <c>CLAUDE.md</c> names as the reference implementation for this pattern.
    ///
    /// <para><b>Why a sink and not a single <c>LastError</c> slot.</b> One export tolerates
    /// several distinct failures — a missing font, then a missing glyph, then an undecodable
    /// image — and a <c>Last*</c> property keeps only the last of them. The event lets a host see
    /// all of them; the <c>[ThreadStatic]</c> slot answers the common "did anything go wrong?"
    /// question without wiring a handler.</para>
    ///
    /// <para><b>Read it on the thread that ran the export.</b> The slot is
    /// <c>[ThreadStatic]</c>, so a thread that never exported sees <c>null</c> — the honest
    /// answer — rather than a neighbour's message.</para>
    /// </summary>
    public static class BuilderDiagnostics
    {
        [ThreadStatic] private static BuilderDiagnostic? _last;

        /// <summary>
        /// The most recent tolerated failure on this thread, or <c>null</c>. Cleared at the start
        /// of every export, so a value read after one is always about that export — a slot that is
        /// only ever written accumulates stale answers across documents.
        /// </summary>
        public static BuilderDiagnostic? LastError => _last;

        /// <summary>Raised for every tolerated failure. Handlers must not throw.</summary>
        public static event Action<BuilderDiagnostic>? OnEvent;

        internal static void Clear() => _last = null;

        internal static void Report(BuilderDiagnosticKind kind, string detail, Exception? ex = null)
        {
            var d = new BuilderDiagnostic(kind, detail, ex);
            _last = d;

            // ONE implementation of subscriber fan-out, in Core. A try/catch round a single
            // multicast invoke contains a throwing handler's exception without fixing the delivery:
            // the first subscriber to throw skips every later one, which is the half that is
            // invisible to the caller and to review.
            DiagnosticFanOut.Invoke(OnEvent, d);

            // The export running on this thread's own handler (PdfExportOptions.OnDiagnostic): every report, not
            // only the font resolver's.
            var handler = ExportHandler;
            if (handler != null) DiagnosticFanOut.Invoke(handler, d);
        }

        /// <summary>
        /// The handler of the export running on this thread (<see cref="PdfExportOptions.OnDiagnostic"/>), set for
        /// the export's duration and restored after it, so a report made anywhere in the export reaches it.
        /// </summary>
        [ThreadStatic] internal static Action<BuilderDiagnostic>? ExportHandler;

        /// <summary>
        /// Reports an <c>Opacity</c> an export format cannot express, <paramref name="why"/> saying
        /// why — a DOCX and an ODT exporter's alike. Silent at full opacity, so the overwhelmingly
        /// common document raises nothing.
        /// </summary>
        internal static void ReportDroppedOpacity(double opacity, string what, string why)
        {
            double a = PdfExporter.Alpha01(opacity);
            if (a >= 1) return;
            Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                $"an opacity of {a.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"on {what} was dropped: {why}. The PDF and HTML exports of the same model still carry it.");
        }
    }
}
