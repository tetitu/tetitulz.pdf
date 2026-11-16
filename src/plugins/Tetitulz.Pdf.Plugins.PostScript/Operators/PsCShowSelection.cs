// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// The character a composite font's <c>cshow</c> selected, while its procedure runs (PLRM §8.2,
    /// <c>cshow</c>): the procedure is given the code's last byte, and a <c>show</c> — or a variant,
    /// or <c>charpath</c> — of that one byte in the same font shows the code the CMap selected. A
    /// print driver's <c>cshow</c> procedure does exactly this, a character at a time, to place each
    /// glyph itself.
    ///
    /// <para>It holds while the <c>cshow</c> that made it is on the execution stack: an error that
    /// unwinds the procedure leaves no selection behind for a later <c>show</c>.</para>
    /// </summary>
    internal sealed class PsCShowSelection
    {
        public PsCShowSelection(PsFrame frame, PsDict font, PsString code)
        {
            Frame = frame;
            Font = font;
            Code = code;
        }

        /// <summary>The <c>cshow</c> whose procedure is running.</summary>
        public PsFrame Frame { get; }

        /// <summary>The composite font <c>cshow</c> ran in.</summary>
        public PsDict Font { get; }

        /// <summary>The whole code the CMap selected.</summary>
        public PsString Code { get; }

        /// <summary>The byte the procedure was given: the code's last.</summary>
        public byte LastByte => Code[Code.Length - 1];

        /// <summary>
        /// The string a text operator in the procedure shows: the selected code for the one byte the
        /// procedure was given, in the font <c>cshow</c> ran in; any other string in that font is a
        /// <c>rangecheck</c>, as PLRM says. Outside a live selection, or in another font, the string
        /// as it is.
        /// </summary>
        public static PsString Resolve(PsInterpreter i, string op, PsString text)
        {
            PsCShowSelection selection = i.CShowSelection;
            if (selection == null || !ReferenceEquals(i.CurrentFontDict, selection.Font) || !selection.IsLive(i)) return text;
            if (text.Length != 1 || text[0] != selection.LastByte)
                throw new PsErrorException(PsErrorKind.RangeCheck, op,
                    "inside cshow's procedure a composite font shows only the one byte cshow gave it");
            return selection.Code;
        }

        private bool IsLive(PsInterpreter i)
        {
            for (int k = i.ExecStack.Count - 1; k >= 0; k--)
                if (ReferenceEquals(i.ExecStack[k], Frame)) return true;
            return false;
        }
    }
}
