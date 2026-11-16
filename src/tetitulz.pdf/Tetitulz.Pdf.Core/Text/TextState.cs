// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Text
{
    internal sealed class TextState
    {
        public string FontName { get; set; }
        public double FontSize { get; set; }
        public double CharacterSpacing { get; set; } // Tc
        public double WordSpacing { get; set; } // Tw
        public double HorizontalScaling { get; set; } = 100; // Tz (percentage)
        public double Leading { get; set; } // TL
        public double TextRise { get; set; } // Ts
        public Matrix3x3 TextMatrix { get; set; } = Matrix3x3.Identity;
        public Matrix3x3 TextLineMatrix { get; set; } = Matrix3x3.Identity;

        /// <summary>
        /// Where the last glyph appended to the text ended, in device space — kept across <c>BT</c>,
        /// which resets the text matrix, so the gap to the next text can still be measured; null
        /// before any.
        /// </summary>
        public Matrix3x3? LastShownEnd { get; set; }

        public TextState Clone()
        {
            return new TextState
            {
                FontName = FontName,
                FontSize = FontSize,
                CharacterSpacing = CharacterSpacing,
                WordSpacing = WordSpacing,
                HorizontalScaling = HorizontalScaling,
                Leading = Leading,
                TextRise = TextRise,
                TextMatrix = TextMatrix,
                TextLineMatrix = TextLineMatrix,
                LastShownEnd = LastShownEnd,
            };
        }
    }
}
