// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// Which entries of the kept pages' resource dictionaries the kept pages draw: what an extract
    /// carries of a resource dictionary several pages share.
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> A producer commonly writes one <c>/Resources</c> for every page, naming every
    /// page's pictures and forms. Carried whole, an extract of one page holds the images and the
    /// text of the forms only the dropped pages draw: invisible in a viewer, one object dump away.</para>
    /// <para><b>What is read.</b> Each kept page's content and its annotations' appearances, and from
    /// them every form, Type 3 glyph, tiling pattern and soft-mask group they draw, each with the
    /// resource dictionary it draws through: its own, or the one it was drawn from when it has none.
    /// An entry of a category (<c>/Font</c>, <c>/XObject</c>, <c>/ExtGState</c>, <c>/ColorSpace</c>,
    /// <c>/Pattern</c>, <c>/Shading</c>, <c>/Properties</c>) is kept when one of them names it; a
    /// colour space also keeps the entries its own value names (a Pattern space's underlying space,
    /// an Indexed base), which a reader looks up by name in the same dictionary, and the default
    /// colour spaces (<c>/DefaultGray</c>, <c>/DefaultRGB</c>, <c>/DefaultCMYK</c>, §8.6.5.6) are kept
    /// wherever drawing goes through their dictionary, since every device colour is drawn through
    /// them and no operator names them.</para>
    /// <para><b>Kept whole</b>, as an extract carried every resource dictionary before this read:
    /// one whose drawing could not all be read — a stream that will not decode, or the read's
    /// budget spent — and a category dictionary that something the extract carries names without
    /// having been read here (<see cref="ReleaseWhatOthersName"/>): an appearance this did not
    /// draw, a form's <c>/DR</c>. What those draw is not known, so nothing of theirs is left out.</para>
    /// </remarks>
    internal sealed class SplitResourceUse
    {
        private static readonly PdfName[] Categories =
        {
            PdfName.Font, PdfName.XObject, PdfName.ExtGState, PdfName.ColorSpace,
            PdfName.Pattern, PdfName.Shading, PdfName.Properties,
        };

        /// <summary>The appearance states an annotation's <c>/AP</c> holds (§12.5.5).</summary>
        private static readonly PdfName[] AppearanceKinds = { PdfName.N, PdfName.R, PdfName.D };

        /// <summary>
        /// The colour spaces a device colour is drawn through when the resources hold them
        /// (§8.6.5.6): named by no operator, used by every device-colour paint, image and shading.
        /// </summary>
        private static readonly string[] DefaultColourSpaces = { "DefaultGray", "DefaultRGB", "DefaultCMYK" };

        /// <summary>How deep a colour space's value is searched for names it looks up.</summary>
        private const int MaxColourSpaceDepth = 8;

        /// <summary>How deep a carried object's own nesting is searched for a <c>/Resources</c> or <c>/DR</c>.</summary>
        private const int MaxReleaseDepth = 32;

        private readonly PdfDocument _doc;

        // Resource dictionary → category → the names drawn through it.
        private readonly Dictionary<PdfDictionary, Dictionary<PdfName, HashSet<string>>> _named =
            new Dictionary<PdfDictionary, Dictionary<PdfName, HashSet<string>>>(PdfReferenceEquality.Instance);

        // Every resource dictionary a read drew through, named or not.
        private readonly HashSet<PdfDictionary> _analysed = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);

        // Resource dictionaries whose drawing could not all be read: kept whole.
        private readonly HashSet<PdfDictionary> _undecided = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);

        // The dictionaries whose drawing was read with the resources they name: pages, forms,
        // appearances, Type 3 fonts, tiling patterns, soft-mask groups.
        private readonly HashSet<PdfDictionary> _read = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);

        // Stream → the resource dictionaries it has been read with (a form drawn from two contexts
        // that both lack their own resources is read once with each).
        private readonly Dictionary<PdfDictionary, HashSet<PdfDictionary>> _readWith =
            new Dictionary<PdfDictionary, HashSet<PdfDictionary>>(PdfReferenceEquality.Instance);

        private readonly Queue<(PdfStream Stream, PdfDictionary Resources)> _pending = new Queue<(PdfStream, PdfDictionary)>();

        // Category dictionary → the names kept; a category dictionary absent from it is carried whole.
        private readonly Dictionary<PdfDictionary, HashSet<string>> _kept =
            new Dictionary<PdfDictionary, HashSet<string>>(PdfReferenceEquality.Instance);

        // The carried objects ReleaseWhatOthersName has already searched: what it releases only ever
        // shrinks what is kept, so an object searched once has nothing more to say.
        private readonly HashSet<int> _searchedForRelease = new HashSet<int>();
        private bool _searchedDirectlyCarried;

        private long _bytesLeft;
        private int _readsLeft;
        private string _firstUndecided;

        private SplitResourceUse(PdfDocument doc)
        {
            _doc = doc;
            _bytesLeft = PdfLimits.MaxDecompressedBytes;
            _readsLeft = PdfLimits.MaxFormXObjectInvocationsPerPage;
        }

        /// <summary>How many resource dictionaries are carried whole because their drawing could not all be read.</summary>
        internal int UndecidedCount => _undecided.Count;

        /// <summary>Why the first of them could not be read, or null.</summary>
        internal string FirstUndecided => _firstUndecided;

        /// <summary>How many category dictionaries were released whole because something unread names them.</summary>
        internal int ReleasedCount { get; private set; }

        /// <summary>How many content streams were read: the evidence of the walk, which an extract cannot show.</summary>
        internal int StreamsRead { get; private set; }

        /// <summary>How many carried objects <see cref="ReleaseWhatOthersName"/> has searched, over every call: each once.</summary>
        internal int ObjectsSearchedForRelease { get; private set; }

        /// <summary>Reads what <paramref name="pages"/> draw from their resources.</summary>
        internal static SplitResourceUse Read(PdfDocument doc, IReadOnlyList<PdfPage> pages)
        {
            var use = new SplitResourceUse(doc);
            foreach (PdfPage page in pages)
            {
                PdfDictionary resources = page.Resources;
                use._read.Add(page.Dictionary);
                if (resources != null) use._analysed.Add(resources);

                byte[] content;
                try
                {
                    content = page.GetContentStreamData();
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    use.Undecided(resources, "page " + (page.PageIndex + 1).ToString(CultureInfo.InvariantCulture) +
                                             "'s content could not be read (" + ex.Message + ")");
                    content = null;
                }
                if (content != null && use.Spend(content.Length, resources))
                {
                    use.StreamsRead++;
                    use.Scan(content, resources);
                }

                // An appearance stream is a form; one with no resources of its own is read with the page's.
                if (page.GetAnnotations() is PdfArray annotations)
                    foreach (PdfObject item in annotations)
                        if (doc.ResolveObject(item) is PdfDictionary annotation
                            && doc.ResolveObject(annotation[PdfName.AP]) is PdfDictionary appearance)
                            foreach (PdfName kind in AppearanceKinds)
                            {
                                PdfObject state = doc.ResolveObject(appearance[kind]);
                                if (state is PdfStream single) use.Enqueue(single, resources);
                                else if (state is PdfDictionary states)
                                    foreach (var entry in states)
                                        if (doc.ResolveObject(entry.Value) is PdfStream one) use.Enqueue(one, resources);
                            }
            }
            use.Drain();
            use.Decide();
            return use;
        }

        /// <summary>Whether the collection walk leaves out <paramref name="key"/> of <paramref name="container"/>: an entry of a category dictionary no kept page draws.</summary>
        internal bool Skips(PdfDictionary container, PdfName key)
            => _kept.Count > 0 && _kept.TryGetValue(container, out HashSet<string> names) && !names.Contains(key.Value);

        /// <summary>
        /// The category dictionaries something the extract carries names without having been read
        /// here — every <c>/Resources</c> of a dictionary this did not read with it, every
        /// <c>/DR</c> — released to be carried whole; the caller walks each again, since the walk
        /// left their entries out. Asked until it releases nothing: what a released dictionary
        /// brings may name another. Each carried object is searched once over all the calls, so a
        /// chain of releases costs the extract's size, not its size once a link.
        /// </summary>
        internal List<PdfDictionary> ReleaseWhatOthersName(IEnumerable<int> collected, IEnumerable<PdfObject> alsoCarried)
        {
            var released = new List<PdfDictionary>();
            if (_kept.Count == 0) return released;

            var roots = new List<PdfObject>();
            foreach (int number in collected)
                if (_searchedForRelease.Add(number)) roots.Add(_doc.GetObject(number));
            if (!_searchedDirectlyCarried)
            {
                roots.AddRange(alsoCarried);
                _searchedDirectlyCarried = true;
            }
            ObjectsSearchedForRelease += roots.Count;

            var seen = new HashSet<object>(PdfReferenceEquality.Instance);
            var walk = new Stack<(PdfObject Value, int Depth)>();
            foreach (PdfObject root in roots) walk.Push((root, 0));
            while (walk.Count > 0)
            {
                var (value, depth) = walk.Pop();
                if (value == null || depth > MaxReleaseDepth || !seen.Add(value)) continue;
                if (value is PdfDictionary dict)
                {
                    ReleaseNamedBy(dict, PdfName.Resources, released);
                    ReleaseNamedBy(dict, PdfName.DR, released);
                    foreach (var entry in dict)
                        if (entry.Key != PdfName.Parent && (entry.Value is PdfDictionary || entry.Value is PdfArray))
                            walk.Push((entry.Value, depth + 1));
                }
                else if (value is PdfArray array)
                {
                    foreach (PdfObject item in array)
                        if (item is PdfDictionary || item is PdfArray) walk.Push((item, depth + 1));
                }
            }
            ReleasedCount += released.Count;
            return released;
        }

        private void ReleaseNamedBy(PdfDictionary holder, PdfName key, List<PdfDictionary> released)
        {
            if (!(_doc.ResolveObject(holder[key]) is PdfDictionary resources)) return;
            // A dictionary read with its own resources draws what the read found; nothing else does.
            if (key == PdfName.Resources && _read.Contains(holder) && _analysed.Contains(resources)) return;
            foreach (PdfName category in Categories)
                if (_doc.ResolveObject(resources[category]) is PdfDictionary names && _kept.Remove(names))
                    released.Add(names);
        }

        // ------------------------------------------------------------------ reading

        private void Enqueue(PdfStream stream, PdfDictionary resources)
        {
            PdfDictionary own = _doc.ResolveObject(stream[PdfName.Resources]) as PdfDictionary;
            PdfDictionary drawsThrough = own ?? resources;
            if (!_readWith.TryGetValue(stream, out HashSet<PdfDictionary> with))
                _readWith[stream] = with = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            // A stream with no resources at all is keyed by itself: it names nothing, but is read once.
            if (!with.Add(drawsThrough ?? stream)) return;
            _pending.Enqueue((stream, drawsThrough));
        }

        private void Drain()
        {
            while (_pending.Count > 0)
            {
                var (stream, resources) = _pending.Dequeue();
                _read.Add(stream);
                // With no resources to draw through, a stream names nothing a read could keep.
                if (resources == null) continue;
                _analysed.Add(resources);
                if (_readsLeft-- <= 0)
                {
                    Undecided(resources, "more than " + PdfLimits.MaxFormXObjectInvocationsPerPage.ToString(CultureInfo.InvariantCulture) +
                                         " content streams are drawn from the kept pages");
                    continue;
                }
                if (_bytesLeft < 1)
                {
                    Undecided(resources, "the kept pages draw more than " + PdfLimits.MaxDecompressedBytes.ToString(CultureInfo.InvariantCulture) +
                                         " bytes of content");
                    continue;
                }
                if (!stream.TryGetDecodedData(_bytesLeft, out byte[] data, out string refused))
                {
                    // Only the decoder knows how far it got, so a refusal spends half of what is left.
                    _bytesLeft -= Math.Max(1, _bytesLeft / 2);
                    Undecided(resources, "a stream they draw could not be read (" + refused + ")");
                    continue;
                }
                data = data ?? Array.Empty<byte>();
                if (!Spend(data.Length, resources)) continue;
                StreamsRead++;
                Scan(data, resources);
            }
        }

        private bool Spend(long bytes, PdfDictionary resources)
        {
            if (bytes > _bytesLeft)
            {
                _bytesLeft = 0;
                Undecided(resources, "the kept pages draw more than " + PdfLimits.MaxDecompressedBytes.ToString(CultureInfo.InvariantCulture) +
                                     " bytes of content");
                return false;
            }
            _bytesLeft -= bytes;
            return true;
        }

        private void Undecided(PdfDictionary resources, string why)
        {
            if (resources == null) return;
            if (_undecided.Add(resources)) _firstUndecided ??= why;
        }

        /// <summary>The names <paramref name="content"/> draws through <paramref name="resources"/>, and what it draws from them queued to be read.</summary>
        private void Scan(byte[] content, PdfDictionary resources)
        {
            if (resources == null) return;
            foreach (ContentStreamOperation op in ContentStreamParser.Parse(content))
            {
                switch (op.Operator)
                {
                    case PdfOperators.SetFont:
                        if (NameAt(op, 0) is string font)
                        {
                            Note(resources, PdfName.Font, font);
                            if (Entry(resources, PdfName.Font, font) is PdfDictionary fontDict
                                && _doc.ResolveObject(fontDict[PdfName.Subtype]) is PdfName subtype && subtype == PdfName.Type3)
                                ReadType3(fontDict, resources);
                        }
                        break;

                    case PdfOperators.InvokeXObject:
                        if (NameAt(op, 0) is string xobject)
                        {
                            Note(resources, PdfName.XObject, xobject);
                            if (Entry(resources, PdfName.XObject, xobject) is PdfStream form
                                && _doc.ResolveObject(form[PdfName.Subtype]) is PdfName kind && kind == PdfName.Form)
                                Enqueue(form, resources);
                        }
                        break;

                    case PdfOperators.SetExtGState:
                        if (NameAt(op, 0) is string state)
                        {
                            Note(resources, PdfName.ExtGState, state);
                            if (Entry(resources, PdfName.ExtGState, state) is PdfDictionary gs)
                            {
                                if (_doc.ResolveObject(gs[PdfName.SMask]) is PdfDictionary mask
                                    && _doc.ResolveObject(mask[PdfName.G]) is PdfStream group)
                                    Enqueue(group, resources);

                                // Table 57's /Font [font size] selects a font as Tf does; a Type 3 font so
                                // chosen draws its glyph procedures from these resources (Table 112) when
                                // it has none of its own, and what they name is drawn.
                                if (_doc.ResolveObject(gs[PdfName.Font]) is PdfArray gsFont && gsFont.Count > 0
                                    && _doc.ResolveObject(gsFont[0]) is PdfDictionary gsFontDict
                                    && _doc.ResolveObject(gsFontDict[PdfName.Subtype]) is PdfName gsKind && gsKind == PdfName.Type3)
                                    ReadType3(gsFontDict, resources);
                            }
                        }
                        break;

                    case PdfOperators.SetColorSpaceFill:
                    case PdfOperators.SetColorSpaceStroke:
                        if (NameAt(op, 0) is string space) NoteColourSpace(resources, space);
                        break;

                    case PdfOperators.SetColorFillN:
                    case PdfOperators.SetColorStrokeN:
                        if (NameAt(op, op.Operands.Length - 1) is string pattern)
                        {
                            Note(resources, PdfName.Pattern, pattern);
                            // A tiling pattern is a stream with content of its own (§8.7.3.1).
                            if (Entry(resources, PdfName.Pattern, pattern) is PdfStream tile) Enqueue(tile, resources);
                        }
                        break;

                    case PdfOperators.ShadingFill:
                        if (NameAt(op, 0) is string shading) Note(resources, PdfName.Shading, shading);
                        break;

                    case PdfOperators.BeginMarkedContentProps:
                    case PdfOperators.MarkedPointProps:
                        if (NameAt(op, 1) is string properties) Note(resources, PdfName.Properties, properties);
                        break;

                    case PdfOperators.BeginInlineImage:
                        // An inline image may name its colour space from the resources (§8.9.7).
                        if (op.Operands.Length > 0 && op.Operands[0] is PdfDictionary image)
                            NoteColourSpaceValue(resources, image[PdfName.CS] ?? image[PdfName.ColorSpace], 0);
                        break;
                }
            }
        }

        private void ReadType3(PdfDictionary font, PdfDictionary resources)
        {
            _read.Add(font);
            PdfDictionary own = _doc.ResolveObject(font[PdfName.Resources]) as PdfDictionary;
            if (_doc.ResolveObject(font[PdfName.CharProcs]) is PdfDictionary procedures)
                foreach (var entry in procedures)
                    if (_doc.ResolveObject(entry.Value) is PdfStream glyph)
                        Enqueue(glyph, own ?? resources);
        }

        private void Note(PdfDictionary resources, PdfName category, string name)
        {
            if (!_named.TryGetValue(resources, out var byCategory))
                _named[resources] = byCategory = new Dictionary<PdfName, HashSet<string>>();
            if (!byCategory.TryGetValue(category, out HashSet<string> names))
                byCategory[category] = names = new HashSet<string>(StringComparer.Ordinal);
            names.Add(name);
        }

        /// <summary>A colour space named, and every entry of the same dictionary its value names.</summary>
        private void NoteColourSpace(PdfDictionary resources, string name)
        {
            var pending = new Stack<string>();
            pending.Push(name);
            var noted = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                string next = pending.Pop();
                if (!noted.Add(next)) continue;
                Note(resources, PdfName.ColorSpace, next);
                foreach (string named in NamesIn(Entry(resources, PdfName.ColorSpace, next), 0))
                    pending.Push(named);
            }
        }

        private void NoteColourSpaceValue(PdfDictionary resources, PdfObject value, int depth)
        {
            foreach (string named in NamesIn(value, depth)) NoteColourSpace(resources, named);
        }

        private IEnumerable<string> NamesIn(PdfObject value, int depth)
        {
            if (value == null || depth > MaxColourSpaceDepth) yield break;
            if (value is PdfName name) { yield return name.Value; yield break; }
            if (value is PdfArray array)
                foreach (PdfObject item in array)
                    if (item is PdfName || item is PdfArray)
                        foreach (string inner in NamesIn(item, depth + 1)) yield return inner;
        }

        private PdfObject Entry(PdfDictionary resources, PdfName category, string name)
            => (_doc.ResolveObject(resources[category]) as PdfDictionary) is PdfDictionary names
                ? _doc.ResolveObject(names[new PdfName(name)])
                : null;

        private static string NameAt(ContentStreamOperation op, int index)
            => index >= 0 && index < op.Operands.Length && op.Operands[index] is PdfName name ? name.Value : null;

        // ------------------------------------------------------------------ deciding

        private void Decide()
        {
            // A category dictionary an undecided resource dictionary holds is carried whole, wherever else it is named.
            var whole = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            foreach (PdfDictionary resources in _undecided)
                foreach (PdfName category in Categories)
                    if (_doc.ResolveObject(resources[category]) is PdfDictionary names) whole.Add(names);

            foreach (PdfDictionary resources in _analysed)
            {
                if (_undecided.Contains(resources)) continue;
                // What draws through these resources draws its device colours through their defaults.
                foreach (string space in DefaultColourSpaces)
                    if (Entry(resources, PdfName.ColorSpace, space) != null) NoteColourSpace(resources, space);
                _named.TryGetValue(resources, out var byCategory);
                foreach (PdfName category in Categories)
                {
                    if (!(_doc.ResolveObject(resources[category]) is PdfDictionary names) || whole.Contains(names)) continue;
                    if (!_kept.TryGetValue(names, out HashSet<string> kept))
                        _kept[names] = kept = new HashSet<string>(StringComparer.Ordinal);
                    if (byCategory != null && byCategory.TryGetValue(category, out HashSet<string> drawn)) kept.UnionWith(drawn);
                }
            }
        }
    }
}
