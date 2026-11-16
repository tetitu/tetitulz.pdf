// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>Policy for <see cref="PdfBatesNumbering"/>.</summary>
    public sealed class BatesNumberingOptions
    {
        /// <summary>Text before the number, e.g. an exhibit or matter code.</summary>
        public string Prefix { get; set; } = "";

        /// <summary>Text after the number.</summary>
        public string Suffix { get; set; } = "";

        /// <summary>
        /// Minimum digits, zero-padded. Defaults to 6, which is the width legal bundles use.
        /// </summary>
        /// <remarks>
        /// <b>A pad width is not a cap.</b> A number wider than this is written in full rather than
        /// truncated: a stamp exists to identify a page uniquely, and a truncated one identifies the
        /// wrong page while looking correct. Set 0 for no padding.
        /// </remarks>
        public int Digits { get; set; } = 6;

        /// <summary>The number given to the first page of the first document. Defaults to 1.</summary>
        public long StartNumber { get; set; } = 1;

        /// <summary>Stamp at the top of the page rather than the bottom. Off by default.</summary>
        public bool InHeader { get; set; }

        /// <summary>Standard-14 font name. Defaults to Helvetica.</summary>
        public string FontName { get; set; } = StandardFonts.Helvetica;

        /// <summary>Point size. Defaults to 10.</summary>
        public double FontSize { get; set; } = 10;

        /// <summary>Distance from the page edge, in points. Defaults to 36 (half an inch).</summary>
        public double MarginPt { get; set; } = 36;

        /// <summary>Which edge the stamp is aligned to. Defaults to the right.</summary>
        public HorizontalAlignment HorizontalAlignment { get; set; } = HorizontalAlignment.Right;

        /// <summary>Fill red channel, 0..1. Defaults to black.</summary>
        public double ColorR { get; set; }

        /// <summary>Fill green channel, 0..1. Defaults to black.</summary>
        public double ColorG { get; set; }

        /// <summary>Fill blue channel, 0..1. Defaults to black.</summary>
        public double ColorB { get; set; }
    }

    /// <summary>
    /// Bates numbering: one running number stamped across a set of documents, and removable
    /// afterwards.
    /// </summary>
    /// <remarks>
    /// <para><b>What this adds over <see cref="PdfHeaderFooterWriter"/> is the two things a bundle
    /// needs and a header does not</b>: a sequence that <i>continues</i> from one document into the
    /// next, and a stamp that can be taken off again. A bundle is renumbered when a document is
    /// added or withdrawn, and re-stamping over the old numbers is how a page ends up carrying
    /// two.</para>
    ///
    /// <para><b>The sequence state crosses the boundary as a number, not as an object.</b>
    /// <see cref="Apply(Stream, Stream, BatesNumberingOptions)"/> returns the next unused number, so
    /// a caller stamping documents one at a time — from a queue, across a request, in whatever order
    /// their workflow produces them — carries the state themselves. The list overload does the
    /// chaining for the ordinary case.</para>
    ///
    /// <para><b>Each stamp is its own content stream, marked as a pagination artifact and carrying
    /// a comment that names it.</b> Both halves are load-bearing. The artifact marking is what
    /// §14.8.2.2 requires of a page number — it is not document content, and a screen reader must
    /// not read it as though it were, which is a defect the plain header/footer writer has. The
    /// comment is what <see cref="Remove"/> matches on, and it is a comment rather than a
    /// dictionary key because a content stream has nowhere else to carry one.</para>
    ///
    /// <para><b>Removal unlinks, it does not erase.</b> The stamp streams stay in the file as
    /// unreachable objects, because an incremental update cannot delete — the same trade
    /// <c>PdfImageReplacer</c> makes and <c>PdfRedactor</c> refuses. That is right here: a Bates
    /// number is an index, not a secret. A caller who needs the bytes gone should rewrite the
    /// document afterwards.</para>
    /// </remarks>
    public static class PdfBatesNumbering
    {
        /// <summary>
        /// The first line of every stamp stream this class writes, and what <see cref="Remove"/>
        /// looks for.
        /// </summary>
        /// <remarks>
        /// A content-stream comment (§7.2.4): ignored by every interpreter, preserved by every
        /// writer that does not rewrite content. <b>An optimiser that strips comments takes this
        /// with it</b>, and then the stamp is ordinary content that cannot be identified — which is
        /// stated rather than guarded against, because the alternative is a marker no reader ignores.
        /// </remarks>
        internal const string StampMarker = "% Tetitulz.Pdf Bates stamp";

        /// <summary>
        /// Stamps every page of one document and reports the next unused number.
        /// </summary>
        /// <param name="input">The PDF to stamp. Not disposed.</param>
        /// <param name="output">Receives the stamped PDF. Not disposed.</param>
        /// <param name="options">Numbering and appearance; null uses the defaults.</param>
        /// <returns>
        /// The number the <i>next</i> document should start at — this document's start plus its page
        /// count. Feed it back as <see cref="BatesNumberingOptions.StartNumber"/> to continue.
        /// </returns>
        public static long Apply(Stream input, Stream output, BatesNumberingOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            options ??= new BatesNumberingOptions();
            Validate(options);

            long next = options.StartNumber;

            PdfPageContentAppender.AppendContentToAllPages(
                input, output,
                (builder, page) =>
                {
                    // Derived from the page INDEX rather than from a counter incremented per call:
                    // the number a page carries must be a function of where the page is, not of the
                    // order the appender happened to visit it in.
                    long number = options.StartNumber + page.PageIndex;
                    if (number >= next) next = number + 1;
                    BuildStamp(builder, page, options, number);
                },
                compressStream: false);

            return next;
        }

        /// <summary>
        /// Stamps a whole bundle with one running sequence.
        /// </summary>
        /// <param name="inputs">The documents, in bundle order. None disposed.</param>
        /// <param name="outputs">Receives each stamped document, positionally. None disposed.</param>
        /// <param name="options">Numbering and appearance; null uses the defaults.</param>
        /// <returns>The next unused number, so a later addition can continue the bundle.</returns>
        /// <exception cref="ArgumentException">The two lists are of different lengths.</exception>
        public static long Apply(
            IReadOnlyList<Stream> inputs, IReadOnlyList<Stream> outputs,
            BatesNumberingOptions options = null)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (outputs == null) throw new ArgumentNullException(nameof(outputs));
            if (inputs.Count != outputs.Count)
                throw new ArgumentException(
                    $"{inputs.Count} input(s) and {outputs.Count} output(s): a bundle is stamped " +
                    "positionally, so one output per input is required.", nameof(outputs));

            options ??= new BatesNumberingOptions();
            // Not validated here: the per-document Apply below checks the options at its own entry,
            // before it reads its input, so a bad option is refused before the first document is
            // touched — a second check here would do nothing.
            long next = options.StartNumber;

            for (int i = 0; i < inputs.Count; i++)
            {
                var perDocument = Clone(options);
                perDocument.StartNumber = next;
                next = Apply(inputs[i], outputs[i], perDocument);
            }

            return next;
        }

        /// <summary>
        /// Removes the stamps this class applied, and reports how many pages lost one.
        /// </summary>
        /// <param name="input">The stamped PDF. Not disposed.</param>
        /// <param name="output">Receives the result. Not disposed.</param>
        /// <returns>
        /// How many stamps were unlinked. <b>0 means the document carried none</b> — a document
        /// stamped by other software, or one whose content streams have since been rewritten,
        /// is indistinguishable from an unstamped one here and is left alone rather than guessed at.
        /// </returns>
        /// <remarks>
        /// Nothing is written when nothing matched: appending a revision that changes no object
        /// would grow the file and move its <c>startxref</c> to say that a stamp was removed.
        /// </remarks>
        public static int Remove(Stream input, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var source = Core.IO.SeekableStream.Ensure(input, rewind: true, out bool owned);
            try
            {
                source.Position = 0;
                using var doc = PdfDocument.Open(source);

                var updated = new List<IndirectObject>();
                int removed = 0;

                foreach (var page in doc.Pages)
                {
                    PdfDictionary dictionary = page.Dictionary;
                    if (!(doc.ResolveObject(dictionary[PdfName.Contents]) is PdfArray contents))
                        continue;

                    var kept = new PdfArray();
                    int removedHere = 0;
                    for (int i = 0; i < contents.Count; i++)
                    {
                        if (IsStamp(doc, contents[i])) { removedHere++; continue; }
                        kept.Add(contents[i]);
                    }

                    if (removedHere == 0) continue;
                    removed += removedHere;

                    var replacement = PdfDictionary.CloneShallow(dictionary);
                    replacement.Set(PdfName.Contents, kept);
                    replacement.ObjectNumber = dictionary.ObjectNumber;
                    replacement.GenerationNumber = 0;
                    updated.Add(new IndirectObject(
                        dictionary.ObjectNumber, 0, replacement));
                }

                if (removed == 0)
                {
                    source.Position = 0;
                    source.CopyTo(output);
                    return 0;
                }

                PdfPageContentAppender.AppendRevision(
                    source, output, doc, updated,
                    doc.Parser.CrossReferenceTable.GetMaxObjectNumber() + 1);
                return removed;
            }
            finally
            {
                if (owned) source.Dispose();
            }
        }

        /// <summary>The text a given number is stamped as.</summary>
        /// <remarks>
        /// Public because a caller building an index of the bundle needs the same string this wrote,
        /// and re-deriving it from the prefix and pad width is how the index and the pages drift.
        /// </remarks>
        public static string Format(long number, BatesNumberingOptions options)
        {
            options ??= new BatesNumberingOptions();
            Validate(options);
            int digits = options.Digits;

            string body = number.ToString(CultureInfo.InvariantCulture);
            if (digits > 0 && body.Length < digits)
                body = body.PadLeft(digits, '0');

            return (options.Prefix ?? "") + body + (options.Suffix ?? "");
        }

        /// <summary>
        /// The widest pad <see cref="BatesNumberingOptions.Digits"/> accepts. A pad is a string
        /// allocation per stamp, and unbounded, <c>Digits = int.MaxValue</c> would ask
        /// <c>PadLeft</c> for a two-gigabyte label and die of memory. Thirty-two covers every
        /// legal bundle (a <c>long</c> has nineteen digits).
        /// </summary>
        public const int MaxDigits = 32;

        /// <summary>
        /// The options a caller's mistake can turn into a crash or a blank stamp, refused at the
        /// entry: a pad past <see cref="MaxDigits"/> or below 0, a non-positive or non-finite
        /// font size (a NaN size wrote <c>0 Tf</c> and no visible stamp), a negative or non-finite
        /// margin.
        /// </summary>
        private static void Validate(BatesNumberingOptions options)
        {
            if (options.Digits < 0 || options.Digits > MaxDigits)
                throw new ArgumentOutOfRangeException(nameof(options), options.Digits,
                    $"options.Digits must be between 0 and {MaxDigits}.");
            NumericArguments.RequirePositive(options.FontSize, nameof(options), nameof(options.FontSize));
            NumericArguments.RequireNonNegative(options.MarginPt, nameof(options), nameof(options.MarginPt));
        }

        // ----------------------------------------------------------------

        private static void BuildStamp(
            PdfContentStreamBuilder builder, PdfPageInfo page,
            BatesNumberingOptions options, long number)
        {
            string text = Format(number, options);
            string fontResource = builder.AddStandardFont(options.FontName);

            builder.AppendRaw(StampMarker);

            builder.SaveState();
            // §14.8.2.2: a page number is a pagination artifact, not document content. Unmarked, a
            // screen reader announces it in the middle of the text.
            builder.BeginArtifact(PdfArtifactTypes.Pagination,
                                  options.InHeader ? PdfArtifactSubtypes.Header
                                                   : PdfArtifactSubtypes.Footer);

            builder.SetFillColorRgb((float)options.ColorR, (float)options.ColorG,
                                    (float)options.ColorB);

            // Placed on the page as shown — its box's corner and its /Rotate included — through the
            // one placement the header/footer writer uses.
            double y = options.InHeader
                ? page.ShownHeight - options.MarginPt - options.FontSize
                : options.MarginPt;

            builder.BeginText();
            builder.SetFont(fontResource, (float)options.FontSize);
            PdfHeaderFooterWriter.PositionAsShown(builder, page, PdfHeaderFooterWriter.GetAlignedX(
                options.HorizontalAlignment, options.FontName, options.MarginPt, page.ShownWidth, options.FontSize, text), y);
            builder.ShowText(text);
            builder.EndText();

            builder.EndMarkedContent();
            builder.RestoreState();
        }

        /// <summary>Whether a <c>/Contents</c> entry is one of this class's stamps.</summary>
        private static bool IsStamp(PdfDocument doc, PdfObject entry)
        {
            if (!(doc.ResolveObject(entry) is PdfStream stream)) return false;

            byte[] data;
            try { data = stream.GetDecodedData(); }
            catch { return false; }   // an undecodable stream is not one this class wrote

            if (data == null || data.Length < StampMarker.Length) return false;

            // The marker is the FIRST thing in the stream, so a document whose content happens to
            // contain the same comment further in is not mistaken for a stamp.
            for (int i = 0; i < StampMarker.Length; i++)
                if (data[i] != (byte)StampMarker[i]) return false;

            return true;
        }

        private static BatesNumberingOptions Clone(BatesNumberingOptions source) =>
            new BatesNumberingOptions
            {
                Prefix = source.Prefix,
                Suffix = source.Suffix,
                Digits = source.Digits,
                StartNumber = source.StartNumber,
                InHeader = source.InHeader,
                FontName = source.FontName,
                FontSize = source.FontSize,
                MarginPt = source.MarginPt,
                HorizontalAlignment = source.HorizontalAlignment,
                ColorR = source.ColorR,
                ColorG = source.ColorG,
                ColorB = source.ColorB,
            };
    }
}
