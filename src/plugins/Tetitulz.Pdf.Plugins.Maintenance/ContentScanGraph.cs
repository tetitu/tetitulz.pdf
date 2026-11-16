// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Why a nested content stream is being walked — which decides what "not a form" means.
    /// </summary>
    /// <remarks>
    /// The two kinds answer different questions; see <see cref="ContentScanGraph.ShouldWalkSoftMaskGroup"/>.
    /// </remarks>
    internal enum NestedStreamRole
    {
        /// <summary>Named by a <c>Do</c> operator: may legitimately be an image, so skipping is silent.</summary>
        XObjectInvocation,

        /// <summary>An <c>/SMask</c>'s <c>/G</c>: §11.6.5.2 requires a form, so a refusal is reported.</summary>
        SoftMaskGroup,

        /// <summary>A pattern or a stream walked directly; only <c>/PatternType</c> is consulted.</summary>
        PatternOrDirect,
    }

    /// <summary>
    /// How the two content scanners resolve references through their object map, look a resource up
    /// by category and name, and decide whether a soft-mask group can be walked.
    /// </summary>
    /// <remarks>
    /// <para><c>FontGlyphUsageScanner</c> and <c>ImagePlacementScanner</c> walk the same graph for
    /// different reasons — one collects the glyphs a document draws so the rest can be pruned, the
    /// other collects where images are placed. They share <c>Deref</c>, <c>Lookup</c> and
    /// <c>LookupReference</c> here, and <b>one answer to the soft-mask question</b>.</para>
    ///
    /// <para><b>A divergence would delete glyphs.</b> If the font scanner skipped a soft-mask group
    /// the image scanner walks, glyphs drawn inside it would be absent from <c>UsedGlyphs</c> while
    /// <c>Reason</c> stayed null, so <c>PdfOptimizer</c> would treat the scan as complete and
    /// <c>TrueTypeGlyphPruner</c> would blank glyphs the file actually draws — a page with holes in
    /// it, and nothing in the pipeline saying so.</para>
    ///
    /// <para><b>The rule is conservative on purpose.</b> §11.6.5.2 Table 144 says
    /// a soft mask's <c>/G</c> shall be a form XObject, so a group declaring some <i>other</i>
    /// subtype has no content stream to walk and is skipped — <b>with a reason</b>. A group
    /// declaring <i>no</i> subtype is malformed, and is walked anyway: a scan whose output decides
    /// what to delete must never let "I did not look" reach the caller as "there was nothing
    /// there".</para>
    /// </remarks>
    internal static class ContentScanGraph
    {
        /// <summary>
        /// Bounds a reference chain. A malformed file can point an object at itself; the scanners
        /// hand back the same cached instance every time, so an unbounded follow never terminates.
        /// </summary>
        private const int MaxReferenceHops = 32;

        /// <summary>
        /// Every appearance stream reachable from a page's annotations (§12.5.5): each
        /// <c>/Annots</c> entry's <c>/AP</c>, every state key in it, and — where a key holds a
        /// dictionary of appearance states rather than a stream — every state inside that.
        /// </summary>
        /// <remarks>
        /// <para>Both scanners walk appearance streams through this, differing only in what they do
        /// with each stream and in how they carry their budget.</para>
        ///
        /// <para><b>The object number is yielded because it cannot be recovered later.</b> It comes
        /// from the <i>reference</i> that named the stream, so a caller holding only the resolved
        /// <see cref="PdfStream"/> has no way back to it; <c>-1</c> means the appearance was written
        /// directly rather than as an indirect object, which is legal and which both scanners
        /// already treated as "no number".</para>
        ///
        /// <para><b>Budgeting stays with the caller</b>, which is why this is an iterator rather
        /// than a callback: the two scanners hold their budgets differently (one a <c>ref int</c>,
        /// the other a field on its state object) and each simply stops enumerating; the budget is
        /// checked once per stream.</para>
        /// </remarks>
        internal static IEnumerable<(PdfStream Stream, int ObjectNumber, PdfDictionary Annotation)>
            AnnotationAppearanceStreams(PdfDictionary page, Dictionary<int, PdfObject> objects)
        {
            if (!(Deref(page?[PdfName.Annots], objects) is PdfArray annots)) yield break;

            for (int i = 0; i < annots.Count; i++)
            {
                if (!(Deref(annots[i], objects) is PdfDictionary annot)) continue;
                if (!(Deref(annot[PdfName.AP], objects) is PdfDictionary ap)) continue;

                foreach (var key in ap.Keys)
                {
                    PdfObject entry = ap[key];
                    PdfObject resolved = Deref(entry, objects);

                    // /N may be a stream, or a dictionary of appearance states.
                    if (resolved is PdfStream stream)
                    {
                        yield return (stream, ReferenceNumber(entry), annot);
                    }
                    else if (resolved is PdfDictionary states)
                    {
                        foreach (var stateKey in states.Keys)
                        {
                            PdfObject stateEntry = states[stateKey];
                            if (Deref(stateEntry, objects) is PdfStream stateStream)
                                yield return (stateStream, ReferenceNumber(stateEntry), annot);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The object number an entry was stored under, or <c>-1</c> when it is written directly.
        /// </summary>
        internal static int ReferenceNumber(PdfObject entry)
            => entry is PdfReference r ? r.ReferenceObjectNumber : -1;

        /// <summary>
        /// The page's <c>/Contents</c> as one stream for a walk that accepts at most
        /// <paramref name="maxTotal"/> bytes of it — <c>null</c> with <paramref name="overTotal"/> set
        /// when the parts together pass it; <paramref name="partUnread"/> set when a part does not decode
        /// on its own (that part is skipped, as <c>PdfPageContent.Concatenate</c> skips one).
        /// </summary>
        /// <remarks>
        /// <para><b>Each part of an array is decoded under what is left of the walk's ceiling</b>, rather than
        /// the page being decoded and joined whole before a walk could refuse it — each part up to
        /// <see cref="OptimizationOptions.MaxStreamDecodeBytes"/>, as many parts as the page lists.
        /// A part refused under the reduced ceiling is asked once under the per-stream one, so a part that
        /// does not decode is told from a page that is too large — the scan stops at the first of either
        /// kind of over, so that costs one bounded decode a page.</para>
        /// <para><b>Uncached:</b> a memo would keep every page's content alive for the whole optimization.</para>
        /// <para>One implementation for both scanners, through Core's <c>PdfPageContent</c> — §7.7.3.3's
        /// separator and the single-versus-array shape included; the decode is the delegate, which is why
        /// the shared form takes one rather than choosing a policy for every caller.</para>
        /// </remarks>
        internal static byte[] PageContent(PdfDictionary page, Dictionary<int, PdfObject> objects,
            OptimizationOptions options, long maxTotal, out bool partUnread, out bool overTotal)
        {
            bool unread = false, over = false;
            long perStream = options.MaxStreamDecodeBytes;
            // A single stream is not joined: it is decoded under the per-stream ceiling and the walk
            // judges it at once, with the refusal that names its own budget.
            Tetitulz.Pdf.Core.Document.PdfPageContent.Parts(page, o => Deref(o, objects), out bool isArray);
            long left = isArray ? maxTotal : long.MaxValue;
            byte[] content = Tetitulz.Pdf.Core.Document.PdfPageContent.Concatenate(page, o => Deref(o, objects), s =>
            {
                if (over) return null;
                long ceiling = left < perStream ? left : perStream;
                if (s.TryGetDecodedDataUncached(ceiling, out byte[] data, out _))
                {
                    left -= data.LongLength + 1;
                    return data;
                }
                if (ceiling < perStream && s.TryGetDecodedDataUncached(perStream, out _, out _)) over = true;
                else unread = true;
                return null;
            });
            partUnread = unread;
            overTotal = over;
            return over ? null : content;
        }

        /// <summary>Follows an indirect reference through the scan's object map.</summary>
        internal static PdfObject Deref(PdfObject obj, Dictionary<int, PdfObject> objects)
        {
            int guard = 0;
            while (obj is PdfReference reference && guard++ < MaxReferenceHops)
            {
                if (objects == null ||
                    !objects.TryGetValue(reference.ReferenceObjectNumber, out obj)) return null;
            }
            return obj;
        }

        /// <summary>The value of <c>resources[category][name]</c>, resolving at both levels.</summary>
        internal static PdfObject Lookup(PdfDictionary resources, PdfName category, PdfName name,
            Dictionary<int, PdfObject> objects)
        {
            var group = Deref(resources?[category], objects) as PdfDictionary;
            return group == null ? null : Deref(group[name], objects);
        }

        /// <summary>
        /// The object number <c>resources[category][name]</c> points at, or <c>-1</c> when it is
        /// not an indirect reference. Deliberately <b>not</b> resolved — the caller wants the
        /// identity, which is what its cycle guard keys on.
        /// </summary>
        internal static int LookupReference(PdfDictionary resources, PdfName category, PdfName name,
            Dictionary<int, PdfObject> objects)
        {
            var group = Deref(resources?[category], objects) as PdfDictionary;
            return group?[name] is PdfReference reference ? reference.ReferenceObjectNumber : -1;
        }

        /// <summary>
        /// Whether a stream named by a <c>Do</c> operator should be walked.
        /// </summary>
        /// <remarks>
        /// <b>Skipping an image here is not a refusal.</b> <c>Do</c> names a form XObject or an
        /// image XObject with equal legitimacy (§8.8), and an image has no content stream to walk,
        /// so declining one is the ordinary case and must stay silent — a scanner that reported it
        /// would disclaim something on nearly every page. A stream that declares <b>no</b> subtype
        /// is the different case: it cannot be classified as a form to walk or an image to skip, so
        /// it is reported, by both scanners.
        /// </remarks>
        internal static bool ShouldWalkXObject(
            PdfStream stream, Dictionary<int, PdfObject> objects, out string refusal)
        {
            refusal = null;
            if (stream == null) return false;

            var subtype = Deref(stream[PdfName.Subtype], objects) as PdfName;
            if (subtype == null)
            {
                refusal = "an XObject drawn by Do declared no /Subtype, so it could not be " +
                    "classified as a form to walk or an image to skip, and its contents were " +
                    "not examined";
                return false;
            }

            return subtype.Value == "Form";
        }

        /// <summary>
        /// Whether a soft-mask group's stream should be walked. Returns <c>false</c> only for a
        /// group that declares a subtype other than <c>/Form</c>, and always supplies a reason when
        /// it does — see the remarks on this class for why a missing subtype is walked instead.
        /// </summary>
        /// <remarks>
        /// <b>This is the opposite default from <see cref="ShouldWalkXObject"/>, and deliberately
        /// so.</b> A <c>Do</c> may legitimately name something with no content; a soft mask's
        /// <c>/G</c> may not — §11.6.5.2 Table 144 requires a form XObject there. So an
        /// unclassifiable <c>Do</c> target is skipped and an unclassifiable soft-mask group is
        /// walked, and each is the conservative answer for its own question. One boolean serving
        /// both questions would apply the rule correct for <c>Do</c> to soft masks and silently
        /// discard a soft mask's glyphs.
        /// </remarks>
        internal static bool ShouldWalkSoftMaskGroup(
            PdfStream group, Dictionary<int, PdfObject> objects, out string refusal)
        {
            refusal = null;
            if (group == null) return false;

            var subtype = Deref(group[PdfName.Subtype], objects) as PdfName;
            if (subtype != null && subtype.Value != "Form")
            {
                refusal = "a soft-mask group declared /Subtype /" + subtype.Value +
                    " rather than /Form, so it has no content stream to walk and its contents " +
                    "were not examined";
                return false;
            }

            return true;
        }
    }
}
