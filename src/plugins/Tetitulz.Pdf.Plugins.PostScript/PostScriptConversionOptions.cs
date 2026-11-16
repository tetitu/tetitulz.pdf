// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>What <c>showpage</c> means for this conversion.</summary>
    public enum ShowPageBehavior
    {
        /// <summary>
        /// The PostScript is a fragment inside a host PDF page (a <c>/Subtype /PS</c> XObject or the
        /// inline <c>PS</c> operator). <c>showpage</c> resets the graphics state and emits nothing
        /// that paints: PLRM's implicit <c>erasepage</c> would white out the host page, which is
        /// certainly not what the document meant.
        /// </summary>
        ResetOnly,

        /// <summary>
        /// The PostScript is a document (<c>.ps</c> / <c>.eps</c> import). <c>showpage</c> closes
        /// the current page and starts another, <b>keeping the same VM</b> — PostScript preserves
        /// <c>userdict</c> across pages, and a prologue's procedures are used on every one of them.
        /// </summary>
        NewPage
    }

    /// <summary>How a document conversion orients its pages — Acrobat Distiller's <c>AutoRotatePages</c> parameter.</summary>
    public enum PostScriptAutoRotate
    {
        /// <summary>Every page as the program drew it.</summary>
        None,

        /// <summary>
        /// Each page turned — its box and its content, a quarter or half turn — so its predominant text
        /// reads left to right: a print driver draws a landscape page as rotated content on portrait
        /// paper, and says so only in a feature comment. A page with no text keeps its orientation.
        /// </summary>
        PageByPage
    }

    /// <summary>
    /// Per-conversion knobs. Every bound defaults to the matching <see cref="PsLimits"/> constant;
    /// a host that needs a different trade-off overrides it here rather than by editing the table.
    /// </summary>
    public sealed class PostScriptConversionOptions
    {
        /// <summary>Defaults suitable for converting an untrusted fragment.</summary>
        public static PostScriptConversionOptions Default => new();

        /// <summary>What <c>showpage</c> does. Defaults to <see cref="ShowPageBehavior.ResetOnly"/>.</summary>
        public ShowPageBehavior ShowPage { get; set; } = ShowPageBehavior.ResetOnly;

        /// <summary>
        /// Maximum interpreter steps. Defaults to <see cref="PsLimits.MaxTotalOps"/>. Lower it for a
        /// latency-sensitive host; the README records the measured steps-per-second so it can be
        /// sized against an SLA.
        /// </summary>
        public long MaxTotalOps { get; set; } = PsLimits.MaxTotalOps;

        /// <summary>
        /// Maximum cumulative composite allocation. Defaults to
        /// <see cref="PsLimits.MaxCompositeAllocationBytes"/>.
        /// </summary>
        public long MaxCompositeAllocationBytes { get; set; } = PsLimits.MaxCompositeAllocationBytes;

        /// <summary>
        /// Maximum emitted content stream, in bytes. Defaults to
        /// <see cref="PsLimits.MaxEmittedContentBytes"/>, which matches the host-side cap in
        /// <c>PdfLimits.MaxPluginContentStreamBytes</c> — raising it past that only produces output
        /// the renderer will reject.
        /// </summary>
        public int MaxEmittedContentBytes { get; set; } = PsLimits.MaxEmittedContentBytes;

        /// <summary>
        /// When false, <c>image</c> / <c>imagemask</c> / <c>colorimage</c> consume their operands
        /// and draw nothing. For a host that will not accept plugin-supplied image data at all.
        /// </summary>
        public bool AllowImages { get; set; } = true;

        /// <summary>
        /// When false, <c>show</c> and friends consume their operands and draw nothing. Text needs
        /// the standard-14 metrics and a font mapping; a host that wants geometry only can turn it
        /// off.
        /// </summary>
        public bool AllowText { get; set; } = true;

        /// <summary>
        /// The matrix mapping PostScript default user space onto the target space, as six numbers.
        /// Identity is correct for both surfaces this plugin serves — a PDF form XObject's space
        /// and PostScript default user space are both origin-bottom-left, +y up, 1 unit = 1/72 inch
        /// — so this exists for a host embedding the output somewhere else.
        ///
        /// <para>Refused by both entry points unless it is null or exactly six finite numbers
        /// (<see cref="System.ArgumentException"/>, <see cref="System.ArgumentOutOfRangeException"/>):
        /// a NaN here reaches every emitted coordinate.</para>
        /// </summary>
        public double[] BaseMatrix { get; set; }

        /// <summary>
        /// The TrueType program a standard font's text takes its <c>charpath</c> outlines from, asked by
        /// the standard-14 name (<c>Helvetica-Bold</c>, <c>Times-Roman</c>, …) at most once a conversion;
        /// null when it has none for that face, and the text keeps its metric box. Left null, the host's
        /// fonts are asked — Arial, Times New Roman and Courier New, or their metric-compatible
        /// Liberation substitutes, as Core's <c>SystemFontResolver</c> finds them — which makes the
        /// outlines depend on the machine; a host that wants the same answer everywhere supplies the
        /// faces (the <c>Plugins.Fonts</c> package carries Liberation). Symbol's and ZapfDingbats' codes
        /// are looked up by the character their built-in glyph names stand for, then a symbol font's
        /// private-use code. A provider that throws is reported, and the text keeps its box.
        /// </summary>
        public System.Func<string, byte[]> OutlineFontProvider { get; set; }

        /// <summary>
        /// How <c>ConvertDocument</c> orients its pages. Defaults to <see cref="PostScriptAutoRotate.None"/>:
        /// the pages as drawn. A printer's job wants <see cref="PostScriptAutoRotate.PageByPage"/>.
        /// </summary>
        public PostScriptAutoRotate AutoRotatePages { get; set; } = PostScriptAutoRotate.None;

        /// <summary>
        /// A copy, so an entry point that must force a setting does not reach into the caller's
        /// object to do it. <c>ConvertDocument</c> forces <see cref="ShowPage"/>; mutating the
        /// instance it was handed would give a host that reuses one options object document
        /// semantics on its next fragment conversion.
        ///
        /// <para>Kept next to the properties on purpose: a new property has to be added here too,
        /// and this is the only place that is obvious.</para>
        /// </summary>
        internal PostScriptConversionOptions Copy() => new()
        {
            ShowPage                   = ShowPage,
            MaxTotalOps                = MaxTotalOps,
            MaxCompositeAllocationBytes = MaxCompositeAllocationBytes,
            MaxEmittedContentBytes     = MaxEmittedContentBytes,
            AllowImages                = AllowImages,
            AllowText                  = AllowText,
            BaseMatrix                 = BaseMatrix,
            OutlineFontProvider        = OutlineFontProvider,
            AutoRotatePages            = AutoRotatePages
        };
    }
}
