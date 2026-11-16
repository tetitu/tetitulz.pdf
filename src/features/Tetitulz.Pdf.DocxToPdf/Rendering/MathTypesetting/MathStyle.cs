// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
namespace Tetitulz.Pdf.DocxToPdf.Rendering.MathTypesetting
{
    /// <summary>
    /// The typesetting state at a point in a formula: what size to set at, whether this is display
    /// or inline, and how the glyphs are styled.
    ///
    /// <para>
    /// Immutable, and stepped down by <see cref="ToScript"/> rather than mutated — a formula is a
    /// tree, and a shared mutable style would leak a numerator's size into its denominator's
    /// sibling.
    /// </para>
    /// </summary>
    internal readonly struct MathStyle
    {
        internal MathStyle(string fontName, double sizePt, bool display, int scriptLevel,
            bool italic, bool bold, string colorHex)
        {
            FontName = fontName;
            SizePt = sizePt;
            Display = display;
            ScriptLevel = scriptLevel;
            Italic = italic;
            Bold = bold;
            ColorHex = colorHex;
        }

        internal string FontName { get; }
        internal double SizePt { get; }

        /// <summary>Display style sets fractions at full size and puts n-ary limits above/below.</summary>
        internal bool Display { get; }

        internal int ScriptLevel { get; }
        internal bool Italic { get; }
        internal bool Bold { get; }
        internal string ColorHex { get; }

        internal static MathStyle Root(string fontName, double sizePt, bool display, string colorHex)
            => new MathStyle(fontName, sizePt, display, 0, true, false, colorHex);

        /// <summary>
        /// One level down. Level 1 is 70% and level 2 is 50%; deeper stays at 50%, which is what
        /// TeX does — otherwise a deeply nested formula shrinks to nothing.
        /// </summary>
        internal MathStyle ToScript()
        {
            int level = ScriptLevel + 1;
            double scale = level == 1
                ? MathLayoutMetrics.ScriptScaleLevel1
                : MathLayoutMetrics.ScriptScaleLevel2;

            double baseSize = SizePt / CurrentScale();
            double size = Math.Max(MathLayoutMetrics.MinScriptSizePt, baseSize * scale);

            // A script is never display style: its fractions set small and its limits sit beside.
            return new MathStyle(FontName, size, false, level, Italic, Bold, ColorHex);
        }

        /// <summary>The style a fraction's numerator and denominator are set in.</summary>
        internal MathStyle ForFractionPart() => Display
            ? new MathStyle(FontName, SizePt, false, ScriptLevel, Italic, Bold, ColorHex)
            : ToScript();

        internal MathStyle WithItalic(bool italic)
            => new MathStyle(FontName, SizePt, Display, ScriptLevel, italic, Bold, ColorHex);

        internal MathStyle WithBold(bool bold)
            => new MathStyle(FontName, SizePt, Display, ScriptLevel, Italic, bold, ColorHex);

        internal MathStyle WithColor(string colorHex)
            => new MathStyle(FontName, SizePt, Display, ScriptLevel, Italic, Bold, colorHex);

        internal MathStyle WithSize(double sizePt)
            => new MathStyle(FontName, sizePt, Display, ScriptLevel, Italic, Bold, ColorHex);

        private double CurrentScale() => ScriptLevel switch
        {
            0 => 1.0,
            1 => MathLayoutMetrics.ScriptScaleLevel1,
            _ => MathLayoutMetrics.ScriptScaleLevel2,
        };

        /// <summary>The em size, for turning the fractional constants into points.</summary>
        internal double Em => SizePt;

        /// <summary>Rule thickness at this size, floored so a hairline cannot vanish.</summary>
        internal double RuleThickness => Math.Max(
            MathLayoutMetrics.MinRuleThicknessPt, SizePt * MathLayoutMetrics.RuleThicknessEm);
    }
}



