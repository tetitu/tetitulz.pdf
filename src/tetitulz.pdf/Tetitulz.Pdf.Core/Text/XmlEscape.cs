// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Escapes text for insertion into XML character data or an attribute value, in the
    /// <b>five-entity</b> dialect: <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&quot;</c> and
    /// <c>&apos;</c>.
    ///
    /// <para><b>Which dialect matters, and this type owns exactly one.</b> Seven escapers exist in
    /// this tree across four dialects, and they are not interchangeable — <c>&amp;apos;</c> is not a
    /// valid HTML 4 entity, so "fixing" an HTML escaper to match this one is a regression, not a
    /// consolidation. Only same-dialect XML callers belong here; see the remarks for the list that
    /// deliberately stays out.</para>
    ///
    /// <para><b>It is in Core rather than Base because a plugin has to reach it.</b> Base's
    /// <c>SvgSyntax.XmlEsc</c> is <c>internal</c> and Core's <c>InternalsVisibleTo</c> names no
    /// plugin, so the conformance converters could not have shared it. <c>PdfStringEscape</c> — the
    /// PDF-literal analogue, which replaced eight copies of §7.3.4.2 — is the precedent.</para>
    /// </summary>
    /// <remarks>
    /// <b>No static fields, deliberately.</b> Like <c>SvgSyntax</c>, this type has no type
    /// initializer, so nothing that calls it can be drawn into a type-initialization cycle — the
    /// failure that once crashed the Base test host mid-run and silently dropped ~190 tests.
    ///
    /// <para><b>Callers that must NOT be migrated here</b>, each answering a different question:</para>
    /// <list type="bullet">
    /// <item><c>Builder/Export/HtmlExporter</c> — five entities but the <b>numeric</b>
    /// <c>&amp;#39;</c>, whose own doc comment records the XSS reason it escapes the apostrophe.</item>
    /// <item><c>Plugins.Conformance/PdfHtmlConverter</c> and <c>Builder/Export/OdtRunStyleTable</c>
    /// — four entities, no apostrophe.</item>
    /// <item><c>Builder/Export/OdtExporter</c> — three entities, neither quote.</item>
    /// </list>
    /// </remarks>
    public static class XmlEscape
    {
        /// <summary>
        /// <paramref name="s"/> with the five XML entities escaped. A <c>null</c> input yields the
        /// empty string.
        /// </summary>
        /// <remarks>
        /// <b>The null guard is the one behavioural difference the merge had to settle.</b>
        /// <c>SvgSyntax.XmlEsc</c> had it; the two conformance converters did not and would have
        /// thrown a <see cref="System.NullReferenceException"/> — never a contract, and never
        /// reachable either, since every call site there passes <c>?? ""</c> or a value from a
        /// <c>switch</c> with a total fallback. Taking the guarded behaviour is strictly safer and
        /// changes nothing any caller can observe today.
        /// </remarks>
        public static string EscapeText(string s)
        {
            if (s == null) return "";

            return StripUnrepresentable(s)
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        /// <summary>
        /// <paramref name="s"/> with every character XML 1.0 cannot carry removed.
        /// </summary>
        /// <remarks>
        /// <para><b>Escaping is not the question here — representability is.</b> XML 1.0 §2.2
        /// defines <c>Char</c> as <c>#x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] |
        /// [#x10000-#x10FFFF]</c>, and a character outside that set has <i>no spelling at all</i>:
        /// <c>&amp;#1;</c> is as ill-formed as a raw <c>0x01</c>. So the five-entity pass could not
        /// have fixed this and a caller could not have worked around it.</para>
        ///
        /// <para><b>What it broke.</b> One such byte makes the entire document unparseable, not
        /// just the element carrying it — which is how a PDF→SVG export of a composite font with no
        /// <c>/ToUnicode</c> produced a file every XML reader rejected. There the decoded "text" is
        /// the raw code units, and a CID of 1 is an ordinary thing for a subset font to use.</para>
        ///
        /// <para><b>Dropped rather than substituted.</b> None of these characters has a glyph, so
        /// removing one changes no rendering; U+FFFD in their place would put a visible box into a
        /// visible <c>&lt;text&gt;</c> element and lengthen every string a search runs over. Lone
        /// surrogates are dropped for the same reason — a half of a pair is not a character, and
        /// <c>[#x10000-#x10FFFF]</c> admits only complete ones.</para>
        ///
        /// <para>The scan allocates nothing when the string is already representable, which is
        /// every string but a handful.</para>
        /// </remarks>
        private static string StripUnrepresentable(string s) => XmlLegalText.Remove(s);
    }
}
