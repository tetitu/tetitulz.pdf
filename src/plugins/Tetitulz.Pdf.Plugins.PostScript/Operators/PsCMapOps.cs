// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// The CMap operators of the <c>CIDInit</c> procedure set (PLRM §5.11.4 and §8.2): <c>begincmap</c>
    /// … <c>endcmap</c> collect a CMap's codespace ranges and its CID and notdef mappings, each
    /// <c>n begin…</c> taking the <c>n</c> entries its <c>end…</c> pops; <c>/CMap defineresource</c>
    /// then makes the dictionary they were collected in a <see cref="PsCMap"/>, which
    /// <c>composefont</c> composes a font with, the text operators decode its codes through, and the
    /// collector embeds as the Type0 font's <c>/Encoding</c> stream.
    ///
    /// <para><b>What a PDF encoding CMap cannot hold is consumed and said</b> (ISO 32000-2 §9.7.5.4):
    /// mappings for a font other than font 0 (<c>usefont</c>), the base-font mappings
    /// (<c>beginbfchar</c>, <c>beginbfrange</c>), a font's matrix (<c>beginusematrix</c>) and a
    /// rearranged font. A PDF Type 0 font has one descendant, which is font 0.</para>
    /// </summary>
    internal static class PsCMapOps
    {
        /// <summary>The CMap being collected between <c>begincmap</c> and <c>endcmap</c>.</summary>
        private sealed class Builder
        {
            public readonly PsCMap.Entries Entries = new();
            public int Font;
            public string UseCMap;
            public PsCMap Parent;
            public string OpenKind;
            public int OpenCount;
            public int Dropped;
            public string DroppedWhat;
        }

        /// <summary>The CMap <c>begincmap</c> opened, or null. <c>[ThreadStatic]</c>, cleared per conversion.</summary>
        [ThreadStatic] private static Builder _building;

        /// <summary>
        /// Each CMap dictionary <c>endcmap</c> closed, by the dictionary itself (a CLR object cannot go
        /// into a <see cref="PsDict"/>), until <c>defineresource</c> defines it. <c>[ThreadStatic]</c>,
        /// cleared per conversion.
        /// </summary>
        [ThreadStatic] private static Dictionary<PsDict, Builder> _closed;

        /// <summary>Each CMap dictionary defined as a resource, by the dictionary. <c>[ThreadStatic]</c>, cleared per conversion.</summary>
        [ThreadStatic] private static Dictionary<PsDict, PsCMap> _defined;

        internal static void ResetPerConversionState()
        {
            _building = null;
            _closed = null;
            _defined = null;
        }

        /// <summary>The CMap operators, added to a <c>CIDInit</c> procedure set.</summary>
        internal static void AddTo(PsDict procedures)
        {
            void Op(string name, Action<PsInterpreter> run)
                => procedures.PutName(name, PsObject.Op(new PsOperator(name, o => run((PsInterpreter)o))));

            Op("begincmap", _ => _building = new Builder());
            Op("endcmap", EndCMap);
            Op("usecmap", UseCMap);
            Op("usefont", i => Open(i).Font = i.Operands.PopInt("usefont"));
            foreach (string kind in new[] { "codespacerange", "cidrange", "cidchar", "notdefrange", "notdefchar", "bfrange", "bfchar" })
            {
                string k = kind;
                Op("begin" + k, i => Begin(i, k));
                Op("end" + k, i => End(i, k));
            }
            Op("beginusematrix", i => { i.Operands.PopInt("beginusematrix"); Drop(Open(i), 1, "a font matrix (beginusematrix)"); });
            Op("endusematrix", i => i.Operands.PopArray("endusematrix"));
            Op("beginrearrangedfont", i =>
            {
                i.Operands.PopArray("beginrearrangedfont");
                i.Operands.Pop();
                PostScriptDiagnostics.ReportUnimplemented("beginrearrangedfont");
            });
            Op("endrearrangedfont", _ => { });
        }

        /// <summary>The CMap being collected; one a program fills without <c>begincmap</c> is opened for it.</summary>
        private static Builder Open(PsInterpreter i) => _building ??= new Builder();

        /// <summary><c>n begin…</c>: the count its <c>end…</c> takes, the entries charged now.</summary>
        private static void Begin(PsInterpreter i, string kind)
        {
            string op = "begin" + kind;
            int n = i.Operands.PopInt(op);
            if (n < 0) throw new PsErrorException(PsErrorKind.RangeCheck, op, "a negative entry count");
            Builder b = Open(i);
            if ((long)b.Entries.Count + n > PsLimits.MaxCMapEntries)
                throw new PsErrorException(PsErrorKind.LimitCheck, op,
                    $"a CMap of more than {PsLimits.MaxCMapEntries} entries (PsLimits.MaxCMapEntries); the cap was reached");
            i.ChargeAllocation((long)n * BytesPerEntry, op);
            b.OpenKind = kind;
            b.OpenCount = n;
        }

        /// <summary>What one entry is charged: its codes, its CID and the program text written for it.</summary>
        private const int BytesPerEntry = 64;

        /// <summary><c>entries… end…</c>: the <c>n</c> entries its <c>begin…</c> counted, in the order written.</summary>
        private static void End(PsInterpreter i, string kind)
        {
            string op = "end" + kind;
            Builder b = Open(i);
            if (b.OpenKind != kind) throw new PsErrorException(PsErrorKind.RangeCheck, op, $"no begin{kind} is open");
            int n = b.OpenCount;
            b.OpenKind = null;

            bool triple = kind.EndsWith("range", StringComparison.Ordinal) && kind != "codespacerange";
            int arity = triple ? 3 : 2;
            if ((long)n * arity > i.Operands.Count)
                throw new PsErrorException(PsErrorKind.StackUnderflow, op, $"{n} entries were declared");

            var items = new PsObject[n * arity];
            for (int k = items.Length - 1; k >= 0; k--) items[k] = i.Operands.Pop();
            i.Tick(1 + items.Length / 64);

            if (kind is "bfchar" or "bfrange")
            {
                Drop(b, n, "base-font mappings (begin" + kind + ")");
                return;
            }
            bool forOtherFont = kind != "codespacerange" && b.Font != 0;
            for (int e = 0; e < n; e++)
            {
                int at = e * arity;
                byte[] first = Code(items[at], op);
                if (kind == "codespacerange")
                {
                    b.Entries.Codespaces.Add((first, SameWidth(first, Code(items[at + 1], op), op)));
                    continue;
                }
                if (triple)
                {
                    byte[] hi = SameWidth(first, Code(items[at + 1], op), op);
                    int cid = Cid(items[at + 2], op);
                    if (forOtherFont) continue;
                    (kind == "cidrange" ? b.Entries.CidRanges : b.Entries.NotdefRanges).Add((first, hi, cid));
                }
                else
                {
                    int cid = Cid(items[at + 1], op);
                    if (forOtherFont) continue;
                    (kind == "cidchar" ? b.Entries.CidChars : b.Entries.NotdefChars).Add((first, cid));
                }
            }
            if (forOtherFont) Drop(b, n, $"mappings for font {b.Font} (usefont)");
        }

        private static void Drop(Builder b, int count, string what)
        {
            b.Dropped += count;
            b.DroppedWhat ??= what;
        }

        /// <summary>A code: a string of one to four bytes (PLRM: no longer than an integer).</summary>
        private static byte[] Code(PsObject o, string op)
        {
            if (o.Type != PsType.String) throw new PsErrorException(PsErrorKind.TypeCheck, op, "a code is not a string");
            PsString s = o.AsString();
            if (s.Length < 1 || s.Length > 4) throw new PsErrorException(PsErrorKind.RangeCheck, op, "a code is not one to four bytes long");
            return s.ToArray();
        }

        /// <summary><paramref name="hi"/>, a range's upper bound, refused unless as long as its lower one.</summary>
        private static byte[] SameWidth(byte[] lo, byte[] hi, string op)
            => hi.Length == lo.Length ? hi
               : throw new PsErrorException(PsErrorKind.RangeCheck, op, "a range's bounds differ in length");

        private static int Cid(PsObject o, string op)
        {
            if (o.Type != PsType.Integer) throw new PsErrorException(PsErrorKind.TypeCheck, op, "a CID is not an integer");
            long cid = o.AsLong();
            return cid is >= 0 and <= int.MaxValue ? (int)cid
                : throw new PsErrorException(PsErrorKind.RangeCheck, op, "a CID is negative or past an integer");
        }

        /// <summary>
        /// <c>key usecmap</c>: the CMap this one takes the mappings it does not give from — one the
        /// program defined, or a predefined one, kept by name.
        /// </summary>
        private static void UseCMap(PsInterpreter i)
        {
            PsObject key = i.Operands.Pop();
            string name = KeyText(i, key) ?? throw new PsErrorException(PsErrorKind.TypeCheck, "usecmap", "the key is not a name or a string");
            Builder b = Open(i);
            PsCMap parent = Named(i, key).CMap;
            if (parent != null && !PsCMap.IsRegularName(name))
            {
                PostScriptDiagnostics.Report("PsCMapOps.UseCMap",
                    "a CMap used a CMap whose name a CMap program cannot write as a name; its mappings were not taken");
                return;
            }
            // The reader follows no more usecmap levels than this (CMapCodec), so a deeper chain would
            // decode short of what the program defined; refused rather than carried so.
            if (parent != null && parent.Depth + 1 > Core.Constants.PdfLimits.MaxCMapUsecmapDepth)
                throw new PsErrorException(PsErrorKind.LimitCheck, "usecmap",
                    $"CMaps used through more than {Core.Constants.PdfLimits.MaxCMapUsecmapDepth} levels (PdfLimits.MaxCMapUsecmapDepth); the cap was reached");
            if (parent == null && !PsCidFonts.IsKnownCMap(name))
                PostScriptDiagnostics.Report("PsCMapOps.UseCMap",
                    $"a CMap used '{PsTypeOps.Ellipsis(name)}', which is neither a CMap the program defined nor a registered predefined one; " +
                    "its mappings are left to the reader");
            b.UseCMap = name;
            b.Parent = parent;
        }

        /// <summary><c>endcmap</c>: the collected CMap kept with the current dictionary, the CMap dictionary, for <c>defineresource</c>.</summary>
        private static void EndCMap(PsInterpreter i)
        {
            Builder b = Open(i);
            _building = null;
            _closed ??= new Dictionary<PsDict, Builder>(PdfReferenceEquality.Instance);
            _closed[i.Dicts.Current] = b;
        }

        /// <summary>
        /// <c>key dict /CMap defineresource</c>: a dictionary <c>endcmap</c> closed becomes the CMap
        /// <paramref name="key"/> names — its <c>/CIDSystemInfo</c> (a dictionary, or an array whose
        /// first is font 0's) and <c>/WMode</c> read now, as the program set them.
        /// </summary>
        internal static void Define(PsInterpreter i, PsObject key, PsDict dict)
        {
            if (_closed == null || !_closed.TryGetValue(dict, out Builder b) || KeyText(i, key) is not { } name) return;

            PsDict info = dict.TryGetName("CIDSystemInfo", out PsObject si) ? si.Type switch
            {
                PsType.Dictionary => si.AsDict(),
                PsType.Array when si.AsArray().Length > 0 && si.AsArray()[0].Type == PsType.Dictionary => si.AsArray()[0].AsDict(),
                _ => null,
            } : null;
            string registry = Text(info, "Registry"), ordering = Text(info, "Ordering");
            int supplement = info != null && info.TryGetName("Supplement", out PsObject sup) && sup.Type == PsType.Integer ? (int)Math.Max(0, Math.Min(int.MaxValue, sup.AsLong())) : 0;
            if (registry == null || ordering == null)
            {
                PostScriptDiagnostics.Report("PsCMapOps.Define",
                    $"CMap '{PsTypeOps.Ellipsis(name)}' declares no character collection (/CIDSystemInfo); Adobe-Identity-0 was written for it");
                registry = "Adobe";
                ordering = "Identity";
                supplement = 0;
            }
            int wMode = dict.TryGetName("WMode", out PsObject wm) && wm.Type == PsType.Integer && wm.AsLong() == 1 ? 1 : 0;

            long ancestry = 0;
            for (PsCMap p = b.Parent; p != null; p = p.Parent) ancestry += p.Program.Length;
            // The names are kept and written into the program as hexadecimal: six bytes a character.
            long names = 6L * (name.Length + registry.Length + ordering.Length + (b.UseCMap?.Length ?? 0));
            i.ChargeAllocation((long)b.Entries.Count * BytesPerEntry + ancestry + names, "defineresource");
            i.Tick(1 + (b.Entries.Count * BytesPerEntry + ancestry + names) / 64);

            var cmap = PsCMap.Build(name, registry, ordering, supplement, wMode, b.UseCMap, b.Parent, b.Entries);
            _closed.Remove(dict);
            _defined ??= new Dictionary<PsDict, PsCMap>(PdfReferenceEquality.Instance);
            _defined[dict] = cmap;

            if (b.Dropped > 0)
                PostScriptDiagnostics.Report("PsCMapOps.Define",
                    $"CMap '{PsTypeOps.Ellipsis(name)}' holds {b.Dropped} entries a PDF encoding CMap cannot (ISO 32000-2 §9.7.5.4) — " +
                    $"{b.DroppedWhat} among them — which were not carried; a PDF Type 0 font has one descendant, font 0");
        }

        private static string Text(PsDict dict, string key)
            => dict != null && dict.TryGetName(key, out PsObject o)
                ? o.Type == PsType.String ? o.AsString().AsLatin1() : o.Type == PsType.Name ? o.AsName() : null
                : null;

        /// <summary>
        /// The CMap <paramref name="keyOrDict"/> is — a CMap dictionary the program defined, or the
        /// key of one in the CMap category — and that dictionary; both null otherwise.
        /// </summary>
        internal static (PsCMap CMap, PsDict Dict) Named(PsInterpreter i, PsObject keyOrDict)
        {
            if (_defined == null) return (null, null);
            PsDict dict = keyOrDict.Type == PsType.Dictionary ? keyOrDict.AsDict()
                : keyOrDict.Type is PsType.Name or PsType.String && PsResourceOps.TryFindInstance(i, "CMap", keyOrDict, out PsObject found)
                  && found.Type == PsType.Dictionary ? found.AsDict()
                : null;
            return dict != null && _defined.TryGetValue(dict, out PsCMap cmap) ? (cmap, dict) : (null, null);
        }

        /// <summary>The program-defined CMap the composite font <paramref name="font"/> was composed with (its <c>/CMap</c>), or null.</summary>
        internal static PsCMap Of(PsDict font)
            => _defined != null && font != null && font.TryGetName("CMap", out PsObject cmap) && cmap.Type == PsType.Dictionary
               && _defined.TryGetValue(cmap.AsDict(), out PsCMap defined)
                ? defined
                : null;

        /// <summary>The program-defined CMap the current font was composed with, or null for a predefined one or none.</summary>
        internal static PsCMap Current(PsInterpreter i) => Of(i.CurrentFontDict);

        /// <summary>
        /// <paramref name="bytes"/> as the codes of the current font's CMap <paramref name="cmap"/>, each
        /// with its CID and its text in <paramref name="ordering"/>: through the program's own CMap
        /// when it defined one, else Core's predefined one; null when neither can be read.
        /// </summary>
        internal static IReadOnlyList<Core.Fonts.CidCollectionText.CodeText> Decode(PsInterpreter i, string cmap, string ordering, byte[] bytes)
            => Current(i) is { } defined ? defined.Codec.Decode(ordering, bytes) : Core.Fonts.CidCollectionText.Decode(cmap, ordering, bytes);

        /// <summary>The ordering of the character collection the current font's CMap <paramref name="cmap"/> belongs to.</summary>
        internal static string Ordering(PsInterpreter i, string cmap) => Current(i)?.Ordering ?? PsCidFonts.CollectionFor(cmap).Ordering;

        /// <summary>How many codes <paramref name="bytes"/> holds under the current font's CMap <paramref name="cmap"/>.</summary>
        internal static int CountCodes(PsInterpreter i, string cmap, byte[] bytes)
            => Current(i) is { } defined ? defined.CountCodes(bytes) : PsCidFonts.CountCodes(cmap, bytes);

        /// <summary><paramref name="bytes"/> as the codes of the current font's CMap <paramref name="cmap"/>.</summary>
        internal static List<byte[]> SplitCodes(PsInterpreter i, string cmap, byte[] bytes)
            => Current(i) is { } defined ? defined.SplitCodes(bytes) : PsCidFonts.SplitCodes(cmap, bytes);

        private static string KeyText(PsInterpreter i, PsObject key)
            => key.Type == PsType.Name ? key.AsName()
               : key.Type == PsType.String ? i.LatinFromOperand(key.AsString(), "usecmap")
               : null;
    }
}
