// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The embeddings, overrides and isolates open at a point of a text, as its bidi controls open and
    /// close them (UAX #9 X1–X8) — what a producer that shapes a piece at a time needs to know of the
    /// whole: whether the piece lies inside an override, and in which direction.
    /// </summary>
    /// <remarks>
    /// <para>A level says how a piece is ordered among its neighbours, not that its own characters are
    /// held in one direction whatever their class: a Latin word inside a right-to-left override and the
    /// same word inside a right-to-left embedding share a level, and only the override reverses it. A
    /// producer shaping the piece on its own has to ask for the override (<see
    /// cref="TextRunOptions.DirectionalOverride"/>), and this is where it learns of it.</para>
    /// <para>An isolate's initiator starts a context the override around it does not reach (X5a–X5c),
    /// and its PDI closes everything opened inside it (X6a); a PDF closes the innermost embedding or
    /// override, never an isolate, and is ignored with nothing to close (X7). The legacy DOCX renderer's
    /// lines (<c>LineBidi</c>) and the Builder's pieces both read it.</para>
    /// </remarks>
    internal sealed class BidiControlStack
    {
        private readonly StringBuilder _open = new StringBuilder();

        /// <summary>A stack holding <paramref name="open"/>, the controls an earlier text left open, outermost first.</summary>
        internal BidiControlStack(string open = "")
        {
            foreach (char c in open ?? "") Feed(c);
        }

        /// <summary>The controls open now, outermost first — to carry to the text that follows.</summary>
        internal string Open => _open.ToString();

        /// <summary>
        /// The direction an override open now holds text in, or <c>null</c> when the innermost open
        /// control is an embedding, an isolate or nothing.
        /// </summary>
        internal BaseDirection? OverrideDirection
        {
            get
            {
                if (_open.Length == 0) return null;
                char top = _open[_open.Length - 1];
                return top == UnicodeBidi.Controls.LeftToRightOverride ? BaseDirection.LeftToRight
                     : top == UnicodeBidi.Controls.RightToLeftOverride ? BaseDirection.RightToLeft
                     : (BaseDirection?)null;
            }
        }

        /// <summary>Takes <paramref name="c"/> into account; a character that is no control changes nothing.</summary>
        internal void Feed(char c)
        {
            switch (c)
            {
                case UnicodeBidi.Controls.LeftToRightEmbedding:
                case UnicodeBidi.Controls.RightToLeftEmbedding:
                case UnicodeBidi.Controls.LeftToRightOverride:
                case UnicodeBidi.Controls.RightToLeftOverride:
                case UnicodeBidi.Controls.LeftToRightIsolate:
                case UnicodeBidi.Controls.RightToLeftIsolate:
                case UnicodeBidi.Controls.FirstStrongIsolate:
                    _open.Append(c);
                    break;
                case UnicodeBidi.Controls.PopDirectionalFormatting:
                    if (_open.Length > 0 && !IsIsolate(_open[_open.Length - 1])) _open.Length--;
                    break;
                case UnicodeBidi.Controls.PopDirectionalIsolate:
                    int isolate = _open.Length - 1;
                    while (isolate >= 0 && !IsIsolate(_open[isolate])) isolate--;
                    if (isolate >= 0) _open.Length = isolate;
                    break;
            }
        }

        /// <summary>Takes every character of <paramref name="text"/> into account, in order.</summary>
        internal void Feed(string text)
        {
            if (text == null) return;
            foreach (char c in text) Feed(c);
        }

        private static bool IsIsolate(char c)
            => c == UnicodeBidi.Controls.LeftToRightIsolate || c == UnicodeBidi.Controls.RightToLeftIsolate
               || c == UnicodeBidi.Controls.FirstStrongIsolate;
    }
}
