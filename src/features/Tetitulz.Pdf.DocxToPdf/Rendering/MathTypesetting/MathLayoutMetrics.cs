// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.DocxToPdf.Rendering.MathTypesetting
{
    /// <summary>
    /// The layout constants of 2-D math setting, as fractions of the current font size.
    ///
    /// <para>
    /// <b>Why these live in the feature and not in <c>Core.Constants</c>.</b> That folder is the home
    /// for values <i>the PDF spec names</i>. None of these are in ISO 32000-1 — they are typesetting
    /// opinions from <i>The TeXbook</i> Appendix G and the OpenType MATH table. The precedent is one
    /// folder away: <c>Rendering/DocxLayoutMetrics.cs</c> already holds
    /// <c>VertAlignFontScale</c>, <c>SuperscriptRiseFactor</c> and <c>SubscriptDropFactor</c> — the
    /// same class of value, feature-local. A separate file because twelve math constants would
    /// swamp a file whose stated purpose is the <i>shared</i> factors, and the two sets are never
    /// compared with each other.
    /// </para>
    /// </summary>
    internal static class MathLayoutMetrics
    {
        /// <summary>Script size at one level down (TeX "script style").</summary>
        internal const double ScriptScaleLevel1 = 0.70;

        /// <summary>
        /// Script size at two levels down (TeX "scriptscript"). Terminal — level 3 and beyond stay
        /// here, exactly as TeX does, or a deeply nested formula would shrink to nothing.
        /// </summary>
        internal const double ScriptScaleLevel2 = 0.50;

        /// <summary>Below this a script is illegible in print, whatever the cascade says.</summary>
        internal const double MinScriptSizePt = 5.0;

        /// <summary>Rule thickness as a fraction of the em (TeX's <c>default_rule_thickness</c>).</summary>
        internal const double RuleThicknessEm = 0.04;

        /// <summary>
        /// Floor for a drawn rule, matching the existing underline emitter's own
        /// <c>Math.Max(0.3, …)</c> — a hairline that rounds to zero simply disappears.
        /// </summary>
        internal const double MinRuleThicknessPt = 0.4;

        /// <summary>Clearance above and below a fraction bar, inline (TeX: 1×rule).</summary>
        internal const double FractionGapTextEm = 0.06;

        /// <summary>Clearance above and below a fraction bar, display (TeX: 3×rule).</summary>
        internal const double FractionGapDisplayEm = 0.14;

        /// <summary>
        /// The maths axis — the height a fraction bar sits at. Half the x-height when the font
        /// reports one; this is the fallback.
        /// </summary>
        internal const double AxisHeightFallbackEm = 0.25;

        /// <summary>Superscript baseline shift (TeX σ13).</summary>
        internal const double SuperscriptShiftUpEm = 0.41;

        /// <summary>Subscript baseline shift when there is no superscript (TeX σ16).</summary>
        internal const double SubscriptShiftDownEm = 0.15;

        /// <summary>Subscript shift when a superscript is present too (TeX σ17) — deeper.</summary>
        internal const double SubSupSubShiftDownEm = 0.25;

        /// <summary>Minimum gap between a superscript's depth and a subscript's height.</summary>
        internal const double SubSupMinGapEm = 0.16;

        /// <summary>Vertical clearance between a radicand and its vinculum.</summary>
        internal const double RadicalVerticalGapEm = 0.06;

        /// <summary>How far the vinculum sits above the radicand's top.</summary>
        internal const double RadicalExtraAscenderEm = 0.05;

        /// <summary>Width of the drawn surd hook, as a fraction of the em.</summary>
        internal const double RadicalHookWidthEm = 0.55;

        /// <summary>The degree sits this far up the radical's total height (TeX: 60%).</summary>
        internal const double RadicalDegreeRaiseFactor = 0.60;

        /// <summary>Height of a grouping character drawn to its base's width — a brace, bracket, parenthesis or arrow (<c>m:groupChr</c>).</summary>
        internal const double GroupShapeHeightEm = 0.30;

        /// <summary>Gap between a base and an accent drawn over it.</summary>
        internal const double AccentGapEm = 0.04;

        /// <summary>Gap between an n-ary operator and a limit set above or below it.</summary>
        internal const double NaryLimitGapEm = 0.10;

        /// <summary>∑ and ∫ grow in display style.</summary>
        internal const double NaryDisplayOperatorScale = 1.40;

        /// <summary>Padding inside a delimiter pair.</summary>
        internal const double DelimiterPaddingEm = 0.10;

        /// <summary>
        /// Beyond this a vertically stretched delimiter becomes a hairline caricature, so it is
        /// drawn at the cap and reported instead.
        /// </summary>
        internal const double MaxDelimiterStretch = 3.0;

        /// <summary>A thin space, used between a function name and its argument.</summary>
        internal const double ThinSpaceEm = 0.167;

        /// <summary>Vertical gap between the rows of a matrix or equation array.</summary>
        internal const double StackRowGapEm = 0.35;

        /// <summary>Horizontal gap between the columns of a matrix.</summary>
        internal const double MatrixColumnGapEm = 0.60;

        /// <summary>
        /// Maximum cells in one matrix. Rows × columns are both file-controlled, so the product
        /// needs its own bound — the per-node ceiling counts constructs, not the grid it describes.
        /// </summary>
        internal const int MaxMatrixCells = 400;

        /// <summary>
        /// How far a display equation may be shrunk to fit its column before the renderer stops
        /// and reports. Below about half size the formula stops being readable, and a caller is
        /// better served by knowing it overflows than by a line of unreadable glyphs.
        /// </summary>
        internal const double MinDisplayMathScale = 0.50;

        /// <summary>
        /// Recursion guard. OMML nests arbitrarily and the builder is recursive; the linearizer's
        /// own ceiling is 64, but a box tree also allocates per level.
        /// </summary>
        internal const int MaxNestingDepth = 32;

        /// <summary>
        /// Aggregate guard. Depth alone does not bound a <b>wide</b> tree — a formula with tens of
        /// thousands of siblings is shallow and still unbounded.
        /// </summary>
        internal const int MaxNodeCount = 2000;
    }
}


