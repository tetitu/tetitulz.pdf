// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// What the codes of a string shown through an Adobe predefined CMap stand for: each code's
    /// length, its CID, and the text the character collection's own Unicode map gives that CID — for a
    /// caller outside Core that holds a composite font by its CMap's name alone (the PostScript
    /// converter's <c>charpath</c>, which outlines the text in a host face).
    /// </summary>
    /// <remarks>
    /// <para><b>The CMaps are the host's registrations</b>: the encoding CMap through
    /// <c>Plugins.CMap</c> (or a host's own <see cref="CMapParser.RegisterPredefinedCMap"/> call), and the
    /// collection's Unicode map — <c>Adobe-Japan1-UCS2</c> and its kin — likewise. Without them nothing
    /// is decoded, and <see cref="Decode(string, string, byte[])"/> answers null. A CMap held as its own
    /// program decodes through <see cref="EncodingCMap"/>.</para>
    /// <para><b>The CID-to-text lookup is the font model's</b> (<see cref="TextOf"/>, which
    /// <c>PdfType0Font</c> asks for a CID font with no <c>/ToUnicode</c>), so an outline drawn for a code
    /// is the character the extracted text says it is.</para>
    /// </remarks>
    public static class CidCollectionText
    {
        /// <summary>One code of a decoded string.</summary>
        public readonly struct CodeText
        {
            internal CodeText(int length, int cid, string text) { Length = length; Cid = cid; Text = text; }

            /// <summary>The bytes the code takes in the string.</summary>
            public int Length { get; }

            /// <summary>The CID the encoding CMap maps the code to.</summary>
            public int Cid { get; }

            /// <summary>The text the collection's Unicode map gives the CID, or null when it gives none.</summary>
            public string Text { get; }
        }

        /// <summary>
        /// <paramref name="bytes"/> as the codes <paramref name="cmapName"/> splits it into, each with its
        /// CID and its text in the collection <paramref name="ordering"/> (<c>Japan1</c>, <c>GB1</c>,
        /// <c>CNS1</c>, <c>Korea1</c>); null when the CMap is neither Identity nor registered.
        /// </summary>
        public static IReadOnlyList<CodeText> Decode(string cmapName, string ordering, byte[] bytes)
        {
            CMapCodec codec = CodecFor(cmapName);
            if (codec == null || bytes == null) return null;
            return Decode(codec, ordering, bytes);
        }

        /// <summary><paramref name="bytes"/> as the codes <paramref name="codec"/> splits it into, each with its CID and its text.</summary>
        internal static IReadOnlyList<CodeText> Decode(CMapCodec codec, string ordering, byte[] bytes)
        {
            var codes = new List<CodeText>(bytes.Length);
            foreach (CMapCodec.Code code in codec.Decode(bytes))
                codes.Add(new CodeText(code.ByteLength, code.Cid, TextOf(ordering, code.Cid)));
            return codes;
        }

        /// <summary>
        /// The text a CID of the collection <paramref name="ordering"/> stands for, through the first of
        /// the collection's registered Unicode maps that names it; null when none does, or for any other
        /// ordering.
        /// </summary>
        internal static string TextOf(string ordering, int cid)
        {
            string[] aliases = ordering switch
            {
                "Japan1" => Japan1Aliases,
                "GB1" => Gb1Aliases,
                "CNS1" => Cns1Aliases,
                "Korea1" => Korea1Aliases,
                _ => null,
            };
            if (aliases == null) return null;
            // These tables are CID→Unicode (CMapParser.LookupPredefined's own contract), so they take the CID.
            foreach (string name in aliases)
            {
                string text = CMapParser.LookupPredefined(name, cid);
                if (text != null) return text;
            }
            return null;
        }

        // Accepted registration names for each ordering (first match wins).
        private static readonly string[] Japan1Aliases = { "Adobe-Japan1-UCS2", "UniJIS-UTF16-H", "UniJIS-UCS2-H" };
        private static readonly string[] Gb1Aliases = { "Adobe-GB1-UCS2", "UniGB-UTF16-H", "UniGB-UCS2-H" };
        private static readonly string[] Cns1Aliases = { "Adobe-CNS1-UCS2", "UniCNS-UTF16-H", "UniCNS-UCS2-H" };
        private static readonly string[] Korea1Aliases = { "Adobe-Korea1-UCS2", "UniKS-UTF16-H", "UniKS-UCS2-H" };

        /// <summary>
        /// A predefined CMap's codec, parsed once a process: the registered CMaps are a fixed set of a
        /// few hundred, and a <c>charpath</c> loop would otherwise parse its CMap every call.
        /// </summary>
        private static CMapCodec CodecFor(string cmapName)
        {
            if (string.IsNullOrEmpty(cmapName)) return null;
            if (Codecs.TryGetValue(cmapName, out CMapCodec known)) return known;
            CMapCodec codec = CMapCodec.FromPredefinedName(cmapName);
            if (codec != null) Codecs[cmapName] = codec;   // an unregistered name is asked again, as it may be registered later
            return codec;
        }

        private static readonly ConcurrentDictionary<string, CMapCodec> Codecs = new ConcurrentDictionary<string, CMapCodec>(System.StringComparer.Ordinal);
    }
}
