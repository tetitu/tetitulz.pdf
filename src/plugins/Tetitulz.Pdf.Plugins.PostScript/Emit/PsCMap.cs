// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// A CMap the program built with the <c>CIDInit</c> operators (PLRM §5.11.4) and defined as a
    /// CMap resource: its name, its character collection, its writing mode, and its mappings written
    /// back as a CMap program in Adobe Technical Note #5014's format — the program an embedded CMap
    /// stream holds (ISO 32000-2 §9.7.5.4, whose example this follows) — read by Core's
    /// <see cref="EncodingCMap"/>, so a code is split and mapped here as the reader will.
    ///
    /// <para><b>A <c>usecmap</c> stays a reference</b>: one naming a CMap the program defined is
    /// answered with that CMap's program, and is embedded as its own stream under <c>/UseCMap</c>;
    /// one naming a predefined CMap is answered by the registry and written as the name.</para>
    /// </summary>
    internal sealed class PsCMap
    {
        /// <summary>The mappings the operators gave, in the order given.</summary>
        internal sealed class Entries
        {
            public readonly List<(byte[] Lo, byte[] Hi)> Codespaces = new();
            public readonly List<(byte[] Lo, byte[] Hi, int Cid)> CidRanges = new();
            public readonly List<(byte[] Code, int Cid)> CidChars = new();
            public readonly List<(byte[] Lo, byte[] Hi, int Cid)> NotdefRanges = new();
            public readonly List<(byte[] Code, int Cid)> NotdefChars = new();

            /// <summary>How many entries all five hold.</summary>
            public int Count => Codespaces.Count + CidRanges.Count + CidChars.Count + NotdefRanges.Count + NotdefChars.Count;
        }

        private PsCMap(string name, string registry, string ordering, int supplement, int wMode,
                       string useCMap, PsCMap parent, byte[] program, int depth)
        {
            Name = name;
            Registry = registry;
            Ordering = ordering;
            Supplement = supplement;
            WMode = wMode;
            UseCMap = useCMap;
            Parent = parent;
            Program = program;
            Depth = depth;
            Codec = EncodingCMap.Parse(program, ProgramOfAncestor);
        }

        /// <summary>The name it is defined under.</summary>
        public string Name { get; }

        /// <summary>Its <c>/CIDSystemInfo</c>'s registry.</summary>
        public string Registry { get; }

        /// <summary>Its <c>/CIDSystemInfo</c>'s ordering.</summary>
        public string Ordering { get; }

        /// <summary>Its <c>/CIDSystemInfo</c>'s supplement.</summary>
        public int Supplement { get; }

        /// <summary>Its <c>/WMode</c>: 0 horizontal, 1 vertical.</summary>
        public int WMode { get; }

        /// <summary>The CMap its <c>usecmap</c> named, or null.</summary>
        public string UseCMap { get; }

        /// <summary>The program-defined CMap <see cref="UseCMap"/> names, or null when it names a predefined one or none.</summary>
        public PsCMap Parent { get; }

        /// <summary>How many CMaps the reader follows <c>usecmap</c> through from it: the program-defined ones it uses in turn, and a predefined one at their end; 0 for none.</summary>
        public int Depth { get; }

        /// <summary>The CMap program written from its entries.</summary>
        public byte[] Program { get; }

        /// <summary>Its codespace and mappings as Core reads them.</summary>
        public EncodingCMap Codec { get; }

        private byte[] ProgramOfAncestor(string name)
        {
            for (PsCMap p = Parent; p != null; p = p.Parent)
                if (p.Name == name) return p.Program;
            return null;
        }

        /// <summary>
        /// The CMap <paramref name="entries"/> make under <paramref name="name"/>, using
        /// <paramref name="useCMap"/> — <paramref name="parent"/> when the program defined it.
        /// </summary>
        public static PsCMap Build(string name, string registry, string ordering, int supplement, int wMode,
                                   string useCMap, PsCMap parent, Entries entries)
        {
            var text = new StringBuilder(256 + entries.Count * 24);
            text.Append("%!PS-Adobe-3.0 Resource-CMap\n");
            text.Append("%%DocumentNeededResources: ProcSet (CIDInit)\n");
            text.Append("%%IncludeResource: ProcSet (CIDInit)\n");
            text.Append("%%EndComments\n");
            text.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
            if (useCMap != null) text.Append(NameToken(useCMap)).Append(" usecmap\n");
            text.Append("/CIDSystemInfo 3 dict dup begin\n");
            text.Append("/Registry ").Append(HexString(Core.Text.Latin1Text.Encode(registry))).Append(" def\n");
            text.Append("/Ordering ").Append(HexString(Core.Text.Latin1Text.Encode(ordering))).Append(" def\n");
            text.Append("/Supplement ").Append(supplement.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" def\n");
            text.Append("end def\n");
            text.Append("/CMapName ").Append(NameToken(name)).Append(" def\n");
            text.Append("/CMapType 1 def\n");
            text.Append("/WMode ").Append(wMode == 1 ? '1' : '0').Append(" def\n");

            Section(text, "codespacerange", entries.Codespaces, (b, e) => b.Append(Hex(e.Lo)).Append(' ').Append(Hex(e.Hi)));
            Section(text, "notdefrange", entries.NotdefRanges, (b, e) => b.Append(Hex(e.Lo)).Append(' ').Append(Hex(e.Hi)).Append(' ').Append(e.Cid));
            Section(text, "notdefchar", entries.NotdefChars, (b, e) => b.Append(Hex(e.Code)).Append(' ').Append(e.Cid));
            Section(text, "cidrange", entries.CidRanges, (b, e) => b.Append(Hex(e.Lo)).Append(' ').Append(Hex(e.Hi)).Append(' ').Append(e.Cid));
            Section(text, "cidchar", entries.CidChars, (b, e) => b.Append(Hex(e.Code)).Append(' ').Append(e.Cid));

            text.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n%%EOF\n");
            return new PsCMap(name, registry, ordering, supplement, wMode, useCMap, parent,
                              Encoding.ASCII.GetBytes(text.ToString()),
                              parent != null ? parent.Depth + 1 : useCMap != null ? 1 : 0);
        }

        /// <summary>
        /// One kind of entry as TN 5014 writes it: <c>n begin… … end…</c>, at most 100 entries a
        /// block, as ISO 32000-2 §9.7.5.4's example and Adobe's CMap files have them.
        /// </summary>
        private static void Section<T>(StringBuilder text, string kind, List<T> entries, Action<StringBuilder, T> write)
        {
            for (int start = 0; start < entries.Count; start += 100)
            {
                int n = Math.Min(100, entries.Count - start);
                text.Append(n).Append(" begin").Append(kind).Append('\n');
                for (int k = start; k < start + n; k++)
                {
                    write(text, entries[k]);
                    text.Append('\n');
                }
                text.Append("end").Append(kind).Append('\n');
            }
        }

        /// <summary>A code as a hexadecimal string.</summary>
        private static string Hex(byte[] code)
        {
            var b = new StringBuilder(2 + code.Length * 2).Append('<');
            foreach (byte x in code) b.Append(x.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            return b.Append('>').ToString();
        }

        private static string HexString(byte[] bytes) => Hex(bytes);

        /// <summary>
        /// A name as a literal name token, or — holding a character a name token cannot, a
        /// delimiter, white space or a byte outside printable ASCII — as its bytes through <c>cvn</c>,
        /// so the program reads the name it was given.
        /// </summary>
        private static string NameToken(string name)
            => IsRegularName(name) ? "/" + name : HexString(Core.Text.Latin1Text.Encode(name)) + " cvn";

        /// <summary>Whether <paramref name="name"/> is written as a literal name: printable ASCII, no delimiter, not empty.</summary>
        internal static bool IsRegularName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name)
                if (c <= ' ' || c > '~' || "()<>[]{}/%#".IndexOf(c) >= 0) return false;
            return true;
        }

        /// <summary>How many character codes <paramref name="bytes"/> holds under this CMap.</summary>
        public int CountCodes(byte[] bytes) => bytes == null || bytes.Length == 0 ? 0 : Codec.Decode(Ordering, bytes).Count;

        /// <summary><paramref name="bytes"/> as the codes this CMap splits it into.</summary>
        public List<byte[]> SplitCodes(byte[] bytes)
        {
            var codes = new List<byte[]>();
            if (bytes == null) return codes;
            int at = 0;
            foreach (var code in Codec.Decode(Ordering, bytes))
            {
                var one = new byte[code.Length];
                Array.Copy(bytes, at, one, 0, code.Length);
                codes.Add(one);
                at += code.Length;
            }
            return codes;
        }
    }
}
