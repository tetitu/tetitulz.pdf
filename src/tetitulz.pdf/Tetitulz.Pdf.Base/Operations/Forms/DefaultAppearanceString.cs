// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// What a default-appearance string (<c>/DA</c>, §12.7.3.3) says: the font resource, its size,
    /// and the fill colour.
    /// </summary>
    /// <remarks>
    /// <para><b>One reading, for three consumers that each had their own.</b> The form appearance
    /// generator, the renderers' no-<c>/AP</c> fallback and the annotation appearance builder all
    /// parsed <c>/DA</c>, and they disagreed:</para>
    /// <list type="bullet">
    ///   <item>The renderers read <c>g</c> and <c>rg</c> but not <c>k</c>. So a CMYK
    ///     <c>/DA</c> drew its text black in both renderers, while the appearance the library
    ///     generated for the same annotation used the colour.</item>
    ///   <item>Two of them pasted the colour operands back into a content stream as they found them,
    ///     so an operand that was not a number went out as whatever it was.</item>
    ///   <item>The size a string with no <c>Tf</c> is drawn at was 10 in one and 12 in another, and
    ///     the second's comment said the two agreed.</item>
    /// </list>
    /// <para><b>A leaf by construction</b>: no static fields, so no type initializer, and nothing
    /// here that a renderer's initialization edge could reach.</para>
    /// </remarks>
    internal sealed class DefaultAppearanceString
    {
        /// <summary>
        /// The size text is drawn at when the string sets none: the renderers' no-<c>/AP</c> fallback
        /// and the annotation appearance builder alike, so the drawn and the generated appearance
        /// agree.
        /// </summary>
        internal const float DefaultFontSize = 10f;

        private DefaultAppearanceString(string fontResourceName, float? fontSize, double[] fill)
        {
            FontResourceName = fontResourceName;
            FontSize = fontSize;
            Fill = fill;
        }

        /// <summary>
        /// The font resource named by the <c>Tf</c> operator, without its slash; <c>null</c> when the
        /// string names none.
        /// </summary>
        internal string FontResourceName { get; }

        /// <summary>
        /// The <c>Tf</c> operator's size, <c>null</c> when there is none or it is not a number.
        /// <c>0</c> is §12.7.3.3's "auto-size", and is left to each consumer.
        /// </summary>
        internal float? FontSize { get; }

        /// <summary>
        /// The fill colour's components: one (<c>g</c>), three (<c>rg</c>) or four (<c>k</c>);
        /// <c>null</c> when the string sets no fill colour, or none whose operands are all numbers.
        /// The last one set wins, as it would in a content stream.
        /// </summary>
        internal double[] Fill { get; }

        /// <summary>Reads <paramref name="da"/>. Never returns <c>null</c>.</summary>
        internal static DefaultAppearanceString Parse(string da)
        {
            string fontName = null;
            float? fontSize = null;
            double[] fill = null;
            if (string.IsNullOrEmpty(da)) return new DefaultAppearanceString(null, null, null);

            string[] tokens = da.Split(new[] { ' ', '\t', '\r', '\n', '\f', '\0' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                switch (tokens[i])
                {
                    case PdfOperators.SetFont when i >= 1:
                        fontSize = TryNumber(tokens[i - 1], out double size) ? (float)size : (float?)null;
                        fontName = i >= 2 && tokens[i - 2].Length > 1 && tokens[i - 2][0] == '/'
                            ? tokens[i - 2].Substring(1)
                            : null;
                        break;

                    case PdfOperators.SetGrayFill when i >= 1:
                        fill = Operands(tokens, i, 1) ?? fill;
                        break;

                    case PdfOperators.SetRgbFill when i >= 3:
                        fill = Operands(tokens, i, 3) ?? fill;
                        break;

                    case PdfOperators.SetCmykFill when i >= 4:
                        fill = Operands(tokens, i, 4) ?? fill;
                        break;
                }
            }

            return new DefaultAppearanceString(fontName, fontSize, fill);
        }

        /// <summary>
        /// The fill colour as a content-stream operator (<c>0 g</c>, <c>0 0 1 rg</c>,
        /// <c>0 0 0 1 k</c>), its numbers written by the one PDF formatter; <c>null</c> when the
        /// string sets none.
        /// </summary>
        internal string FillOperator()
        {
            if (Fill == null) return null;
            var sb = new StringBuilder();
            foreach (double c in Fill) sb.Append(PdfRealFormat.Format(c)).Append(' ');
            sb.Append(Fill.Length == 1 ? PdfOperators.SetGrayFill
                    : Fill.Length == 3 ? PdfOperators.SetRgbFill
                    : PdfOperators.SetCmykFill);
            return sb.ToString();
        }

        /// <summary>
        /// The <paramref name="count"/> operands before <paramref name="op"/>, or <c>null</c> when
        /// any of them is not a number. A colour with an unreadable component is not a colour.
        /// </summary>
        private static double[] Operands(string[] tokens, int op, int count)
        {
            var values = new double[count];
            for (int k = 0; k < count; k++)
                if (!TryNumber(tokens[op - count + k], out values[k])) return null;
            return values;
        }

        private static bool TryNumber(string token, out double value)
            => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
