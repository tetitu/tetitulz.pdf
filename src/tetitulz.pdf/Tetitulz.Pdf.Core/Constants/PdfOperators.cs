// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// The PDF content-stream operator tokens (PDF 32000-1:2008, Annex A).
    ///
    /// <para>These are <c>const string</c> rather than <see cref="Objects.PdfName"/> statics on
    /// purpose: operators are dispatched through <c>switch</c> statements, and a C# <c>case</c>
    /// label requires a compile-time constant. <see cref="Objects.PdfName"/> covers the
    /// dictionary-key half of the problem; this type covers the operator half.</para>
    ///
    /// <para>The names are semantic rather than literal, because several operators are unreadable
    /// as identifiers (<c>f*</c>, <c>'</c>, <c>"</c>) and several differ only by case
    /// (<c>g</c>/<c>G</c>, <c>k</c>/<c>K</c>, <c>w</c>/<c>W</c>) — a class of bug this type is
    /// specifically meant to prevent.</para>
    /// </summary>
    public static class PdfOperators
    {
        // ---- Graphics state ----
        /// <summary><c>q</c> — push a copy of the graphics state.</summary>
        public const string Save = "q";
        /// <summary><c>Q</c> — pop the graphics state.</summary>
        public const string Restore = "Q";
        /// <summary><c>cm</c> — concatenate a matrix onto the CTM.</summary>
        public const string ConcatMatrix = "cm";
        /// <summary><c>w</c> — set line width. Not to be confused with <see cref="Clip"/> (<c>W</c>).</summary>
        public const string SetLineWidth = "w";
        /// <summary><c>J</c> — set line cap style.</summary>
        public const string SetLineCap = "J";
        /// <summary><c>j</c> — set line join style.</summary>
        public const string SetLineJoin = "j";
        /// <summary><c>M</c> — set miter limit.</summary>
        public const string SetMiterLimit = "M";
        /// <summary><c>d</c> — set dash pattern.</summary>
        public const string SetDash = "d";
        /// <summary><c>ri</c> — set rendering intent.</summary>
        public const string SetRenderingIntent = "ri";
        /// <summary><c>i</c> — set flatness tolerance (ignored by this renderer).</summary>
        public const string SetFlatness = "i";
        /// <summary><c>gs</c> — apply a named ExtGState dictionary.</summary>
        public const string SetExtGState = "gs";

        // ---- Path construction ----
        /// <summary><c>m</c> — begin a new subpath at (x, y).</summary>
        public const string MoveTo = "m";
        /// <summary><c>l</c> — append a straight line segment.</summary>
        public const string LineTo = "l";
        /// <summary><c>c</c> — append a cubic Bézier with both control points given.</summary>
        public const string CurveTo = "c";
        /// <summary><c>v</c> — cubic Bézier using the current point as the first control point.</summary>
        public const string CurveToV = "v";
        /// <summary><c>y</c> — cubic Bézier using the endpoint as the second control point.</summary>
        public const string CurveToY = "y";
        /// <summary><c>h</c> — close the current subpath.</summary>
        public const string ClosePath = "h";
        /// <summary><c>re</c> — append a complete rectangle subpath.</summary>
        public const string Rectangle = "re";

        // ---- Path painting ----
        /// <summary><c>S</c> — stroke the path.</summary>
        public const string Stroke = "S";
        /// <summary><c>s</c> — close then stroke the path.</summary>
        public const string CloseStroke = "s";
        /// <summary><c>f</c> — fill the path using the nonzero winding rule.</summary>
        public const string Fill = "f";
        /// <summary><c>F</c> — obsolete synonym for <see cref="Fill"/>, retained for PDF 1.0 files.</summary>
        public const string FillObsolete = "F";
        /// <summary><c>f*</c> — fill using the even-odd rule.</summary>
        public const string FillEvenOdd = "f*";
        /// <summary><c>B</c> — fill then stroke, nonzero winding.</summary>
        public const string FillStroke = "B";
        /// <summary><c>B*</c> — fill then stroke, even-odd.</summary>
        public const string FillStrokeEvenOdd = "B*";
        /// <summary><c>b</c> — close, fill, then stroke, nonzero winding.</summary>
        public const string CloseFillStroke = "b";
        /// <summary><c>b*</c> — close, fill, then stroke, even-odd.</summary>
        public const string CloseFillStrokeEvenOdd = "b*";
        /// <summary><c>n</c> — end the path without painting (applies any pending clip).</summary>
        public const string EndPath = "n";

        // ---- Clipping ----
        /// <summary><c>W</c> — intersect the clip with the current path, nonzero winding.</summary>
        public const string Clip = "W";
        /// <summary><c>W*</c> — intersect the clip with the current path, even-odd.</summary>
        public const string ClipEvenOdd = "W*";

        // ---- Colour ----
        /// <summary><c>g</c> — set non-stroking DeviceGray colour.</summary>
        public const string SetGrayFill = "g";
        /// <summary><c>G</c> — set stroking DeviceGray colour.</summary>
        public const string SetGrayStroke = "G";
        /// <summary><c>rg</c> — set non-stroking DeviceRGB colour.</summary>
        public const string SetRgbFill = "rg";
        /// <summary><c>RG</c> — set stroking DeviceRGB colour.</summary>
        public const string SetRgbStroke = "RG";
        /// <summary><c>k</c> — set non-stroking DeviceCMYK colour.</summary>
        public const string SetCmykFill = "k";
        /// <summary><c>K</c> — set stroking DeviceCMYK colour.</summary>
        public const string SetCmykStroke = "K";
        /// <summary><c>cs</c> — set the non-stroking colour space.</summary>
        public const string SetColorSpaceFill = "cs";
        /// <summary><c>CS</c> — set the stroking colour space.</summary>
        public const string SetColorSpaceStroke = "CS";
        /// <summary><c>sc</c> — set non-stroking colour components.</summary>
        public const string SetColorFill = "sc";
        /// <summary><c>scn</c> — set non-stroking colour components, pattern-aware.</summary>
        public const string SetColorFillN = "scn";
        /// <summary><c>SC</c> — set stroking colour components.</summary>
        public const string SetColorStroke = "SC";
        /// <summary><c>SCN</c> — set stroking colour components, pattern-aware.</summary>
        public const string SetColorStrokeN = "SCN";

        // ---- Text ----
        /// <summary><c>BT</c> — begin a text object.</summary>
        public const string BeginText = "BT";
        /// <summary><c>ET</c> — end a text object.</summary>
        public const string EndText = "ET";
        /// <summary><c>Tf</c> — set font and size.</summary>
        public const string SetFont = "Tf";
        /// <summary><c>Td</c> — move to the start of the next line by an offset.</summary>
        public const string TextMove = "Td";
        /// <summary><c>TD</c> — as <see cref="TextMove"/>, and set leading to -ty.</summary>
        public const string TextMoveSetLeading = "TD";
        /// <summary><c>Tm</c> — set the text matrix and text line matrix.</summary>
        public const string SetTextMatrix = "Tm";
        /// <summary><c>T*</c> — move to the start of the next line.</summary>
        public const string NextLine = "T*";
        /// <summary><c>Tj</c> — show a text string.</summary>
        public const string ShowText = "Tj";
        /// <summary><c>TJ</c> — show text with individual glyph positioning.</summary>
        public const string ShowTextArray = "TJ";
        /// <summary><c>'</c> — move to the next line and show a string.</summary>
        public const string NextLineShowText = "'";
        /// <summary><c>"</c> — set word/char spacing, move to next line, show a string.</summary>
        public const string NextLineSetSpacingShowText = "\"";
        /// <summary><c>Tc</c> — set character spacing.</summary>
        public const string SetCharSpacing = "Tc";
        /// <summary><c>Tw</c> — set word spacing.</summary>
        public const string SetWordSpacing = "Tw";
        /// <summary><c>Tz</c> — set horizontal scaling, as a percentage.</summary>
        public const string SetHorizontalScaling = "Tz";
        /// <summary><c>TL</c> — set text leading.</summary>
        public const string SetLeading = "TL";
        /// <summary><c>Tr</c> — set the text rendering mode (see <c>TextRenderMode</c>).</summary>
        public const string SetTextRenderMode = "Tr";
        /// <summary><c>Ts</c> — set text rise.</summary>
        public const string SetTextRise = "Ts";

        // ---- XObject / images / shading ----
        /// <summary><c>Do</c> — invoke a named XObject.</summary>
        public const string InvokeXObject = "Do";
        /// <summary><c>BI</c> — begin an inline image.</summary>
        public const string BeginInlineImage = "BI";
        /// <summary><c>ID</c> — begin inline image data.</summary>
        public const string InlineImageData = "ID";
        /// <summary><c>EI</c> — end an inline image.</summary>
        public const string EndInlineImage = "EI";
        /// <summary><c>sh</c> — paint a shading pattern.</summary>
        public const string ShadingFill = "sh";

        // ---- Type 3 glyph metrics ----
        /// <summary><c>d0</c> — set glyph width only (setcharwidth).</summary>
        public const string SetCharWidth = "d0";
        /// <summary><c>d1</c> — set glyph width and bounding box (setcachedevice).</summary>
        public const string SetCacheDevice = "d1";

        // ---- Compatibility sections ----
        /// <summary><c>BX</c> — begin a compatibility section.</summary>
        public const string BeginCompat = "BX";
        /// <summary><c>EX</c> — end a compatibility section.</summary>
        public const string EndCompat = "EX";

        // ---- Marked content ----
        /// <summary><c>BMC</c> — begin a marked-content sequence.</summary>
        public const string BeginMarkedContent = "BMC";
        /// <summary><c>BDC</c> — begin a marked-content sequence with a property list.</summary>
        public const string BeginMarkedContentProps = "BDC";
        /// <summary><c>EMC</c> — end a marked-content sequence.</summary>
        public const string EndMarkedContent = "EMC";
        /// <summary><c>MP</c> — designate a marked-content point.</summary>
        public const string MarkedPoint = "MP";
        /// <summary><c>DP</c> — marked-content point with a property list.</summary>
        public const string MarkedPointProps = "DP";

        /// <summary>
        /// <c>PS</c> — legacy PDF 1.1 PostScript passthrough. Deprecated since PDF 1.2 and
        /// treated as a no-op; the operand has already been consumed by the parser.
        /// </summary>
        public const string PostScript = "PS";
    }
}
