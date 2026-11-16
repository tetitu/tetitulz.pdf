// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>What the point given for a page's text is of its first line.</summary>
    public enum PageTextAnchor
    {
        /// <summary>The line's baseline: its letters stand on the point's height.</summary>
        Baseline,

        /// <summary>The line's top — its font's ascent: the text hangs below the point, as it does below a click.</summary>
        Top,
    }

    /// <summary>Text to draw on a page, and how: where, how large, in which font and colour.</summary>
    public sealed class PageTextOptions
    {
        /// <summary>
        /// The text, in the order it was typed (logical order). A line break starts a new line under
        /// the one before; a tab is drawn as a space.
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Where each line starts <b>as it is read</b>, in the page's own coordinates: the left end of
        /// a left-to-right line, the right end of a right-to-left one — the point a writer puts the
        /// pen down at.
        /// </summary>
        public double X { get; set; }

        /// <summary>The first line's height on the page, in the page's own coordinates: its baseline, or its top under <see cref="PageTextAnchor.Top"/>.</summary>
        public double Y { get; set; }

        /// <summary>What <see cref="Y"/> is of the first line: its baseline by default.</summary>
        public PageTextAnchor Anchor { get; set; } = PageTextAnchor.Baseline;

        /// <summary>The font size, in points.</summary>
        public double FontSize { get; set; } = 12;

        /// <summary>The distance from one baseline to the next, as a multiple of the font size.</summary>
        public double LineSpacing { get; set; } = 1.2;

        /// <summary>The family to draw in, as a hint: a face that cannot draw the text is not taken for its name.</summary>
        public string FontFamily { get; set; } = "Arial";

        /// <summary>Prefer a bold face.</summary>
        public bool Bold { get; set; }

        /// <summary>Prefer an italic face.</summary>
        public bool Italic { get; set; }

        /// <summary>The colour's red, 0 to 1.</summary>
        public double Red { get; set; }

        /// <summary>The colour's green, 0 to 1.</summary>
        public double Green { get; set; }

        /// <summary>The colour's blue, 0 to 1.</summary>
        public double Blue { get; set; }

        /// <summary>
        /// The direction each line is laid out in. <see cref="BaseDirection.Auto"/> takes it from the
        /// line's first strong character, which is wrong for a Hebrew line that opens with a number
        /// or a Latin word — say the direction there.
        /// </summary>
        public BaseDirection Direction { get; set; } = BaseDirection.Auto;

        /// <summary>
        /// Draw the text upright as the page is <b>shown</b>: on a page with a <c>/Rotate</c> the
        /// lines run across the page the reader sees, not across the page's own x axis. On by
        /// default; off, the baseline runs along the page's x axis whatever the page's turn.
        /// </summary>
        public bool UprightAsShown { get; set; } = true;

        /// <summary>
        /// Supplies the font program, given <see cref="FontFamily"/> as a hint; return null to
        /// decline. Embedding redistributes a font, so the caller that knows its licence supplies it.
        /// </summary>
        public Func<string, byte[]> FontProvider { get; set; }

        /// <summary>
        /// Whether a font installed on this machine may be embedded when <see cref="FontProvider"/>
        /// supplies none. Off by default, for the licensing reason on
        /// <see cref="FontResolutionRequest.AllowSystemFontSubstitution"/>.
        /// </summary>
        public bool AllowSystemFontSubstitution { get; set; }

        /// <summary>Consider an installed font whose own licence bits forbid embedding; see <see cref="FontResolutionRequest.AllowRestrictedSystemFonts"/>.</summary>
        public bool AllowRestrictedSystemFonts { get; set; }
    }

    /// <summary>What <see cref="PdfPageTextWriter.AddText"/> drew.</summary>
    public sealed class PageTextResult
    {
        internal PageTextResult(double x, double y, double width, double height, int lines, string fontName, bool fontFromSystem)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Lines = lines;
            FontName = fontName;
            FontFromSystem = fontFromSystem;
        }

        /// <summary>The left of the box the text was drawn in, in the page's own coordinates.</summary>
        public double X { get; }

        /// <summary>The box's bottom.</summary>
        public double Y { get; }

        /// <summary>The box's width.</summary>
        public double Width { get; }

        /// <summary>The box's height: from the last line's descent to the first line's ascent.</summary>
        public double Height { get; }

        /// <summary>How many lines were drawn; a blank line between two is counted.</summary>
        public int Lines { get; }

        /// <summary>The PostScript name of the font the text was drawn in.</summary>
        public string FontName { get; }

        /// <summary>True when the font came from this machine and not from the caller's provider.</summary>
        public bool FontFromSystem { get; }
    }

    /// <summary>
    /// Draws text on an existing page, as one appended revision: any script the font covers, shaped
    /// and laid out in its own direction, in a font embedded with the glyphs drawn.
    /// </summary>
    /// <remarks>
    /// <para><b>The text is the page's own content afterwards</b> — a text object in a content
    /// stream appended to the page, drawn in the page's initial graphics state — so a reader
    /// extracts it, searches it and prints it as it does the rest of the page, and
    /// <see cref="PdfPageElements"/> lists each line as an element like any other. It is not an
    /// annotation.</para>
    ///
    /// <para><b>The font is always embedded</b>, a subset holding the glyphs drawn, so the text
    /// looks the same in every reader and its widths are the file's own. With no font program to
    /// embed — no <see cref="PageTextOptions.FontProvider"/>, and installed fonts not allowed — or
    /// with a character the font found cannot draw, nothing is written and the refusal names the
    /// reason: a line with a letter missing is worse than no line.</para>
    ///
    /// <para><b>A revision is appended</b>, so what was in the file stays byte for byte; a
    /// signature over it still covers what it covered, and a reader reports the page as changed
    /// since. An encrypted document is refused.</para>
    /// </remarks>
    public static class PdfPageTextWriter
    {
        /// <summary>The longest text taken, in characters: a page holds no more.</summary>
        public const int MaxTextLength = 100_000;

        /// <summary>
        /// Writes <paramref name="input"/> to <paramref name="output"/> with
        /// <paramref name="options"/>' text drawn on page <paramref name="pageIndex"/>.
        /// </summary>
        /// <param name="input">The document. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="pageIndex">The zero-based page.</param>
        /// <param name="options">The text and how it is drawn.</param>
        /// <returns>The box the text was drawn in and the font it was drawn with.</returns>
        /// <exception cref="ArgumentException">The text is empty or longer than <see cref="MaxTextLength"/>, or a number is not one.</exception>
        /// <exception cref="PdfException">No font can be embedded for the text, or the font found lacks one of its characters; nothing is written.</exception>
        /// <exception cref="PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static PageTextResult AddText(Stream input, Stream output, int pageIndex, PageTextOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (options == null) throw new ArgumentNullException(nameof(options));

            NumericArguments.RequireFinite(options.X, nameof(options), nameof(PageTextOptions.X));
            NumericArguments.RequireFinite(options.Y, nameof(options), nameof(PageTextOptions.Y));
            NumericArguments.RequirePositive(options.FontSize, nameof(options), nameof(PageTextOptions.FontSize));
            NumericArguments.RequirePositive(options.LineSpacing, nameof(options), nameof(PageTextOptions.LineSpacing));
            NumericArguments.RequireFinite(options.Red, nameof(options), nameof(PageTextOptions.Red));
            NumericArguments.RequireFinite(options.Green, nameof(options), nameof(PageTextOptions.Green));
            NumericArguments.RequireFinite(options.Blue, nameof(options), nameof(PageTextOptions.Blue));

            string text = options.Text ?? "";
            if (text.Length > MaxTextLength)
                throw new ArgumentException(
                    string.Format(CultureInfo.InvariantCulture, "The text is {0} characters; at most {1} are drawn on a page.", text.Length, MaxTextLength),
                    nameof(options));
            // A line break at the end starts no line.
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').TrimEnd('\n').Split('\n');
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("There is no text to draw.", nameof(options));

            var ms = PdfHelpers.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "add text to", "the text");
            PdfHelpers.ValidatePageIndex(doc, pageIndex);
            PdfPage page = doc.Pages[pageIndex];

            // ---- The font: one program that draws every line. ----
            FontResolution resolution = SystemFontResolver.Resolve(
                new FontResolutionRequest
                {
                    FamilyHint = options.FontFamily,
                    Bold = options.Bold,
                    Italic = options.Italic,
                    // The shaped text's code points, as every producer asks: a joined Arabic letter is
                    // covered by the form the font reaches, not by the letter alone.
                    RequiredCodePoints = TextRunComposer.CodePointsToCover(string.Concat(lines)),
                    AllowSystemFontSubstitution = options.AllowSystemFontSubstitution,
                    AllowRestrictedSystemFonts = options.AllowRestrictedSystemFonts,
                },
                options.FontProvider);
            if (!resolution.Success)
                throw new PdfException(
                    (options.AllowSystemFontSubstitution
                        ? "No font here can draw the text, so nothing was added (set PageTextOptions.FontProvider to supply one)"
                        : "No font program was available to embed for the text, so nothing was added (set " +
                          "PageTextOptions.FontProvider, or AllowSystemFontSubstitution to use an installed font)")
                    + ": " + resolution.FailureReason);
            TrueTypeFontProgram program = resolution.Program;

            // ---- The lines, shaped: each once, in its own direction. ----
            var gidToUnicode = new Dictionary<ushort, int>();
            var gidToText = new Dictionary<ushort, string>();
            var runs = new ShapedGlyphRun[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                ShapedGlyphRun run = TextRunComposer.Compose(lines[i], program, new TextRunOptions
                {
                    BaseDirection = options.Direction,
                    MissingGlyphPolicy = MissingGlyphPolicy.Refuse,
                });
                if (!run.IsComplete)
                    throw new PdfException(string.Format(CultureInfo.InvariantCulture,
                        "The font {0} has no glyph for U+{1:X4}, so nothing was added.", program.PostScriptName, run.MissingCodePoint.Value));
                run.RecordUsedGlyphs(gidToUnicode, gidToText);
                runs[i] = run;
            }

            // ---- Where: along the line and up from it, as the page is shown. ----
            int rotation = options.UprightAsShown ? PdfPageRotation.Normalize(page.Rotate, out _) : 0;
            (double alongX, double alongY, double upX, double upY) = rotation switch
            {
                90 => (0.0, 1.0, -1.0, 0.0),
                180 => (-1.0, 0.0, 0.0, -1.0),
                270 => (0.0, -1.0, 1.0, 0.0),
                _ => (1.0, 0.0, 0.0, 1.0),
            };
            double size = options.FontSize, leading = size * options.LineSpacing;
            // The program gives its ascent and descent in glyph space, a thousand to the em, whatever its own units.
            double ascent = program.Ascent / Core.Constants.PdfUnits.GlyphSpaceUnitsPerEm * size,
                   descent = program.Descent / Core.Constants.PdfUnits.GlyphSpaceUnitsPerEm * size;
            // Under a point that is the first line's top, every baseline is an ascent lower, as shown.
            double drop = options.Anchor == PageTextAnchor.Top ? ascent : 0;

            var starts = new (double X, double Y)[lines.Length];
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            void Reach(double along, double up, int line)
            {
                double x = starts[line].X + alongX * along + upX * up, y = starts[line].Y + alongY * along + upY * up;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            for (int i = 0; i < lines.Length; i++)
            {
                double width = runs[i]?.WidthAt(size) ?? 0;
                // A right-to-left line is written towards the left of where it starts.
                double back = runs[i] != null && runs[i].IsRightToLeft ? width : 0;
                starts[i] = (options.X - upX * (leading * i + drop) - alongX * back, options.Y - upY * (leading * i + drop) - alongY * back);
                Reach(0, descent, i);
                Reach(width, ascent, i);
            }

            // ---- The revision: the font once, the page's content with the text after it. ----
            var objects = new List<IndirectObject>();
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var (type0ObjNum, fontObjects) = Type0FontBuilder.BuildObjects(ref nextObjNum, program, gidToUnicode, gidToText);
            foreach (var (objNum, obj) in fontObjects) objects.Add(new IndirectObject(objNum, 0, obj));

            // A resource name the page does not use, so the page's own font of that name stays the page's.
            string fontName = PdfPageContentAppender.FreeResourceName(page.Resources, doc, "TF", 1);

            PdfPageContentAppender.AppendToPage(page, builder =>
            {
                builder.SaveState();
                builder.SetFillColorRgb((float)Clamp01(options.Red), (float)Clamp01(options.Green), (float)Clamp01(options.Blue));
                builder.BeginText();
                builder.UseEmbeddedFont(fontName);
                builder.SetFont(fontName, (float)size);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (runs[i] == null) continue;
                    builder.SetTextMatrix(alongX, alongY, upX, upY, starts[i].X, starts[i].Y);
                    // The one way a composed run is written: its marks keep the places the font gives them.
                    builder.AppendRaw(GlyphRunShowText.Operators(runs[i], size) + "\n");
                }
                builder.EndText();
                builder.RestoreState();
            }, compressStream: true, ref nextObjNum, objects, new Dictionary<string, int> { [fontName] = type0ObjNum });

            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
            return new PageTextResult(minX, minY, maxX - minX, maxY - minY, lines.Length, program.PostScriptName, resolution.FromSystem);
        }

        private static double Clamp01(double channel) => channel < 0 ? 0 : channel > 1 ? 1 : channel;
    }
}
