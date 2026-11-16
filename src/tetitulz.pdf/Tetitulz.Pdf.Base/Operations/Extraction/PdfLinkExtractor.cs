// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// Classifies the navigation target of a PDF /Link annotation.
    /// </summary>
    public enum PdfLinkKind
    {
        /// <summary>The link has no recognised /A action or /Dest target.</summary>
        Unknown = 0,
        /// <summary>An external URI (web/mailto/…) via a /URI action.</summary>
        Uri,
        /// <summary>An in-document jump (a /GoTo action or a direct /Dest).</summary>
        GoTo,
        /// <summary>A /Named action (§12.6.4.11) — <see cref="PdfLink.NamedAction"/> says which.</summary>
        Named = 3,
        /// <summary>A jump into another PDF file (/GoToR) — <see cref="PdfLink.File"/> names it.</summary>
        RemoteGoTo = 4,
        /// <summary>A /Launch action: a file or program to open — <see cref="PdfLink.File"/> names it.</summary>
        Launch = 5,
    }

    /// <summary>
    /// A single clickable /Link annotation: its hit rectangle (in PDF user space,
    /// origin bottom-left, points) plus the resolved navigation target.
    /// </summary>
    public sealed class PdfLink
    {
        /// <summary>Zero-based index of the page the link lives on.</summary>
        public int PageIndex { get; }

        /// <summary>Left edge of the hit rectangle, in PDF points.</summary>
        public float X { get; }
        /// <summary>Bottom edge of the hit rectangle, in PDF points (origin bottom-left).</summary>
        public float Y { get; }
        /// <summary>Width of the hit rectangle, in PDF points.</summary>
        public float Width { get; }
        /// <summary>Height of the hit rectangle, in PDF points.</summary>
        public float Height { get; }

        /// <summary>What kind of target the link points at.</summary>
        public PdfLinkKind Kind { get; }

        /// <summary>The destination URI for <see cref="PdfLinkKind.Uri"/> links; otherwise null.</summary>
        public string Uri { get; }

        /// <summary>
        /// Zero-based destination page for <see cref="PdfLinkKind.GoTo"/> links, or -1 when the
        /// destination could not be resolved (an unknown named destination). For a
        /// <see cref="PdfLinkKind.RemoteGoTo"/> link, the page number its explicit destination names
        /// in the other file (§12.6.4.3), as written — that file's page count is not known here — or
        /// -1 for a named destination, which only the other file can resolve.
        /// </summary>
        public int TargetPageIndex { get; }

        /// <summary>
        /// For a <see cref="PdfLinkKind.GoTo"/> or <see cref="PdfLinkKind.RemoteGoTo"/> link, the
        /// user-space y its destination puts at the top of the view (<c>/XYZ</c>, <c>/FitH</c>,
        /// <c>/FitBH</c>, <c>/FitR</c>), named destinations included for a local link; null when the
        /// destination names no top — the page's top is then meant.
        /// </summary>
        public float? TargetTop { get; }

        /// <summary>The action's name (<c>NextPage</c>, <c>PrevPage</c>, <c>FirstPage</c>, <c>LastPage</c>, …) for <see cref="PdfLinkKind.Named"/>; otherwise null.</summary>
        public string NamedAction { get; }

        /// <summary>The file a <see cref="PdfLinkKind.RemoteGoTo"/> or <see cref="PdfLinkKind.Launch"/> link names; otherwise null.</summary>
        public string File { get; }

        internal PdfLink(int pageIndex, float x, float y, float width, float height,
                         PdfLinkKind kind, string uri, int targetPageIndex,
                         float? targetTop = null, string namedAction = null, string file = null)
        {
            PageIndex = pageIndex;
            X = x; Y = y; Width = width; Height = height;
            Kind = kind;
            Uri = uri;
            TargetPageIndex = targetPageIndex;
            TargetTop = targetTop;
            NamedAction = namedAction;
            File = file;
        }
    }

    /// <summary>
    /// Extracts the clickable /Link annotations from a PDF together with their
    /// hit rectangles and resolved navigation targets (URI or in-document page).
    /// This is the read-side counterpart to <see cref="PdfAnnotationWriter.AddUriLink"/>.
    /// </summary>
    public static class PdfLinkExtractor
    {
        // The per-page ceiling is PdfLimits.MaxAnnotationsPerPage, shared with PdfAnnotationExtractor.
        // It bounds volume on an already-parsed array, so a page that trips it is far more likely a
        // legitimately huge generated one (an index, a map overlay) than an adversarial one, and the
        // truncation is reported through LastError rather than treated as an attack.

        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why the most recent extraction on this thread returned an incomplete list, or
        /// <c>null</c> when every annotation on every page was examined.
        /// </summary>
        /// <remarks>
        /// A truncated result is byte-identical in shape to a complete one, so a link checker or
        /// a viewer building a hit-test index would treat the dropped links as non-existent. Reset
        /// at the start of both <c>ExtractLinks</c> overloads; read it on the calling thread (see
        /// the Diagnostics Pattern in CLAUDE.md).
        /// </remarks>
        public static string LastError => _lastError;

        /// <summary>
        /// Returns every /Link annotation in the document. Coordinates are in PDF
        /// user space (points, origin bottom-left). Returns an empty list when the
        /// document has no link annotations.
        /// <para>Check <see cref="LastError"/>: a page carrying more than
        /// <see cref="PdfLimits.MaxAnnotationsPerPage"/> annotations (50 000 by default) is
        /// truncated, and the returned list is then incomplete.</para>
        /// </summary>
        public static IReadOnlyList<PdfLink> ExtractLinks(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            _lastError = null;
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "the links", "PdfOperations.ExtractLinks(Stream, string)");
            return Collect(doc);
        }

        /// <summary>
        /// Returns every /Link annotation in the document at <paramref name="path"/>.
        /// <para>Check <see cref="LastError"/> — see the stream overload.</para>
        /// </summary>
        public static IReadOnlyList<PdfLink> ExtractLinks(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            _lastError = null;
            using var doc = PdfDocument.Open(path);
            EncryptedRead.Refuse(doc, "the links", "PdfOperations.ExtractLinks(Stream, string)");
            return Collect(doc);
        }

        internal static IReadOnlyList<PdfLink> Collect(PdfDocument doc)
        {
            var result = new List<PdfLink>();
            // Lazily built name → destination map (only when a named dest is hit).
            Dictionary<string, NamedDestination> namedDests = null;

            var pages = doc.Pages;
            long documentLeft = PdfLimits.MaxAnnotationsPerDocument;
            bool documentCut = false;
            for (int p = 0; p < pages.Count; p++)
            {
                var annots = pages[p].GetAnnotations();
                if (annots == null || annots.Count == 0) continue;

                // Pages may name one /Annots array, so the entries a document's pages name are
                // bounded together (PdfLimits.MaxAnnotationsPerDocument).
                if (documentLeft <= 0)
                {
                    if (!documentCut) _lastError = DocumentCeilingReached(p);
                    break;
                }

                int ceiling = PdfLimits.MaxAnnotationsPerPage;
                int count = Math.Min(annots.Count, ceiling);
                if (count < annots.Count)
                    _lastError =
                        $"Page {p} carries {annots.Count} annotations; only the first " +
                        $"{ceiling} were examined, so the returned links are " +
                        "incomplete for that page.";
                if (count > documentLeft)
                {
                    count = (int)documentLeft;
                    _lastError = DocumentCeilingReached(p);
                    documentCut = true;
                }
                documentLeft -= count;

                for (int i = 0; i < count; i++)
                {
                    var dict = doc.ResolveObject(annots[i]) as PdfDictionary;
                    if (dict == null) continue;

                    // Resolved: the annotation is resolved on the line above and /Rect on the line
                    // below goes through ResolveArray, but the subtype did not. `continue` drops
                    // the annotation before it becomes a PdfLink, so the URL and its clickable
                    // region are lost outright to link checkers and PDF-to-HTML conversion.
                    var subtype = ResolveName(doc, dict[PdfName.Subtype]);
                    if (subtype == null || subtype.Value != PdfAnnotationSubtypes.Link) continue;

                    var rectArr = ResolveArray(doc, dict[PdfName.Rect]);
                    if (rectArr == null || rectArr.Count < 4) continue;

                    float x1 = ToFloat(doc, rectArr[0]);
                    float y1 = ToFloat(doc, rectArr[1]);
                    float x2 = ToFloat(doc, rectArr[2]);
                    float y2 = ToFloat(doc, rectArr[3]);
                    float x = Math.Min(x1, x2);
                    float y = Math.Min(y1, y2);
                    float w = Math.Abs(x2 - x1);
                    float h = Math.Abs(y2 - y1);

                    var link = ResolveTarget(doc, dict, ref namedDests);
                    result.Add(new PdfLink(p, x, y, w, h, link.Kind, link.Uri, link.Page,
                        link.Top, link.Named, link.File));
                }
            }
            return result;
        }

        private static string DocumentCeilingReached(int pageIndex) =>
            $"The pages name more than {PdfLimits.MaxAnnotationsPerDocument} annotation entries between them; " +
            $"only that many were examined, so the returned links are incomplete from page {pageIndex} on.";

        // ----------------------------------------------------------------
        //  Target resolution
        // ----------------------------------------------------------------

        /// <summary>What a link annotation points at; every field but <c>Kind</c> and <c>Page</c> null when unsaid.</summary>
        private struct Target
        {
            public PdfLinkKind Kind;
            public string Uri;
            public int Page;
            public float? Top;
            public string Named;
            public string File;
        }

        private static Target ResolveTarget(
            PdfDocument doc, PdfDictionary annot, ref Dictionary<string, NamedDestination> namedDests)
        {
            var t = new Target { Kind = PdfLinkKind.Unknown, Page = -1 };

            // /A action takes precedence over a direct /Dest.
            var actionObj = annot[PdfName.A];
            if (actionObj != null)
            {
                var action = doc.ResolveObject(actionObj) as PdfDictionary;
                if (action != null)
                {
                    // Resolved: the action dictionary is resolved on the line above, and /URI and
                    // /D below go through ResolveString/ResolveDest — only the action TYPE was raw.
                    // A null matches neither branch, so the link is reported with kind Unknown and
                    // no Url while its /Rect is correct: a clickable region that points nowhere.
                    var type = PdfActionTypes.FromSubtypeName(ResolveName(doc, action[PdfName.S])?.Value);
                    switch (type)
                    {
                        case PdfActionType.Uri:
                            string u = ResolveString(doc, action[PdfName.URI]);
                            if (!string.IsNullOrEmpty(u))
                            {
                                t.Kind = PdfLinkKind.Uri;
                                t.Uri = u;
                                return t;
                            }
                            break;
                        case PdfActionType.GoTo:
                            t.Kind = PdfLinkKind.GoTo;
                            t.Page = ResolveDest(doc, action[PdfName.D], ref namedDests, out t.Top);
                            return t;
                        case PdfActionType.Named:
                            // What the action says is PdfActionTypes' to read, as for the dispatcher and the runner.
                            t.Named = PdfActionTypes.DescribeTarget(doc, action, type, out _);
                            if (t.Named != null) { t.Kind = PdfLinkKind.Named; return t; }
                            break;
                        case PdfActionType.GoToRemote:
                        case PdfActionType.Launch:
                            t.Kind = type == PdfActionType.Launch ? PdfLinkKind.Launch : PdfLinkKind.RemoteGoTo;
                            t.File = PdfActionTypes.FileOf(doc, action, type);
                            if (t.Kind == PdfLinkKind.RemoteGoTo) t.Page = RemoteDestination(doc, action[PdfName.D], out t.Top);
                            return t;
                    }
                    // JavaScript / SubmitForm / … stay Unknown: a viewer does not run them from a link.
                }
            }

            // Direct /Dest on the link annotation.
            var destObj = annot[PdfName.Dest];
            if (destObj != null)
            {
                t.Kind = PdfLinkKind.GoTo;
                t.Page = ResolveDest(doc, destObj, ref namedDests, out t.Top);
            }
            return t;
        }

        /// <summary>
        /// Where a <c>/GoToR</c> lands in the other file (§12.6.4.3): an explicit destination names
        /// its page by number, answered as written since the other file's page count is not known
        /// here, with the top the array gives; a named destination is the other file's to resolve.
        /// </summary>
        private static int RemoteDestination(PdfDocument doc, PdfObject dest, out float? top)
        {
            top = null;
            if (!(doc.ResolveObject(dest) is PdfArray arr) || arr.Count == 0) return -1;
            // Not a page number until it is an integer, and never past an int: a page nothing can show.
            if (!(doc.ResolveObject(arr[0]) is PdfNumber number) || !number.IsInteger) return -1;
            long page = number.LongValue;
            if (page < 0 || page > int.MaxValue) return -1;
            top = PdfDestinationPage.TopAsFloat(doc, arr);
            return (int)page;
        }

        private static int ResolveDest(
            PdfDocument doc, PdfObject dest, ref Dictionary<string, NamedDestination> namedDests, out float? top)
        {
            top = null;
            if (dest == null) return -1;
            var resolved = doc.ResolveObject(dest);

            // Explicit destination: [pageRef /XYZ left top zoom] (or a page number).
            // One implementation, in Operations.PdfDestinationPage, which the named destinations
            // read too — a copy documented as "matching" another is not tested against it.
            if (resolved is PdfArray arr)
            {
                top = PdfDestinationPage.TopAsFloat(doc, arr);
                return PdfDestinationPage.IndexFromDestArray(doc, arr);
            }

            // Named destination: a /Name (PDF 1.1) or string (PDF 1.2+). Read from the resolved
            // value only: ResolveObject hands a direct object back unchanged, so falling back to
            // the raw `dest` could never find a name the resolved read had missed.
            string name = (resolved as PdfName)?.Value
                          ?? (resolved as PdfString)?.GetText();
            if (name != null)
            {
                if (namedDests == null) namedDests = BuildNamedDestIndex(doc);
                if (namedDests.TryGetValue(name, out NamedDestination named))
                {
                    top = PdfDestinationPage.TopAsFloat(doc, named.DestinationArray);
                    return named.PageIndex;
                }
            }
            return -1;
        }

        private static Dictionary<string, NamedDestination> BuildNamedDestIndex(PdfDocument doc)
        {
            var map = new Dictionary<string, NamedDestination>(StringComparer.Ordinal);
            // Reuse the existing name-tree / legacy-/Dests walker (DRY).
            foreach (var d in PdfNamedDestinations.CollectDestinations(doc))
                if (d.PageIndex >= 0 && !map.ContainsKey(d.Name))
                    map[d.Name] = d;
            return map;
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        private static PdfArray ResolveArray(PdfDocument doc, PdfObject obj)
            => obj as PdfArray ?? doc.ResolveObject(obj) as PdfArray;

        /// <summary>
        /// The sibling this file was missing. <c>ResolveArray</c> and <c>ResolveString</c> already
        /// existed, so the two names read here — an annotation's <c>/Subtype</c> and an action's
        /// <c>/S</c> — were the only reads in the file left following nothing.
        /// </summary>
        private static PdfName ResolveName(PdfDocument doc, PdfObject obj)
            => obj as PdfName ?? doc.ResolveObject(obj) as PdfName;

        private static float ToFloat(PdfDocument doc, PdfObject obj)
        {
            if (obj is PdfNumber n) return (float)n.DoubleValue;
            return doc.ResolveObject(obj) is PdfNumber rn ? (float)rn.DoubleValue : 0f;
        }

        private static string ResolveString(PdfDocument doc, PdfObject obj)
            => (obj as PdfString)?.GetText()
               ?? (doc.ResolveObject(obj) as PdfString)?.GetText();
    }
}
