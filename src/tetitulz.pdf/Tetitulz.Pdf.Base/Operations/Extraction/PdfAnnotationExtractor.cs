// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>One annotation, as an inventory reports it.</summary>
    public sealed class PdfAnnotationInfo
    {
        /// <summary>Zero-based index of the page it is on.</summary>
        public int PageIndex { get; internal set; }

        /// <summary>The resolved <c>/Subtype</c>, without the leading solidus. Never null.</summary>
        public string Subtype { get; internal set; }

        /// <summary>Left edge of the normalised <c>/Rect</c>, in points from the page origin.</summary>
        public double X { get; internal set; }

        /// <summary>Bottom edge of the normalised <c>/Rect</c>.</summary>
        public double Y { get; internal set; }

        /// <summary>Width of the normalised <c>/Rect</c>; never negative.</summary>
        public double Width { get; internal set; }

        /// <summary>Height of the normalised <c>/Rect</c>; never negative.</summary>
        public double Height { get; internal set; }

        /// <summary><c>/Contents</c> — the note text — or null.</summary>
        public string Contents { get; internal set; }

        /// <summary><c>/T</c> — the annotation's author — or null.</summary>
        public string Author { get; internal set; }

        /// <summary><c>/M</c> — the modification date, as the file spells it — or null.</summary>
        public string ModifiedDate { get; internal set; }

        /// <summary><c>/NM</c> — the annotation name, the only stable identifier across revisions.</summary>
        public string Name { get; internal set; }

        /// <summary><c>/F</c> — the §12.5.3 flag bits. 0 when absent, which is the spec's default.</summary>
        public int Flags { get; internal set; }

        /// <summary>
        /// The <c>/NM</c> of the annotation this one replies to (<c>/IRT</c>, Table 170), or null when
        /// it replies to none, or to one that carries no name — a reply to an unnamed comment is
        /// listed on its own, since nothing else identifies the comment across revisions.
        /// </summary>
        public string InReplyToName { get; internal set; }

        /// <summary>
        /// The reply's relationship to that annotation (<c>/RT</c>, Table 170): <c>R</c>, a reply, the
        /// default, or <c>Group</c>, a member of the annotation's group; null when it replies to none.
        /// </summary>
        public string ReplyType { get; internal set; }

        /// <summary>
        /// Whether the annotation carries an <c>/AP</c> <c>/N</c> appearance the file supplies, as
        /// opposed to one a viewer must synthesise.
        /// </summary>
        public bool HasAppearance { get; internal set; }
    }

    /// <summary>
    /// Lists every annotation in a document — "what is in this file, and what could I remove?"
    /// </summary>
    /// <remarks>
    /// <para><b>It reuses <see cref="AnnotationSubtypeWalk"/> rather than adding a second walk.</b>
    /// That type's remarks record the same defect being found in one copy of this loop and fixed
    /// there, with a comment as the only thing connecting it to its twin. An inventory is the fifth
    /// place that would have wanted the loop.</para>
    ///
    /// <para><b>Everything is read through <see cref="PdfResolve"/>.</b> <c>PdfDictionary</c>'s
    /// indexer does not follow an indirect reference, and §7.3.10 permits one for essentially any
    /// value — a producer that shares one <c>/Subtype</c> name object across a thousand annotations
    /// is writing perfectly legal PDF, and a raw read reports every one of them as having no
    /// subtype.</para>
    ///
    /// <para><b>The <c>/Rect</c> is normalised.</b> §7.9.5 lets either corner pair come first, so a
    /// raw copy yields negative extents on files that are entirely valid.</para>
    /// </remarks>
    public static class PdfAnnotationExtractor
    {
        /// <summary>
        /// The default ceiling on annotations examined per page. The ceiling applied is
        /// <see cref="PdfLimits.MaxAnnotationsPerPage"/>, which a host can set and which
        /// <c>PdfLinkExtractor</c> reads too.
        /// </summary>
        /// <remarks>
        /// Kept as a constant because it is public API; the link extractor reads the same limit.
        /// </remarks>
        public const int MaxAnnotationsPerPage = PdfLimits.DefaultMaxAnnotationsPerPage;

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why the most recent listing <b>on this thread</b> is incomplete, or null when it is
        /// complete. Cleared at the start of every call.
        /// </summary>
        /// <remarks>
        /// An inventory that silently truncates is worse than one that refuses: a caller deciding
        /// what to remove would act on a list that omits the very annotations it does not know
        /// about.
        /// </remarks>
        public static string LastError => _lastError;

        /// <summary>Lists every annotation in the document.</summary>
        /// <param name="input">The PDF to read. Not disposed.</param>
        /// <remarks>Check <see cref="LastError"/> before treating the result as exhaustive.</remarks>
        public static IReadOnlyList<PdfAnnotationInfo> ListAnnotations(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            _lastError = null;
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the annotations", "PdfOperations.ListAnnotations(Stream, string)");
            return Collect(doc);
        }

        /// <summary>Lists every annotation in the document at <paramref name="path"/>.</summary>
        /// <param name="path">Path to the PDF.</param>
        /// <remarks>Check <see cref="LastError"/> before treating the result as exhaustive.</remarks>
        public static IReadOnlyList<PdfAnnotationInfo> ListAnnotations(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            _lastError = null;
            using var fs = File.OpenRead(path);
            using var doc = PdfDocument.Open(fs);
            EncryptedRead.Refuse(doc, "the annotations", "PdfOperations.ListAnnotations(Stream, string)");
            return Collect(doc);
        }

        internal static IReadOnlyList<PdfAnnotationInfo> Collect(PdfDocument doc)
        {
            var list = new List<PdfAnnotationInfo>();
            var perPage = new Dictionary<int, int>();
            long examined = 0;

            foreach (var (pageIndex, annot, subtype) in AnnotationSubtypeWalk.All(doc, new AnnotationWalkReport(m => _lastError ??= m)))
            {
                // Pages may name one /Annots array (PdfLimits.MaxAnnotationsPerDocument).
                if (++examined > PdfLimits.MaxAnnotationsPerDocument)
                {
                    _lastError =
                        $"the pages name more than {PdfLimits.MaxAnnotationsPerDocument} annotations between them; " +
                        $"only that many were examined, so the returned inventory is incomplete from page {pageIndex} on.";
                    break;
                }

                perPage.TryGetValue(pageIndex, out int seen);
                if (seen >= PdfLimits.MaxAnnotationsPerPage)
                {
                    _lastError =
                        $"page {pageIndex} carries more than {PdfLimits.MaxAnnotationsPerPage} annotations; " +
                        "only that many were examined, so the returned inventory is incomplete.";
                    continue;
                }
                perPage[pageIndex] = seen + 1;

                var info = new PdfAnnotationInfo
                {
                    PageIndex = pageIndex,
                    Subtype = subtype,
                    Contents = PdfResolve.Str(doc, annot, PdfName.Contents)?.GetText(),
                    Author = PdfResolve.Str(doc, annot, PdfName.T)?.GetText(),
                    ModifiedDate = PdfResolve.Str(doc, annot, PdfName.M)?.GetText(),
                    Name = PdfResolve.Str(doc, annot, PdfName.NM)?.GetText(),
                    Flags = PdfAnnotationFlags.FromNumber(PdfResolve.Number(doc, annot, PdfName.F)) ?? 0,
                    HasAppearance =
                        PdfResolve.Dict(doc, annot, PdfName.AP) is PdfDictionary ap &&
                        PdfResolve.Value(doc, ap[PdfName.N]) != null,
                };

                // One hop: the comment replied to, by the name it carries (its own replies are its own rows).
                if (PdfResolve.Dict(doc, annot, PdfName.IRT) is PdfDictionary repliedTo)
                {
                    info.InReplyToName = PdfResolve.Str(doc, repliedTo, PdfName.NM)?.GetText();
                    info.ReplyType = PdfResolve.Name(doc, annot, PdfName.RT)?.Value ?? "R";
                }

                var rect = PdfResolve.Array(doc, annot, PdfName.Rect);
                if (rect != null && rect.Count >= 4)
                {
                    // Per element: §7.3.10 lets an array element be indirect, and an unresolved one
                    // reads as 0 — a plausible coordinate rather than a detectable failure.
                    double a = PdfResolve.ArrayDouble(doc, rect, 0), b = PdfResolve.ArrayDouble(doc, rect, 1);
                    double c = PdfResolve.ArrayDouble(doc, rect, 2), d = PdfResolve.ArrayDouble(doc, rect, 3);
                    info.X = Math.Min(a, c);
                    info.Y = Math.Min(b, d);
                    info.Width = Math.Abs(c - a);
                    info.Height = Math.Abs(d - b);
                }

                list.Add(info);
            }

            return list;
        }
    }
}
