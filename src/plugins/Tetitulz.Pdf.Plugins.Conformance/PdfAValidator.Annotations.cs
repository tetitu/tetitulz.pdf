// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>How many occurrences an annotation finding names before it counts the rest.</summary>
        private const int MaxNamedAnnotations = 5;

        // ISO 19005-1 6.5.2 (rule 6.5.2-1): PDF Reference 1.4's types, less FileAttachment,
        // Sound and Movie.
        private static readonly HashSet<string> Part1AnnotationTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            PdfAnnotationSubtypes.Text, PdfAnnotationSubtypes.Link, PdfAnnotationSubtypes.FreeText,
            PdfAnnotationSubtypes.Line, PdfAnnotationSubtypes.Square, PdfAnnotationSubtypes.Circle,
            PdfAnnotationSubtypes.Highlight, PdfAnnotationSubtypes.Underline, PdfAnnotationSubtypes.Squiggly,
            PdfAnnotationSubtypes.StrikeOut, PdfAnnotationSubtypes.Stamp, PdfAnnotationSubtypes.Ink,
            PdfAnnotationSubtypes.Popup, PdfAnnotationSubtypes.Widget, PdfAnnotationSubtypes.PrinterMark,
            PdfAnnotationSubtypes.TrapNet,
        };

        // ISO 19005-2/3 6.3.1 (rule 6.3.1-1): ISO 32000-1's types, less 3D, Sound, Screen and Movie.
        private static readonly HashSet<string> Part2AnnotationTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            PdfAnnotationSubtypes.Text, PdfAnnotationSubtypes.Link, PdfAnnotationSubtypes.FreeText,
            PdfAnnotationSubtypes.Line, PdfAnnotationSubtypes.Square, PdfAnnotationSubtypes.Circle,
            PdfAnnotationSubtypes.Polygon, PdfAnnotationSubtypes.PolyLine, PdfAnnotationSubtypes.Highlight,
            PdfAnnotationSubtypes.Underline, PdfAnnotationSubtypes.Squiggly, PdfAnnotationSubtypes.StrikeOut,
            PdfAnnotationSubtypes.Stamp, PdfAnnotationSubtypes.Caret, PdfAnnotationSubtypes.Ink,
            PdfAnnotationSubtypes.Popup, PdfAnnotationSubtypes.FileAttachment, PdfAnnotationSubtypes.Widget,
            PdfAnnotationSubtypes.PrinterMark, PdfAnnotationSubtypes.TrapNet, PdfAnnotationSubtypes.Watermark,
            PdfAnnotationSubtypes.Redact,
        };

        // ISO 19005-4 6.3.1 (rule 6.3.1-1): ISO 32000-2's Table 171, less Sound, Screen, Movie, 3D,
        // RichMedia and FileAttachment — PDF/A-4f adds FileAttachment back, PDF/A-4e 3D and RichMedia.
        private static readonly HashSet<string> Part4AnnotationTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            PdfAnnotationSubtypes.Text, PdfAnnotationSubtypes.Link, PdfAnnotationSubtypes.FreeText,
            PdfAnnotationSubtypes.Line, PdfAnnotationSubtypes.Square, PdfAnnotationSubtypes.Circle,
            PdfAnnotationSubtypes.Polygon, PdfAnnotationSubtypes.PolyLine, PdfAnnotationSubtypes.Highlight,
            PdfAnnotationSubtypes.Underline, PdfAnnotationSubtypes.Squiggly, PdfAnnotationSubtypes.StrikeOut,
            PdfAnnotationSubtypes.Stamp, PdfAnnotationSubtypes.Caret, PdfAnnotationSubtypes.Ink,
            PdfAnnotationSubtypes.Popup, PdfAnnotationSubtypes.Widget, PdfAnnotationSubtypes.PrinterMark,
            PdfAnnotationSubtypes.TrapNet, PdfAnnotationSubtypes.Watermark, PdfAnnotationSubtypes.Redact,
            PdfAnnotationSubtypes.Projection,
        };

        /// <summary>
        /// A37 — an annotation type the part does not permit; A38 — an annotation's <c>/F</c> missing,
        /// or not printing, or hiding it; A39 — an appearance the part does not admit (none where one
        /// is required, an <c>/AP</c> with more than <c>/N</c>, an <c>/N</c> of the wrong kind).
        /// ISO 19005-1 6.5.2 and 6.5.3; ISO 19005-2/3/4 6.3.1, 6.3.2 and 6.3.3; the validation rules of the
        /// same numbers. A85 — PDF/A-1's annotation colour rules (6.5.3; rules 6.5.3-1 and -3): an
        /// annotation's <c>/CA</c> is 1.0, and it holds <c>/C</c> or <c>/IC</c> only when the PDF/A-1 output
        /// intent's profile is RGB.
        /// </summary>
        private static void CheckAnnotations(PdfDocument doc, PdfAConformancePart part, string level,
            List<PdfAViolation> v)
        {
            if (doc?.Pages == null) return;
            bool part1 = part == PdfAConformancePart.Part1;
            bool part4 = part == PdfAConformancePart.Part4;
            var permitted = part1 ? Part1AnnotationTypes : part4 ? Part4AnnotationTypes : Part2AnnotationTypes;

            var types = new Occurrences();
            var flags = new Occurrences();
            var appearances = new Occurrences();
            var colours = new Occurrences();
            Func<PdfObject, PdfObject> resolve = o => doc.ResolveObject(o);
            string outputColourSpace = null;
            bool outputColourSpaceRead = false;

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                if (!(resolve(doc.Pages[p].Dictionary?[PdfName.Annots]) is PdfArray annots) || !ScanReuse.AdmitAnnotations(annots)) continue;
                string page = "page " + (p + 1).ToString(CultureInfo.InvariantCulture);
                for (int a = 0; a < annots.Count; a++)
                {
                    if (!(resolve(annots[a]) is PdfDictionary annot)) continue;
                    string subtype = (resolve(annot[PdfName.Subtype]) as PdfName)?.Value;
                    string shown = page + ": /" + (subtype ?? "(no subtype)");
                    bool popup = subtype == PdfAnnotationSubtypes.Popup;
                    bool zeroSize = IsZeroSize(resolve(annot[PdfName.Rect]) as PdfArray, resolve);

                    bool allowed = subtype != null && (permitted.Contains(subtype)
                        || (part4 && level == "F" && subtype == PdfAnnotationSubtypes.FileAttachment)
                        || (part4 && level == "E" && (subtype == PdfAnnotationSubtypes.ThreeD
                                                      || subtype == PdfAnnotationSubtypes.RichMedia)));
                    if (!allowed) types.Add(shown);

                    // /F: required of every annotation in PDF/A-1; of all but a Popup in PDF/A-2 and
                    // -3; of all but a Popup or one with a zero-size /Rect in PDF/A-4. When present,
                    // Print set and Hidden, Invisible and NoView clear — and ToggleNoView from PDF/A-2.
                    var flagWord = PdfAnnotationFlags.FromNumber(resolve(annot[PdfName.F]));
                    bool fExempt = !part1 && (popup || (part4 && zeroSize));
                    if (flagWord == null)
                    {
                        if (!fExempt) flags.Add(shown + " has no /F");
                    }
                    else
                    {
                        int f = flagWord.Value;
                        int forbidden = PdfAnnotationFlags.Invisible | PdfAnnotationFlags.Hidden | PdfAnnotationFlags.NoView
                                        | (part1 ? 0 : PdfAnnotationFlags.ToggleNoView);
                        if ((f & PdfAnnotationFlags.Print) == 0 || (f & forbidden) != 0)
                            flags.Add(shown + " /F " + f.ToString(CultureInfo.InvariantCulture));
                    }

                    if (part1)
                    {
                        if (resolve(annot[PdfName.CA]) is PdfNumber opacity && !IsOpaqueAlpha(opacity.DoubleValue))
                            colours.Add(shown + " /CA " + opacity.DoubleValue.ToString("0.###", CultureInfo.InvariantCulture));
                        if (annot[PdfName.C] != null || annot[PdfName.IC] != null)
                        {
                            if (!outputColourSpaceRead)
                            {
                                outputColourSpaceRead = true;
                                outputColourSpace = PdfA1OutputIntentColourSpace(doc);
                            }
                            if (outputColourSpace != "RGB ")
                                colours.Add(shown + " holds /" + (annot[PdfName.C] != null ? "C" : "IC") + " and the output intent's profile is " +
                                            (outputColourSpace == null ? "absent or unreadable" : "'" + outputColourSpace.Trim() + "'"));
                        }
                    }

                    var ap = resolve(annot[PdfName.AP]) as PdfDictionary;
                    if (ap == null)
                    {
                        // PDF/A-1: a widget's appearance (6.9, rule 6.9-2). PDF/A-2 onwards: an
                        // appearance unless a Popup, a Link (or a Projection in PDF/A-4), or a zero-size /Rect.
                        bool apExempt = part1 ? subtype != PdfAnnotationSubtypes.Widget
                            : zeroSize || popup || subtype == PdfAnnotationSubtypes.Link
                              || (part4 && subtype == PdfAnnotationSubtypes.Projection);
                        if (!apExempt) appearances.Add(shown + " has no /AP" + (part1 ? " (ISO 19005-1 6.9; rule 6.9-2)" : ""));
                        continue;
                    }
                    foreach (var key in ap.Keys)
                        if (key.Value != "N")
                        {
                            appearances.Add(shown + " /AP holds /" + key.Value);
                            break;
                        }
                    var normal = resolve(ap[PdfName.N]);
                    if (normal == null) continue;
                    bool button = subtype == PdfAnnotationSubtypes.Widget
                                  && FieldTypeOf(annot, resolve) == PdfFieldTypes.Button;
                    if (button ? !IsAppearanceSubdictionary(normal, resolve) : !(normal is PdfStream))
                        appearances.Add(shown + (button ? " /AP /N is not a subdictionary of appearance streams"
                                                          : " /AP /N is not a stream"));
                }
            }

            string clause1 = part1 ? "ISO 19005-1 6.5.2; rule 6.5.2-1" : $"ISO 19005-{(int)part} 6.3.1; rule 6.3.1-1";
            string clause2 = part1 ? "ISO 19005-1 6.5.3; rule 6.5.3-2" : $"ISO 19005-{(int)part} 6.3.2; rules 6.3.2-1/-2";
            string clause3 = part1 ? "ISO 19005-1 6.5.3; rules 6.5.3-4 to -6" : $"ISO 19005-{(int)part} 6.3.3; rules 6.3.3-1 to -4";
            if (types.Count > 0)
                v.Add(new PdfAViolation("A37", $"An annotation type the part does not permit: {types} ({clause1})."));
            if (flags.Count > 0)
                v.Add(new PdfAViolation("A38",
                    $"An annotation's /F is missing, or does not set Print, or sets Hidden, Invisible, NoView" +
                    (part1 ? "" : " or ToggleNoView") + $": {flags} ({clause2})."));
            if (appearances.Count > 0)
                v.Add(new PdfAViolation("A39", $"An annotation's appearance is not one the part admits: {appearances} ({clause3})."));
            if (colours.Count > 0)
                v.Add(new PdfAViolation("A85",
                    $"An annotation's /CA is not 1.0, or it holds /C or /IC without an RGB output intent: {colours} " +
                    "(ISO 19005-1 6.5.3; rules 6.5.3-1/-3)."));
        }

        /// <summary>
        /// The data colour space (<c>"RGB "</c>, <c>"CMYK"</c>, …) of the <c>GTS_PDFA1</c> output intent's
        /// <c>/DestOutputProfile</c>, from its header, or <see langword="null"/> when there is no such intent
        /// or its profile does not read — "not RGB", which is the direction that cannot pass a file wrongly.
        /// </summary>
        private static string PdfA1OutputIntentColourSpace(PdfDocument doc)
        {
            if (!(doc.ResolveObject(doc.Catalog?.Dictionary?[PdfName.OutputIntents]) is PdfArray intents)) return null;
            for (int i = 0; i < intents.Count; i++)
            {
                if (!(doc.ResolveObject(intents[i]) is PdfDictionary intent)) continue;
                if ((doc.ResolveObject(intent[PdfName.S]) as PdfName)?.Value != "GTS_PDFA1") continue;
                if (!(doc.ResolveObject(intent[PdfName.DestOutputProfile]) is PdfStream profile)) return null;
                try
                {
                    return IccProfileComponents.TryGetHeaderFields(profile.GetDecodedData(), out _, out string colourSpace, out _)
                        ? colourSpace : null;
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    Report("A85", "the output intent's profile does not decode (" + ex.GetType().Name + ")");
                    return null;
                }
            }
            return null;
        }

        /// <summary>
        /// What one annotation rule found: the first few occurrences by name, and how many there
        /// were — a file chooses how many annotations it has, so the names held are capped.
        /// </summary>
        private sealed class Occurrences
        {
            private readonly List<string> _named = new List<string>();

            internal int Count { get; private set; }

            internal void Add(string occurrence)
            {
                if (_named.Count < MaxNamedAnnotations) _named.Add(occurrence);
                Count++;
            }

            public override string ToString()
                => string.Join("; ", _named)
                   + (Count > _named.Count
                       ? " and " + (Count - _named.Count).ToString(CultureInfo.InvariantCulture) + " more"
                       : "");
        }

        /// <summary>Whether a <c>/Rect</c> has zero width and zero height (the profile's test: <c>width == 0 &amp;&amp; height == 0</c>).</summary>
        private static bool IsZeroSize(PdfArray rect, Func<PdfObject, PdfObject> resolve)
        {
            if (rect == null || rect.Count < 4) return false;
            var n = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!(resolve(rect[i]) is PdfNumber number)) return false;
                n[i] = number.DoubleValue;
            }
            return n[0] == n[2] && n[1] == n[3];
        }

        /// <summary>A widget's field type, inherited through <c>/Parent</c> (ISO 32000-1 Table 220).</summary>
        private static string FieldTypeOf(PdfDictionary widget, Func<PdfObject, PdfObject> resolve)
        {
            var raw = AcroFieldInheritance.Find(
                widget, PdfName.FT,
                d => resolve(d[PdfName.Parent]) as PdfDictionary,
                resolve,
                o => o is PdfName,
                PdfLimits.MaxFieldInheritanceDepth + 1,
                out _);
            return (resolve(raw) as PdfName)?.Value;
        }

        /// <summary>
        /// A button's normal appearance: a dictionary whose every entry is an appearance stream, one
        /// for each state (the profile's test: <c>N_type == "Dict" &amp;&amp; containsAppearances</c>).
        /// </summary>
        private static bool IsAppearanceSubdictionary(PdfObject normal, Func<PdfObject, PdfObject> resolve)
        {
            if (!(normal is PdfDictionary states) || normal is PdfStream) return false;
            int count = 0;
            foreach (var key in states.Keys)
            {
                if (!(resolve(states[key]) is PdfStream)) return false;
                count++;
            }
            return count > 0;
        }
    }
}
