// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// A composite font's CMap against the three sentences ISO 14289-1 7.21.3.3 and ISO 14289-2 §8.4.5.4
    /// state alike: a CMap that is not predefined is embedded; an embedded CMap's <c>/WMode</c> is the one
    /// its program declares; a CMap references no CMap but a predefined one — by its <c>/UseCMap</c> entry
    /// or its program's <c>usecmap</c>. PDF/UA-1's UA62 and PDF/UA-2's §8.4.5.4 alike. And the character
    /// collection a Type 0 font's CIDFont shares with its CMap, which ISO 14289-1 7.21.3.1 and ISO 14289-2
    /// §8.4.5.3.1 state alike (<see cref="CharacterCollectionProblem"/>): PDF/UA-1's UA63, PDF/UA-2's
    /// §8.4.5.3.1.
    /// </summary>
    /// <remarks>
    /// <para><b>The predefined CMaps are ISO 32000-1 Table 118 and ISO 32000-2 Table 116, which list the
    /// same 61 names</b> — read from both tables' text, and each but the two Identity CMaps present among
    /// Adobe's published CMap resources, which a reader loads them from.</para>
    /// <para>A CMap program is PostScript-like text, read by one pass over its tokens (<see cref="ReadProgram"/>)
    /// that steps over comments and strings, so a <c>usecmap</c> or a <c>/WMode</c> in either is not taken
    /// for the operator.</para>
    /// </remarks>
    internal static class CMapRules
    {
        /// <summary>The predefined CMap names, in Table 116's order.</summary>
        private static readonly HashSet<string> Predefined = new HashSet<string>(StringComparer.Ordinal)
        {
            // Chinese (Simplified)
            "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBpc-EUC-V", "GBK-EUC-H", "GBK-EUC-V",
            "GBKp-EUC-H", "GBKp-EUC-V", "GBK2K-H", "GBK2K-V", "UniGB-UCS2-H", "UniGB-UCS2-V",
            "UniGB-UTF16-H", "UniGB-UTF16-V",
            // Chinese (Traditional)
            "B5pc-H", "B5pc-V", "HKscs-B5-H", "HKscs-B5-V", "ETen-B5-H", "ETen-B5-V",
            "ETenms-B5-H", "ETenms-B5-V", "CNS-EUC-H", "CNS-EUC-V", "UniCNS-UCS2-H", "UniCNS-UCS2-V",
            "UniCNS-UTF16-H", "UniCNS-UTF16-V",
            // Japanese
            "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90msp-RKSJ-V", "90pv-RKSJ-H",
            "Add-RKSJ-H", "Add-RKSJ-V", "EUC-H", "EUC-V", "Ext-RKSJ-H", "Ext-RKSJ-V", "H", "V",
            "UniJIS-UCS2-H", "UniJIS-UCS2-V", "UniJIS-UCS2-HW-H", "UniJIS-UCS2-HW-V",
            "UniJIS-UTF16-H", "UniJIS-UTF16-V",
            // Korean
            "KSC-EUC-H", "KSC-EUC-V", "KSCms-UHC-H", "KSCms-UHC-V", "KSCms-UHC-HW-H", "KSCms-UHC-HW-V",
            "KSCpc-EUC-H", "UniKS-UCS2-H", "UniKS-UCS2-V", "UniKS-UTF16-H", "UniKS-UTF16-V",
            // Generic
            "Identity-H", "Identity-V",
        };

        /// <summary>How many predefined names there are: the evidence the list is the tables' whole.</summary>
        internal static int PredefinedCount => Predefined.Count;

        /// <summary>Whether <paramref name="name"/> is one of the predefined CMaps.</summary>
        internal static bool IsPredefined(string name) => name != null && Predefined.Contains(name);

        /// <summary>
        /// What is wrong with a Type 0 font's CMap, or <see langword="null"/>; <paramref name="unjudged"/>
        /// says why an embedded CMap's program could not be read. A font that is not Type 0, or has no
        /// <c>/Encoding</c> of either form, has no CMap to judge.
        /// </summary>
        internal static string WhatIsWrong(PdfDocument doc, PdfDictionary font, out string unjudged)
        {
            unjudged = null;
            if (doc == null || font == null) return null;
            Func<PdfObject, PdfObject> resolve = doc.ResolveObject;
            if ((resolve(font[PdfName.Subtype]) as PdfName)?.Value != "Type0") return null;

            PdfObject encoding = resolve(font[PdfName.Encoding]);
            if (encoding is PdfName named)
                return IsPredefined(named.Value)
                    ? null
                    : $"its /Encoding names the CMap /{named.Value}, which is not a predefined CMap, and it is not embedded";
            if (!(encoding is PdfStream cmap)) return null;

            var problems = new List<string>();
            PdfObject useCMap = resolve(cmap[PdfName.UseCMap]);
            if (useCMap is PdfStream)
                problems.Add("its embedded CMap's /UseCMap references another embedded CMap");
            else if (useCMap is PdfName parent && !IsPredefined(parent.Value))
                problems.Add($"its embedded CMap's /UseCMap names /{parent.Value}, which is not a predefined CMap");

            byte[] program;
            try { program = FontStreamBytes.CMap(cmap); }
            catch (Exception ex)
            {
                unjudged = "its embedded CMap's program could not be decoded (" + ex.Message + "), so its /WMode and usecmap were not read";
                return problems.Count > 0 ? string.Join("; ", problems) : null;
            }

            Program read = ReadProgram(program);
            int dictionaryWMode = (resolve(cmap[PdfName.WMode]) as PdfNumber)?.IntValue ?? 0;
            if (read.WMode.HasValue && read.WMode.Value != dictionaryWMode)
                problems.Add(string.Format(CultureInfo.InvariantCulture,
                    "its embedded CMap's /WMode entry is {0} and its program declares {1}", dictionaryWMode, read.WMode.Value));
            foreach (string used in read.UsedCMaps)
            {
                if (used == null) problems.Add("its embedded CMap's program calls usecmap on an operand that is not a CMap name");
                else if (!IsPredefined(used)) problems.Add($"its embedded CMap's program uses the CMap /{used}, which is not a predefined CMap");
            }
            return problems.Count > 0 ? string.Join("; ", problems) : null;
        }

        /// <summary>What a CMap program declares: its <c>/WMode</c>, and the operand of each <c>usecmap</c>.</summary>
        internal sealed class Program
        {
            /// <summary>The integer after the first <c>/WMode</c> name, or <see langword="null"/> when none follows one.</summary>
            internal int? WMode;

            /// <summary>Each <c>usecmap</c>'s operand name, <see langword="null"/> for an operand that is not a name.</summary>
            internal readonly List<string> UsedCMaps = new List<string>();

            /// <summary>The <c>/CIDSystemInfo</c>'s <c>/Registry</c> string, or <see langword="null"/>.</summary>
            internal string Registry;

            /// <summary>The <c>/CIDSystemInfo</c>'s <c>/Ordering</c> string, or <see langword="null"/>.</summary>
            internal string Ordering;

            /// <summary>The <c>/CIDSystemInfo</c>'s <c>/Supplement</c> integer, or <see langword="null"/>.</summary>
            internal int? Supplement;
        }

        /// <summary>
        /// One pass over a CMap program's tokens (PostScript syntax, PLRM §3.2): a comment runs to the line's
        /// end, a literal string's parentheses nest and a backslash escapes, a hexadecimal string runs to its
        /// <c>&gt;</c>. Linear in the program's length, which <see cref="FontStreamBytes.CMap"/> bounds. The
        /// first value after each of <c>/WMode</c>, and — once <c>/CIDSystemInfo</c> has been named —
        /// <c>/Registry</c>, <c>/Ordering</c> and <c>/Supplement</c>, is the program's.
        /// </summary>
        internal static Program ReadProgram(byte[] data)
        {
            var read = new Program();
            if (data == null) return read;
            string text = Encoding.ASCII.GetString(data);
            int n = text.Length;
            int i = 0;
            string previous = null;       // the token before this one, a name with its slash
            bool inSystemInfo = false;

            while (i < n)
            {
                char c = text[i];
                if (c == '%')
                {
                    while (i < n && text[i] != '\n' && text[i] != '\r') i++;
                    continue;
                }
                if (IsWhiteSpace(c)) { i++; continue; }
                if (c == '(')
                {
                    int open = i + 1;
                    int depth = 0;
                    for (; i < n; i++)
                    {
                        if (text[i] == '\\') { i++; continue; }
                        if (text[i] == '(') depth++;
                        else if (text[i] == ')' && --depth == 0) { i++; break; }
                    }
                    string value = text.Substring(open, Math.Max(0, Math.Min(i, n) - 1 - open));
                    if (inSystemInfo && previous == "/Registry" && read.Registry == null) read.Registry = value;
                    if (inSystemInfo && previous == "/Ordering" && read.Ordering == null) read.Ordering = value;
                    previous = "()";
                    continue;
                }
                if (c == '<' && i + 1 < n && text[i + 1] != '<')
                {
                    while (i < n && text[i] != '>') i++;
                    i++;
                    previous = "<>";
                    continue;
                }
                if (IsDelimiter(c) && c != '/')
                {
                    i += (c == '<' || c == '>') && i + 1 < n && text[i + 1] == c ? 2 : 1;
                    previous = c.ToString();
                    continue;
                }

                int start = i;
                if (c == '/') i++;
                while (i < n && !IsWhiteSpace(text[i]) && !IsDelimiter(text[i])) i++;
                string token = text.Substring(start, i - start);

                if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
                {
                    if (previous == "/WMode" && read.WMode == null) read.WMode = number;
                    if (inSystemInfo && previous == "/Supplement" && read.Supplement == null) read.Supplement = number;
                }
                if (token == "usecmap")
                    read.UsedCMaps.Add(previous != null && previous.Length > 1 && previous[0] == '/' ? previous.Substring(1) : null);
                if (token == "/CIDSystemInfo") inSystemInfo = true;

                previous = token;
            }
            return read;
        }

        /// <summary>
        /// ISO 14289-1 7.21.3.1 and ISO 14289-2 §8.4.5.3.1, which state it alike: unless a Type 0 font's
        /// <c>/Encoding</c> is <c>Identity-H</c> or <c>Identity-V</c>, its CIDFont's <c>/CIDSystemInfo</c> has
        /// the CMap's <c>/Registry</c> and <c>/Ordering</c>, and a <c>/Supplement</c> at least the CMap's. What
        /// is wrong, or <see langword="null"/>; <paramref name="unjudged"/> says why the CMap's character
        /// collection could not be read.
        /// </summary>
        /// <remarks>
        /// An embedded CMap's character collection is its stream dictionary's <c>/CIDSystemInfo</c> (ISO
        /// 32000-2 Table 118); a predefined CMap's is the one its CMap program declares, the only place it is
        /// declared (ISO 32000-2 leaves Table 117 empty for it) — read from the program registered under the
        /// name (<see cref="PredefinedCMapRegistry.GetProgram"/>, which <c>Plugins.CMap</c>'s
        /// <c>AdobeCMapPlugin</c> fills with Adobe's published CMaps). A name no one registered is not judged,
        /// and says so. A name that is not predefined is the embedding sentence's
        /// (<see cref="WhatIsWrong"/>); a dictionary missing an entry the comparison needs is not compared.
        /// </remarks>
        internal static string CharacterCollectionProblem(PdfDocument doc, PdfDictionary font, out string unjudged)
        {
            unjudged = null;
            if (doc == null || font == null) return null;
            Func<PdfObject, PdfObject> resolve = doc.ResolveObject;
            if ((resolve(font[PdfName.Subtype]) as PdfName)?.Value != "Type0") return null;

            string registry, ordering;
            int? supplement;
            PdfObject encoding = resolve(font[PdfName.Encoding]);
            if (encoding is PdfName named)
            {
                if (named.Value == "Identity-H" || named.Value == "Identity-V" || !IsPredefined(named.Value)) return null;
                byte[] program = PredefinedCMapRegistry.GetProgram(named.Value);
                if (program == null)
                {
                    unjudged = $"the predefined CMap /{named.Value} declares its character collection in its CMap " +
                               "program, and no program is registered under that name (Plugins.CMap's AdobeCMapPlugin " +
                               "registers Adobe's)";
                    return null;
                }
                Program read = ReadProgram(program);
                if (read.Registry == null || read.Ordering == null || read.Supplement == null)
                {
                    unjudged = $"the program registered for the predefined CMap /{named.Value} declares no complete /CIDSystemInfo";
                    return null;
                }
                (registry, ordering, supplement) = (read.Registry, read.Ordering, read.Supplement);
            }
            else if (encoding is PdfStream cmap && resolve(cmap[PdfName.CIDSystemInfo]) is PdfDictionary cmapInfo)
            {
                registry = (resolve(cmapInfo[PdfName.Registry]) as PdfString)?.GetText();
                ordering = (resolve(cmapInfo[PdfName.Ordering]) as PdfString)?.GetText();
                supplement = (resolve(cmapInfo[PdfName.Supplement]) as PdfNumber)?.IntValue;
            }
            else return null;

            var descendants = resolve(font[PdfName.DescendantFonts]) as PdfArray;
            var cidFont = descendants != null && descendants.Count > 0 ? resolve(descendants[0]) as PdfDictionary : null;
            if (!(resolve(cidFont?[PdfName.CIDSystemInfo]) is PdfDictionary cidInfo)) return null;
            string cidRegistry = (resolve(cidInfo[PdfName.Registry]) as PdfString)?.GetText();
            string cidOrdering = (resolve(cidInfo[PdfName.Ordering]) as PdfString)?.GetText();
            int? cidSupplement = (resolve(cidInfo[PdfName.Supplement]) as PdfNumber)?.IntValue;
            if (registry == null || ordering == null || supplement == null
                || cidRegistry == null || cidOrdering == null || cidSupplement == null) return null;

            if (registry == cidRegistry && ordering == cidOrdering && cidSupplement.Value >= supplement.Value) return null;
            return string.Format(CultureInfo.InvariantCulture,
                "its CIDFont's character collection is {0}-{1}-{2} and its CMap's {3}-{4}-{5}; the registry and " +
                "ordering are the CMap's and the supplement at least its",
                cidRegistry, cidOrdering, cidSupplement.Value, registry, ordering, supplement.Value);
        }

        private static bool IsWhiteSpace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\0';

        private static bool IsDelimiter(char c)
            => c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
    }
}
