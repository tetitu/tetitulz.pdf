// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Which font each text-showing operator of a content stream uses, and whether it paints — the
    /// question ISO 14289-1 7.21.4.1 asks of font embedding: a font referenced only in render mode 3
    /// (invisible) or 7 (clip) renders nothing, and NOTE 2 exempts it.
    /// </summary>
    /// <remarks>
    /// <para><b>One answer for both PDF/UA checkers.</b> The deep checker's UA21 tracked the text
    /// state and granted the exemption; the plugin's UA25 counted every font in a page's resources,
    /// used or not, painted or not. So the invisible font of an OCR text layer — which this library
    /// writes unembedded on purpose — passed one check and failed the other, and an audit that runs
    /// both reported the library's own searchable PDFs as non-conforming.</para>
    ///
    /// <para><b>The state follows the graphics state</b>: the selected font and the render mode are
    /// part of it (§9.3), so <c>q</c> saves them and <c>Q</c> restores them — or an OCR layer's
    /// <c>q … 3 Tr … Q</c> followed by visible text would read the visible text as invisible,
    /// granting the exemption to a font that paints. The save stack is bounded by
    /// <see cref="PdfLimits.MaxGraphicsStateStackDepth"/>; saves past it are counted, not stored, so
    /// the matching restores still pair up.</para>
    ///
    /// <para>A stream starts in the default state — no font, render mode 0 — because a form XObject
    /// or appearance stream is read on its own here and the state it would inherit is not known. An
    /// operand that is not an integer from 0 to 7 is not a render mode: it leaves the mode as it
    /// was.</para>
    /// </remarks>
    public sealed class TextPaintTracker
    {
        private readonly Stack<(string Font, TextRenderMode Mode)> _saved = new Stack<(string, TextRenderMode)>();
        private int _unsavedDepth;

        /// <summary>The font resource name the last <c>Tf</c> selected, or null.</summary>
        public string CurrentFont { get; private set; }

        /// <summary>The render mode the last <c>Tr</c> set.</summary>
        public TextRenderMode RenderMode { get; private set; } = TextRenderMode.Fill;

        /// <summary>
        /// Whether text shown now puts marks on the page — every mode but 3 (invisible) and 7 (add
        /// to the clip path and paint nothing).
        /// </summary>
        public bool Paints => RenderMode != TextRenderMode.Invisible && RenderMode != TextRenderMode.Clip;

        /// <summary>
        /// Takes one operator. Returns true when it shows text; <see cref="CurrentFont"/> and
        /// <see cref="Paints"/> then describe that text.
        /// </summary>
        public bool Observe(string op, IReadOnlyList<PdfObject> operands)
        {
            switch (op)
            {
                case PdfOperators.Save:
                    if (_saved.Count < PdfLimits.MaxGraphicsStateStackDepth) _saved.Push((CurrentFont, RenderMode));
                    else _unsavedDepth++;
                    return false;

                case PdfOperators.Restore:
                    if (_unsavedDepth > 0) _unsavedDepth--;
                    else if (_saved.Count > 0) (CurrentFont, RenderMode) = _saved.Pop();
                    return false;

                case PdfOperators.SetFont:
                    for (int k = 0; k < operands.Count; k++)
                        if (operands[k] is PdfName name) { CurrentFont = name.Value; break; }
                    return false;

                case PdfOperators.SetTextRenderMode:
                    // §9.3.6 defines 0 through 7. Anything else is not a mode — and cast as it stood,
                    // 4294967299 would have kept its low 32 bits and read as 3, invisible.
                    if (operands.Count > 0 && operands[operands.Count - 1] is PdfNumber mode && mode.IsInteger
                        && mode.LongValue >= (long)TextRenderMode.Fill && mode.LongValue <= (long)TextRenderMode.Clip)
                        RenderMode = (TextRenderMode)(int)mode.LongValue;
                    return false;

                case PdfOperators.ShowText:
                case PdfOperators.ShowTextArray:
                case PdfOperators.NextLineShowText:
                case PdfOperators.NextLineSetSpacingShowText:
                    return true;

                default:
                    return false;
            }
        }
    }
}
