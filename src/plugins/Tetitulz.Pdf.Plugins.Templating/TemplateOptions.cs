// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Templating
{
    /// <summary>
    /// Where a template's replaceable text lives. A template may use both.
    /// </summary>
    [Flags]
    public enum TemplateSources
    {
        /// <summary>Named AcroForm fields.</summary>
        FormFields = 1,

        /// <summary>Literal placeholder text drawn on the page, e.g. <c>{{name}}</c>.</summary>
        PageTokens = 2,

        /// <summary>Both. The default.</summary>
        All = FormFields | PageTokens,
    }

    /// <summary>
    /// How a replacement is fitted into the space the placeholder occupied.
    /// </summary>
    public enum TemplateFit
    {
        /// <summary>
        /// Fit the replacement to exactly the placeholder's advance, shrinking the font size if it
        /// would otherwise be wider, so that nothing else on the line moves. The default, because a
        /// template's placeholder normally sits in a designed slot — a table cell, a ruled line —
        /// and reflowing the line around it is the more surprising outcome.
        /// </summary>
        KeepSlot = 0,

        /// <summary>
        /// Draw the replacement at its natural width. Text after it on the same line shifts.
        /// </summary>
        Natural = 1,
    }

    /// <summary>Per-call policy for <see cref="PdfTemplateFiller"/>.</summary>
    public sealed class TemplateOptions
    {
        /// <summary>Which placeholder kinds to fill. Defaults to <see cref="TemplateSources.All"/>.</summary>
        public TemplateSources Sources { get; set; } = TemplateSources.All;

        /// <summary>
        /// Opening delimiter of a page token. Defaults to <c>{{</c>.
        ///
        /// <para><b>The default is the riskiest available choice, and it is the default because it
        /// is what template authors expect.</b> <c>{</c>/<c>}</c> is a kerning pair in most Latin
        /// fonts, so a producer is <i>more</i> likely to split it across two show operations than
        /// almost any other pair — which is precisely the case the matcher has to handle. If you
        /// control the template, a delimiter that does not kern (<c>[[</c>, <c>&lt;&lt;</c>) gives
        /// the matcher an easier job.</para>
        /// </summary>
        public string TokenOpen { get; set; } = "{{";

        /// <summary>Closing delimiter of a page token. Defaults to <c>}}</c>.</summary>
        public string TokenClose { get; set; } = "}}";

        /// <summary>How to fit a replacement into the placeholder's space.</summary>
        public TemplateFit Fit { get; set; } = TemplateFit.KeepSlot;

        /// <summary>
        /// After writing, re-read the produced document and report any replaced token that is
        /// still extractable from it, on <see cref="TemplateFillReport.UnverifiedKeys"/>.
        /// Defaults to <c>true</c>.
        /// </summary>
        /// <remarks>
        /// The check can only falsify — extraction is lossy, so finding a token proves the erasure
        /// missed, while not finding one proves nothing. Turning it off skips both the check and
        /// the buffering it needs (the produced document is held in memory so it can be re-read).
        /// </remarks>
        public bool VerifyErasure { get; set; } = true;

        /// <summary>
        /// Paragraph direction for replacement values.
        ///
        /// <para>Defaults to <see cref="BaseDirection.Auto"/>, which takes the direction from the
        /// value's first strong character. <b>For a Hebrew or Arabic template, set this to
        /// <see cref="BaseDirection.RightToLeft"/>.</b> Auto-detection is wrong for exactly the
        /// values a form holds — <c>"Tel Aviv 12"</c> or <c>"1,234 ₪"</c> in a Hebrew field start
        /// with a Latin word or a digit and so resolve left-to-right, putting the currency sign and
        /// the trailing punctuation on the wrong side. Direction is a property of the template, and
        /// no amount of looking at the value recovers it.</para>
        /// </summary>
        public BaseDirection BaseDirection { get; set; } = BaseDirection.Auto;

        /// <summary>
        /// Supplies font <b>program</b> bytes (TrueType/OpenType) for text the template's own fonts
        /// cannot encode — anything outside Latin-1, so every Hebrew or Arabic value. Called with
        /// the font name being substituted for; return <c>null</c> to decline.
        ///
        /// <para>Embedding redistributes the font, which is why this is the caller's decision and
        /// why <see cref="AllowSystemFontSubstitution"/> defaults to false. This library ships no
        /// fonts and licenses none.</para>
        /// </summary>
        public Func<string, byte[]> FontProvider { get; set; }

        /// <summary>
        /// Permit falling back to a font installed on this host when <see cref="FontProvider"/>
        /// declines or is unset. Defaults to <c>false</c>.
        ///
        /// <para>Note for Linux deployments: <b>Liberation has no Hebrew coverage</b>, and it is
        /// what a minimal container installs. Setting this true on such a host still fails to find
        /// Hebrew — install <c>fonts-culmus</c> or point <c>TETITULZ_PDF_FONTDIR</c> at fonts the
        /// deployment owns, which is also the configuration that makes this flag carry no licensing
        /// risk.</para>
        /// </summary>
        public bool AllowSystemFontSubstitution { get; set; }

        /// <summary>
        /// Draw <c>/AP</c> appearance streams for filled form fields. Defaults to <c>true</c>. When
        /// false the values are written and <c>/NeedAppearances</c> is left true, deferring drawing
        /// to the viewer.
        /// </summary>
        public bool GenerateAppearances { get; set; } = true;
    }
}
