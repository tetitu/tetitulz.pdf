// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Text;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ============================================================= CSS cascade (<style>)

        // Folds the document's stylesheet rules into per-element inline style attributes so the
        // rest of the mapper (which reads style="…") transparently sees the cascade. Inline
        // declarations are appended last so they win; among stylesheet rules, higher specificity
        // then later source order wins. Sources are <style> blocks plus <link rel="stylesheet">
        // files — local ones only inside the base directory, remote ones only through
        // HtmlToPdfOptions.RemoteResourceLoader. Processed in document order.
        private void ApplyStylesheets(IDocument dom)
        {
            var sheets = dom.QuerySelectorAll("style, link");
            if (sheets.Length == 0) return;

            var rules = new List<(int spec, int order, string selector, string decls)>();
            int order = 0;
            foreach (var el in sheets)
            {
                if (rules.Count >= MaxCssRules) break;

                // A sheet's own media — <link media="print">, <style media="screen"> — applies it
                // only for that medium, as an @media block around its rules would. It was not read,
                // so a screen-only sheet styled the print and a print-only one the screen. Checked
                // first, so a sheet that does not apply is not fetched either.
                string? sheetMedia = el.GetAttribute("media");
                if (!string.IsNullOrWhiteSpace(sheetMedia) && !MediaApplies(sheetMedia!, _mediaViewport)) continue;

                string css;
                string? location = null;   // where the sheet is, for its @import targets (a <style> block's is the document's)
                if (string.Equals(el.LocalName, HtmlTags.Style, StringComparison.OrdinalIgnoreCase))
                {
                    css = el.TextContent ?? "";
                }
                else // <link>
                {
                    string? rel = el.GetAttribute("rel");
                    if (rel == null || rel.IndexOf("stylesheet", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string? href = el.GetAttribute("href");
                    bool isRemote = !string.IsNullOrEmpty(href) && IsRemoteUrl(href!);
                    string? loaded = string.IsNullOrEmpty(href) ? null
                                   : isRemote ? LoadRemoteStylesheet(href!, out location)
                                   : LoadLocalStylesheet(href!, out location);
                    if (loaded == null)
                    {
                        // The third silent-drop site, and the most expensive: a whole stylesheet
                        // going missing changes every page, yet the conversion returned a valid PDF
                        // and OnDiagnostic never fired. A remote one has already said why.
                        if (!string.IsNullOrEmpty(href) && !isRemote) ReportResourceDeclined("stylesheet", href!);
                        continue;
                    }
                    css = loaded;   // the loader has added `location` to _importedSheets, so the same
                                    // sheet named again (a cycle, or a repeated <link>) loads no second time
                }
                ExtractRules(css, rules, ref order, nestDepth: 0, location);
            }
            if (rules.Count == 0) return;

            var acc = new Dictionary<IElement, List<(int spec, int order, string decls)>>();
            var pseudo = new Dictionary<(IElement el, string attribute), List<(int spec, int order, string decls)>>();

            // The fold collects one (spec, order, decls) tuple per rule and matching element, and
            // ResolveCascade then reads each tuple's declaration text: both are the product of a rule's
            // length and the elements it matches. This budget bounds that product by the declaration
            // text collected; once it is spent no more matches are taken and the drop is reported once.
            long foldBudget = MaxCssCascadeChars;

            // The document's size and depth, measured once, estimate each selector's match cost
            // below: a descendant combinator walks a matched element's ancestors, so the work is
            // about (elements × combinators × depth) — unbounded by the compound cap alone.
            MeasureDom(dom, out int domCount, out int domDepth);

            foreach (var (spec, ord, selector, decls) in rules)
            {
                if (foldBudget <= 0) break;
                // ::before / ::after are peeled off BEFORE the selector reaches the DOM query, or they
                // would land in the catch below alongside genuinely unsupported selectors, a rule the
                // engine can act on indistinguishable from one it cannot. The base selector is a perfectly ordinary one; only the suffix names
                // the generated box.
                string baseSelector = StripPseudoElement(selector, out string? pseudoAttribute);

                // AngleSharp matches a complex selector by recursion over its compounds and the
                // element's ancestors — exponential in a selector of a thousand descendant
                // combinators over a deep document, which the structure fuzzer's HTML corpus ran for
                // its whole window. No stylesheet a person wrote has thirty-two; one that does is
                // skipped and said so, once. Nor more than thirty-two parentheses, which its parser
                // reads by recursion: a selector of :not( nested a hundred thousand deep counts two
                // compounds, all its functions inside the first.
                int complexity = SelectorComplexity(baseSelector);
                if (complexity > MaxSelectorCompounds
                    || CountOf(baseSelector, '(') > MaxSelectorCompounds)
                {
                    if (!_selectorCapReported)
                    {
                        _selectorCapReported = true;
                        ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                            "A stylesheet rule's selector has more than " + MaxSelectorCompounds + " compound " +
                            "selectors or selector functions; the rule was not applied.");
                    }
                    continue;
                }

                // The compound cap bounds the selector's SHAPE, not its match COST: a selector of a
                // few descendant combinators over a document that nests thousands deep walks every
                // matched element's ancestors and runs for billions of steps. Skip a rule whose
                // estimated work — (elements × combinators × depth) — exceeds the budget; a real
                // document (shallow, modest size) never approaches it.
                int combinators = complexity - 1;
                long matchCost = combinators <= 0 ? domCount : (long)domCount * combinators * domDepth;
                if (matchCost > MaxSelectorMatchWork)
                {
                    if (!_selectorCostReported)
                    {
                        _selectorCostReported = true;
                        ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                            "A stylesheet rule's selector would cost too much to match against this " +
                            "document (its combinators times the document's size and depth); the rule " +
                            "was not applied.");
                    }
                    continue;
                }

                IHtmlCollection<IElement> matches;
                try { matches = dom.QuerySelectorAll(baseSelector); }
                catch { continue; } // genuinely unsupported/invalid selector
                foreach (var el in matches)
                {
                    if (foldBudget <= 0) break;
                    foldBudget -= decls.Length;

                    if (pseudoAttribute != null)
                    {
                        var key = (el, pseudoAttribute);
                        if (!pseudo.TryGetValue(key, out var plist)) { plist = new(); pseudo[key] = plist; }
                        plist.Add((spec, ord, decls));
                        continue;
                    }

                    if (!acc.TryGetValue(el, out var list)) { list = new(); acc[el] = list; }
                    list.Add((spec, ord, decls));
                }
            }

            if (foldBudget <= 0 && !_cssFoldCapReported)
            {
                _cssFoldCapReported = true;
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "The document's CSS cascade exceeded its folding budget (a rule's declarations times " +
                    "the elements it matches); the matches past that point kept only their own inline style.");
            }

            foreach (var kv in acc)
                kv.Key.SetAttribute("style", ResolveCascade(kv.Value, kv.Key.GetAttribute("style")));

            // Resolved through the SAME cascade as an element's own rules — a pseudo-element can be
            // targeted by several selectors of different specificity like anything else — and
            // parked on a private attribute, because the rest of the mapper reads nothing but
            // inline style and this keeps that true.
            foreach (var kv in pseudo)
                kv.Key.el.SetAttribute(kv.Key.attribute, ResolveCascade(kv.Value, null));
        }

        /// <summary>Private attribute carrying a resolved <c>::before</c> declaration block.</summary>
        internal const string BeforeStyleAttribute = "data-tetitulz-before";

        /// <summary>Private attribute carrying a resolved <c>::after</c> declaration block.</summary>
        internal const string AfterStyleAttribute = "data-tetitulz-after";

        /// <summary>Private attribute carrying a resolved <c>::marker</c> declaration block — a list item's marker.</summary>
        internal const string MarkerStyleAttribute = "data-tetitulz-marker";

        /// <summary>
        /// Removes a trailing <c>::before</c> / <c>::after</c> (or their one-colon CSS2 spellings) or
        /// <c>::marker</c> from a selector, naming the private attribute its declarations go to.
        /// </summary>
        /// <remarks>
        /// Only a <b>trailing</b> pseudo-element is recognised, which is all CSS allows: a
        /// pseudo-element may appear once, at the end of the last compound selector. Other
        /// pseudo-elements — <c>::first-line</c>, <c>::selection</c> — are left on the selector so
        /// they still fail the DOM query and are skipped, exactly as before. That is deliberate:
        /// silently treating <c>::first-line</c> as if it styled the whole element would be worse
        /// than ignoring it. <c>::marker</c> styles a list item's marker, which the model carries
        /// (<c>ListItem.MarkerFormat</c>).
        /// </remarks>
        private static string StripPseudoElement(string selector, out string? attribute)
        {
            attribute = null;
            if (string.IsNullOrEmpty(selector)) return selector;

            string trimmed = selector.TrimEnd();
            foreach (var (suffix, target) in new[]
                     {
                         ("::before", BeforeStyleAttribute), ("::after", AfterStyleAttribute),
                         (":before", BeforeStyleAttribute),  (":after", AfterStyleAttribute),
                         ("::marker", MarkerStyleAttribute),
                     })
            {
                if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    string head = trimmed.Substring(0, trimmed.Length - suffix.Length).TrimEnd();
                    if (head.Length == 0) return selector;   // a bare "::before" selects nothing
                    attribute = target;
                    return head;
                }
            }
            return selector;
        }

        /// <summary>
        /// Resolves one element's cascade to a single winning declaration per property and
        /// re-emits it as a normalised <c>style="…"</c> value, so the rest of the mapper keeps
        /// reading nothing but inline style.
        ///
        /// <para>Ranking is <c>(origin, specificity, source order)</c>, where origin is
        /// 0 = stylesheet, 1 = inline, 2 = stylesheet <c>!important</c>, 3 = inline
        /// <c>!important</c> — the CSS 2.1 §6.4.1 order for an author-only cascade. The
        /// declarations are then emitted in ascending rank so that a reader processing them
        /// sequentially, last-wins (which is what <see cref="ApplyInlineStyle"/> does), arrives
        /// at the same answer. That ordering is what keeps shorthand/longhand interaction —
        /// <c>font</c> versus <c>font-size</c> — resolving the way the author wrote it.</para>
        /// </summary>
        private static string ResolveCascade(List<(int spec, int order, string decls)> rules, string? inlineStyle)
        {
            var winners = new Dictionary<string, (int origin, int spec, int order, string prop, string value)>(
                StringComparer.Ordinal);

            void Offer(string prop, string value, bool important, bool inline, int spec, int order)
            {
                int origin = (important ? 2 : 0) + (inline ? 1 : 0);
                string key = DeclarationKey(prop);
                if (winners.TryGetValue(key, out var cur))
                {
                    if (cur.origin > origin) return;
                    if (cur.origin == origin)
                    {
                        if (cur.spec > spec) return;
                        if (cur.spec == spec && cur.order > order) return;
                    }
                }
                winners[key] = (origin, spec, order, key, value);
            }

            foreach (var (spec, order, decls) in rules)
                foreach (var decl in SplitDeclarations(decls))
                    if (TrySplitDeclaration(decl, out string p, out string v, out bool imp))
                        Offer(p, v, imp, inline: false, spec, order);

            // Inline declarations outrank every stylesheet rule of the same importance, so they
            // are offered at maximum specificity and order rather than merely "appended last".
            foreach (var decl in SplitDeclarations(inlineStyle))
                if (TrySplitDeclaration(decl, out string p, out string v, out bool imp))
                    Offer(p, v, imp, inline: true, int.MaxValue, int.MaxValue);

            var ordered = winners.Values.ToList();
            ordered.Sort((a, b) =>
            {
                int c = a.origin.CompareTo(b.origin);
                if (c != 0) return c;
                c = a.spec.CompareTo(b.spec);
                if (c != 0) return c;
                return a.order.CompareTo(b.order);
            });

            var sb = new StringBuilder();
            foreach (var w in ordered)
                sb.Append(w.prop).Append(':').Append(w.value).Append(';');
            return sb.ToString();
        }

        // Resolves CSS custom properties: collects every `--name: value` declaration (from the
        // already-merged element styles) into a global map, then substitutes `var(--name[, fallback])`
        // references in all element styles. Runs after ApplyStylesheets. Documented approximation:
        // custom properties are treated as document-global (true per-subtree scoping is not modelled),
        // which covers the dominant `:root { --x: … }` pattern.
        private void ResolveCssVariables(IDocument dom)
        {
            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var el in dom.All)
            {
                string? style = el.GetAttribute("style");
                if (string.IsNullOrEmpty(style) || style!.IndexOf("--", StringComparison.Ordinal) < 0) continue;
                foreach (var decl in SplitDeclarations(style))
                    if (TrySplitDeclaration(decl, out string name, out string value, out _)
                        && name.StartsWith("--", StringComparison.Ordinal))
                        vars[name] = value;
            }

            // The whole pass shares one character budget (a one-cell box, so a recursive fallback shares
            // it): each value's expansion doubles the previous one, so a per-value or per-depth cap bounds
            // nothing — the count of values is the attacker's too. Past it, a var() is left unexpanded (an
            // unset value to the rest of the mapper).
            long[] budget = { MaxCssVarExpansionChars };

            // Resolve var() inside the custom-property values themselves (transitive, bounded).
            foreach (var key in vars.Keys.ToList()) vars[key] = SubstituteVars(vars[key], vars, 0, budget);

            // Substitute var() references everywhere (still runs with an empty map so that a
            // var(--undefined, fallback) reference resolves to its fallback).
            foreach (var el in dom.All)
            {
                string? style = el.GetAttribute("style");
                if (string.IsNullOrEmpty(style) || style!.IndexOf("var(", StringComparison.OrdinalIgnoreCase) < 0) continue;
                el.SetAttribute("style", SubstituteVars(style, vars, 0, budget));
            }

            if (budget[0] <= 0 && !_cssVarCapReported)
            {
                _cssVarCapReported = true;
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "CSS custom-property substitution exceeded its budget; the var() references past " +
                    "that point were left unresolved.");
            }
        }

        // Replaces var(--name[, fallback]) with the mapped value, or the (recursively resolved)
        // fallback when the property is undefined. Brace-balanced so nested var() in a fallback works.
        // `budget[0]` is the characters this whole pass may still write; once it is spent every append is
        // refused, so an expansion that doubles without bound stops rather than filling memory.
        private static string SubstituteVars(string value, Dictionary<string, string> vars, int depth,
            long[] budget)
        {
            if (depth > 8 || budget[0] <= 0 || value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) < 0)
                return value;
            var sb = new StringBuilder();
            int i = 0;

            // Appends as much of `s` as the budget allows, and reports whether it was cut short.
            bool Emit(string s, int from, int count)
            {
                if (count <= 0) return false;
                if (count > budget[0]) { sb.Append(s, from, (int)budget[0]); budget[0] = 0; return true; }
                sb.Append(s, from, count); budget[0] -= count; return false;
            }

            while (i < value.Length)
            {
                if (budget[0] <= 0) break;
                int idx = value.IndexOf("var(", i, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) { Emit(value, i, value.Length - i); break; }
                if (Emit(value, i, idx - i)) break;
                int open = idx + 3; // the '('
                int d = 0, j = open;
                for (; j < value.Length; j++) { if (value[j] == '(') d++; else if (value[j] == ')') { d--; if (d == 0) break; } }
                if (j >= value.Length) { Emit(value, idx, value.Length - idx); break; } // unbalanced
                string inner = value.Substring(open + 1, j - open - 1);
                int comma = inner.IndexOf(',');
                string name = (comma >= 0 ? inner.Substring(0, comma) : inner).Trim();
                string fallback = comma >= 0 ? inner.Substring(comma + 1).Trim() : "";
                string resolved = vars.TryGetValue(name, out var vv) ? vv : SubstituteVars(fallback, vars, depth + 1, budget);
                if (Emit(resolved, 0, resolved.Length)) break;
                i = j + 1;
            }
            return sb.ToString();
        }

        // Brace-aware splitter: emits (specificity, order, selector, declarationBlock) tuples.
        // @media blocks are evaluated for the conversion's medium against the page viewport
        // (MediaApplies: all and the medium's type apply, the other type is skipped, width/height/
        // orientation are evaluated, other features apply); @supports blocks are assumed supported and
        // flattened; @import brings the named sheet's rules in at its place (ImportStylesheet); @charset and @font-face / @keyframes blocks are skipped.
        //
        // `nestDepth` counts flattened at-rule nesting — an imported sheet's included — and is capped
        // at MaxCssNestingDepth.
        // MaxCssRules is not a substitute: a nesting-only payload adds no rules, so it would recurse
        // as deeply as the <style> text is long — and a StackOverflowException cannot be caught, so
        // a host converting untrusted HTML would die rather than get a bad PDF. Refusing the block
        // leaves the outer loop's `i = k` to skip past it, exactly as a screen-only @media is
        // skipped. (It is NOT named `depth`: that identifier is the brace-nesting counter below,
        // which the block-scanning loop drives to 0 — adding 1 to *that* would be a no-op guard.)
        //
        // `sheetLocation` is where this text came from — a local sheet's full path, a remote one's
        // absolute URL, null for a <style> block — so a relative @import resolves against the sheet
        // that wrote it, as a browser resolves it.
        private void ExtractRules(string css, List<(int, int, string, string)> rules, ref int order, int nestDepth,
            string? sheetLocation = null)
        {
            if (nestDepth > MaxCssNestingDepth)
            {
                if (!_cssNestingCapReported)
                {
                    _cssNestingCapReported = true;
                    ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                        "CSS @media/@supports/@import nesting deeper than " + MaxCssNestingDepth +
                        " was not flattened; the rules inside it were not applied.");
                }
                return;
            }
            css = StripCssComments(css);
            int i = 0, n = css.Length;
            while (i < n)
            {
                if (rules.Count >= MaxCssRules) return;
                while (i < n && char.IsWhiteSpace(css[i])) i++;
                if (i >= n) break;

                if (css[i] == '@')
                {
                    int j = i;
                    while (j < n && css[j] != '{' && css[j] != ';') j++;
                    string at = css.Substring(i, j - i).TrimStart('@').Trim();
                    if (j >= n) break;
                    if (css[j] == ';')
                    {
                        // @import (CSS Cascade 4 §5): the named sheet's rules stand here, in source
                        // order, when its media applies — the one statement at-rule that changes the
                        // cascade. @charset and any other is skipped.
                        if (at.Length > 6 && at.StartsWith("import", StringComparison.OrdinalIgnoreCase)
                            && (char.IsWhiteSpace(at[6]) || at[6] == '"' || at[6] == '\'' || at[6] == 'u' || at[6] == 'U'))
                            ImportStylesheet(at.Substring(6), sheetLocation, rules, ref order, nestDepth);
                        i = j + 1;
                        continue;
                    }
                    int bs = j + 1, depth = 1, k = bs;
                    while (k < n && depth > 0) { if (css[k] == '{') depth++; else if (css[k] == '}') depth--; k++; }
                    string kw = new string(at.TakeWhile(c => !char.IsWhiteSpace(c) && c != '(').ToArray()).ToLowerInvariant();
                    bool flatten = kw == "supports" || kw == "layer"
                        || (kw == "media" && MediaApplies(at.Length > kw.Length ? at.Substring(kw.Length) : "", _mediaViewport));
                    if (kw == "layer" && !_cssLayerReported)
                    {
                        // CSS Cascade 5: an @layer block holds ordinary rules, so they apply — in source
                        // order, where layer order would rank unlayered rules above layered ones first.
                        _cssLayerReported = true;
                        ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                            "CSS @layer blocks were applied in source order; the precedence of one layer over " +
                            "another, and of unlayered rules over layered ones, was not.");
                    }
                    if (flatten)
                        ExtractRules(css.Substring(bs, Math.Max(0, k - 1 - bs)), rules, ref order, nestDepth + 1, sheetLocation);
                    else if (kw == "font-face")
                        CollectFontFace(css.Substring(bs, Math.Max(0, k - 1 - bs)));
                    else if ((kw == "container" || kw == "scope") && !_cssSkippedAtRuleReported)
                    {
                        _cssSkippedAtRuleReported = true;
                        ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                            "A CSS @" + kw + " block was not applied: this engine does not evaluate its condition, " +
                            "so the rules inside it are absent.");
                    }
                    i = k; // else skip the block body (@keyframes / screen-only @media)
                    continue;
                }

                int b = i;
                while (b < n && css[b] != '{') b++;
                if (b >= n) break;
                string selList = css.Substring(i, b - i).Trim();
                int blk = b + 1, d = 1, m = blk;
                while (m < n && d > 0) { if (css[m] == '{') d++; else if (css[m] == '}') d--; m++; }
                string block = css.Substring(blk, Math.Max(0, m - 1 - blk)).Trim();
                i = m;
                if (selList.Length == 0 || block.Length == 0) continue;
                foreach (var sel in selList.Split(','))
                {
                    string s = sel.Trim();
                    if (s.Length == 0) continue;
                    rules.Add((Specificity(s), order++, s, block));
                    if (rules.Count >= MaxCssRules) return;
                }
            }
        }

        /// <summary>
        /// A selector's specificity (Selectors Level 4 §17) as one number that orders as the triple
        /// does: <c>a</c> ids, <c>b</c> classes, attributes and pseudo-classes, <c>c</c> types and
        /// pseudo-elements, each capped at 999 so no count of a lesser term outweighs one of a
        /// greater. Counting characters — every <c>.</c>, <c>[</c> and <c>:</c> as a class, every
        /// bare word as a type — would weigh <c>:not(.a)</c> as two classes, an attribute value with
        /// a dot in it as one, <c>[type=text]</c>'s value as a type, and <c>:where()</c> as whatever
        /// it wraps.
        /// </summary>
        internal static int Specificity(string sel)
        {
            var (a, b, c) = SpecificityOf(sel, 0);
            return Math.Min(a, 999) * 1_000_000 + Math.Min(b, 999) * 1000 + Math.Min(c, 999);
        }

        /// <summary>
        /// The specificity triple of one complex selector. Combinators, the universal selector and
        /// namespace prefixes count nothing; a pseudo-element (<c>::x</c>, or the four legacy
        /// single-colon ones) counts as a type; <c>:not()</c>, <c>:is()</c> and <c>:has()</c> count
        /// their most specific argument and nothing for themselves; <c>:where()</c> counts nothing;
        /// <c>:nth-child(An+B of S)</c> counts one plus <c>S</c>'s most specific selector; an
        /// attribute selector counts one whatever its value holds.
        /// </summary>
        /// <remarks>
        /// A function's argument nested past <see cref="MaxSelectorCompounds"/> counts nothing. The
        /// reader recursed once a nested <c>:not(</c>, before any cap looked at the rule: twenty-five
        /// kilobytes of them overflowed a one-megabyte stack, which cannot be caught, and the host died.
        /// A rule nested that deep holds more parentheses than the cap and is skipped before it is
        /// matched (<see cref="ApplyStylesheets"/>), so the specificity it is given orders nothing.
        /// </remarks>
        private static (int A, int B, int C) SpecificityOf(string sel, int depth)
        {
            if (depth > MaxSelectorCompounds) return (0, 0, 0);
            int a = 0, b = 0, c = 0;
            int i = 0, n = sel.Length;
            while (i < n)
            {
                char ch = sel[i];
                if (ch == '#') { i = SkipIdent(sel, i + 1); a++; }
                else if (ch == '.') { i = SkipIdent(sel, i + 1); b++; }
                else if (ch == '[') { i = SkipBalanced(sel, i, '[', ']'); b++; }
                else if (ch == ':')
                {
                    bool element = i + 1 < n && sel[i + 1] == ':';
                    int nameStart = element ? i + 2 : i + 1;
                    int j = SkipIdent(sel, nameStart);
                    string name = sel.Substring(nameStart, j - nameStart).ToLowerInvariant();
                    string? args = null;
                    if (j < n && sel[j] == '(')
                    {
                        int close = SkipBalanced(sel, j, '(', ')');
                        args = sel.Substring(j + 1, Math.Max(0, close - j - 2));
                        j = close;
                    }
                    if (element || name is "before" or "after" or "first-line" or "first-letter") c++;
                    else if (name is "not" or "is" or "has") Add(MostSpecific(args, depth + 1));
                    else if (name == "where") { }
                    else if (name.StartsWith("nth-", StringComparison.Ordinal) && args != null && OfClause(args) is string list) { b++; Add(MostSpecific(list, depth + 1)); }
                    else b++;
                    i = j;
                }
                else if (IsIdentStart(ch))
                {
                    int j = SkipIdent(sel, i);
                    // `ns|el`: the prefix names a namespace, not a type; `|` itself is skipped below.
                    if (j < n && sel[j] == '|' && (j + 1 >= n || sel[j + 1] != '=')) { i = j; continue; }
                    c++;
                    i = j;
                }
                else i++;   // white space, combinators, `*`, `|`, `,`
            }
            return (a, b, c);

            void Add((int A, int B, int C) t) { a += t.A; b += t.B; c += t.C; }
        }

        /// <summary>How many times <paramref name="c"/> occurs in <paramref name="s"/>.</summary>
        private static int CountOf(string s, char c)
        {
            int count = 0;
            foreach (char ch in s) if (ch == c) count++;
            return count;
        }

        /// <summary>
        /// The element count and greatest nesting depth of the document, in one depth-first pass
        /// (O(elements), no per-element ancestor walk) — the two figures a selector's match-cost
        /// estimate multiplies.
        /// </summary>
        private static void MeasureDom(IDocument dom, out int count, out int depth)
        {
            count = 0;
            depth = 0;
            IElement? root = dom.DocumentElement;
            if (root == null) return;

            var stack = new Stack<(IElement El, int Depth)>();
            stack.Push((root, 1));
            while (stack.Count > 0)
            {
                var (el, d) = stack.Pop();
                count++;
                if (d > depth) depth = d;
                foreach (IElement child in el.Children) stack.Push((child, d + 1));
            }
        }

        /// <summary>The most specific selector of a comma-separated list, as <c>:is()</c> and <c>:not()</c> weigh (§17).</summary>
        private static (int A, int B, int C) MostSpecific(string? list, int depth)
        {
            var best = (A: 0, B: 0, C: 0);
            if (list == null) return best;
            foreach (string part in SplitSelectorList(list))
            {
                var s = SpecificityOf(part, depth);
                if (s.A > best.A || (s.A == best.A && (s.B > best.B || (s.B == best.B && s.C > best.C)))) best = s;
            }
            return best;
        }

        /// <summary>The selector list after <c>of</c> in <c>:nth-child(An+B of S)</c>, or null when there is none.</summary>
        /// <remarks>
        /// One forward scan for the first <c>of</c> standing as a word, white space after it. It was
        /// <c>(?:^|\s)of\s+(.+)$</c>, whose <c>.+$</c> failed at a line break and tried the next
        /// <c>of</c> from there, so every <c>of</c> before a break scanned to it: forty kilobytes of
        /// <c>a of</c> took five seconds, quadratic, and a list written across lines was not read.
        /// </remarks>
        private static string? OfClause(string args)
        {
            for (int i = 0; i + 2 < args.Length; i++)
            {
                if ((args[i] | 0x20) != 'o' || (args[i + 1] | 0x20) != 'f' || !char.IsWhiteSpace(args[i + 2])) continue;
                if (i > 0 && !char.IsWhiteSpace(args[i - 1])) continue;
                int start = i + 3;
                while (start < args.Length && char.IsWhiteSpace(args[start])) start++;
                return start < args.Length ? args.Substring(start) : null;
            }
            return null;
        }

        /// <summary>Splits a selector list at the commas outside brackets, parentheses and quotes.</summary>
        private static IEnumerable<string> SplitSelectorList(string list)
        {
            int depth = 0, start = 0;
            char quote = '\0';
            for (int i = 0; i < list.Length; i++)
            {
                char ch = list[i];
                if (quote != '\0') { if (ch == '\\') i++; else if (ch == quote) quote = '\0'; continue; }
                if (ch == '"' || ch == '\'') quote = ch;
                else if (ch == '(' || ch == '[') depth++;
                else if (ch == ')' || ch == ']') depth--;
                else if (ch == ',' && depth == 0) { yield return list.Substring(start, i - start); start = i + 1; }
            }
            yield return list.Substring(start);
        }

        private static bool IsIdentStart(char ch) => char.IsLetter(ch) || ch == '_' || ch == '\\' || ch > 0x7F;

        /// <summary>The index after the identifier starting at <paramref name="i"/> — letters, digits, <c>-</c>, <c>_</c>, escapes and non-ASCII.</summary>
        private static int SkipIdent(string s, int i)
        {
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == '\\') { i += 2; continue; }
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch > 0x7F) i++;
                else break;
            }
            return Math.Min(i, s.Length);
        }

        /// <summary>
        /// The index after the bracketed run opened at <paramref name="i"/>, nesting honoured and
        /// quoted strings skipped, so a <c>.</c>, <c>#</c> or <c>:</c> inside an attribute value or
        /// a functional argument is never read as a selector; an unclosed run ends the selector.
        /// </summary>
        private static int SkipBalanced(string s, int i, char open, char close)
        {
            int depth = 0;
            char quote = '\0';
            for (; i < s.Length; i++)
            {
                char ch = s[i];
                if (quote != '\0') { if (ch == '\\') i++; else if (ch == quote) quote = '\0'; continue; }
                if (ch == '"' || ch == '\'') quote = ch;
                else if (ch == open) depth++;
                else if (ch == close && --depth == 0) return i + 1;
            }
            return s.Length;
        }

        /// <summary>
        /// What <c>@media</c> is evaluated against: the page area, in CSS pixels, for the width and
        /// height features, and the medium for the media type.
        /// </summary>
        internal readonly struct MediaViewport
        {
            public MediaViewport(double width, double height, HtmlMediaType medium = HtmlMediaType.Print)
            {
                Width = width;
                Height = height;
                Medium = medium;
            }

            public double Width { get; }
            public double Height { get; }
            public HtmlMediaType Medium { get; }
        }

        /// <summary>
        /// This conversion's viewport, set before any stylesheet is read; <c>null</c> only before
        /// then, which leaves every feature unknown.
        /// </summary>
        private MediaViewport? _mediaViewport;

        /// <summary>The sheets this conversion has imported, by full path or absolute URL: each once, and a cycle stops.</summary>
        private readonly HashSet<string> _importedSheets = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The most sheets one conversion follows <c>@import</c> to; the cap is reported once.</summary>
        private const int MaxImportedSheets = 64;

        private bool _importCapReported;

        /// <summary>
        /// Brings an <c>@import</c>ed sheet's rules into the cascade at the import's place (CSS
        /// Cascade 4 §5). <paramref name="prelude"/> is what follows the keyword — <c>url(x.css)</c>
        /// or <c>"x.css"</c>, then an optional <c>layer</c>/<c>layer(…)</c> and <c>supports(…)</c>,
        /// then a media query list — and the sheet is skipped when the list does not apply to the
        /// conversion's medium. The target resolves against the importing sheet
        /// (<paramref name="sheetLocation"/>: a remote sheet's relative import is remote, a local
        /// sheet's is beside it, a <c>&lt;style&gt;</c> block's is against the base directory) and
        /// loads by the same two paths as a <c>&lt;link&gt;</c>, local only inside
        /// <see cref="HtmlToPdfOptions.BasePath"/> (declined and reported otherwise) and remote only
        /// through <see cref="HtmlToPdfOptions.RemoteResourceLoader"/> (which reports its own
        /// refusals). Each sheet is imported once per conversion, so a cycle ends where it began;
        /// the imported text is parsed one nesting level deeper, under the same cap as a flattened
        /// <c>@media</c>, and no more than <see cref="MaxImportedSheets"/> sheets are followed.
        /// </summary>
        private void ImportStylesheet(string prelude, string? sheetLocation,
            List<(int, int, string, string)> rules, ref int order, int nestDepth)
        {
            string? target = ParseImportTarget(prelude, out string media);
            if (string.IsNullOrEmpty(target)) return;
            if (!MediaApplies(media, _mediaViewport)) return;

            if (_importedSheets.Count >= MaxImportedSheets)
            {
                if (!_importCapReported)
                {
                    _importCapReported = true;
                    ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                        "More than " + MaxImportedSheets + " stylesheets are reached through @import; the ones past " +
                        "that many were not loaded, and their rules were not applied.");
                }
                return;
            }

            Uri? remoteSheet = sheetLocation != null ? RemoteUri(sheetLocation) : null;
            bool hasScheme = HasUrlScheme(target!);
            string? css, location;
            if (RemoteUri(target!) != null
                || (!hasScheme && remoteSheet != null && Uri.TryCreate(remoteSheet, target!, out Uri? absolute) && (target = absolute.AbsoluteUri) != null))
            {
                css = LoadRemoteStylesheet(target!, out location);   // reports its own refusals
            }
            else if (remoteSheet != null)
            {
                // A sheet from another origin names no local file (HtmlToPdfOptions.RemoteResourceLoader):
                // what it imports is remote or nothing.
                ReportResourceDeclined("stylesheet", target!);
                return;
            }
            else
            {
                string path = target!;
                if (!hasScheme && !Path.IsPathRooted(path) && sheetLocation != null)
                    path = Path.Combine(Path.GetDirectoryName(sheetLocation) ?? "", path);
                css = LoadLocalStylesheet(path, out location);
                if (css == null) ReportResourceDeclined("stylesheet", target!);
            }
            if (css == null) return;
            // The loader added `location` to _importedSheets and returned "" if it was already there, so
            // an already-imported sheet or a cycle arrives here as empty text and adds no rules.
            ExtractRules(css, rules, ref order, nestDepth + 1, location);
        }

        /// <summary>
        /// The target of an <c>@import</c> prelude — <c>url(…)</c>, quoted or bare, or a quoted
        /// string — and what follows the optional <c>layer</c>, <c>layer(…)</c> and
        /// <c>supports(…)</c> as the media query list (empty when none). <c>null</c> when the prelude
        /// names nothing.
        /// </summary>
        internal static string? ParseImportTarget(string prelude, out string media)
        {
            media = "";
            // A real prelude is a few dozen characters; a longer one names nothing. This is what bounds
            // the keyword-skipping loop below, which copies the remaining text once a keyword.
            if (prelude.Length > MaxImportPreludeChars) return null;
            string s = prelude.Trim();
            string? target = null;
            int end;
            if (s.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                end = s.IndexOf(')');
                if (end < 0) return null;
                target = s.Substring(4, end - 4).Trim().Trim('"', '\'');
                end++;
            }
            else if (s.Length > 0 && (s[0] == '"' || s[0] == '\''))
            {
                end = s.IndexOf(s[0], 1);
                if (end < 0) return null;
                target = s.Substring(1, end - 1);
                end++;
            }
            else return null;

            string rest = s.Substring(end).Trim();
            // `layer`, `layer(name)` and `supports(condition)` precede the media list (Cascade 5 §5).
            while (rest.Length > 0)
            {
                if (rest.StartsWith("layer(", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("supports(", StringComparison.OrdinalIgnoreCase))
                {
                    int close = ClosingParen(rest, rest.IndexOf('('));
                    if (close < 0) break;
                    rest = rest.Substring(close + 1).Trim();
                }
                else if (rest.StartsWith("layer", StringComparison.OrdinalIgnoreCase) && (rest.Length == 5 || char.IsWhiteSpace(rest[5])))
                    rest = rest.Substring(5).Trim();
                else break;
            }
            media = rest;
            return target.Length == 0 ? null : target;
        }

        /// <summary>
        /// Evaluates a media query list for the viewport's medium — print unless
        /// <see cref="HtmlToPdfOptions.MediaType"/> says screen — per Media Queries Level 4 §2–3.
        /// </summary>
        /// <remarks>
        /// <para><b>Media types and the keywords are exact.</b> A list is true if any query is, and
        /// an empty list is true (§2.1). <c>not</c> negates the whole query and <c>only</c> changes
        /// nothing (§2.2). <c>all</c> and the medium's own type match; the other of
        /// <c>print</c>/<c>screen</c>, the deprecated types and an unknown type match nothing
        /// (§2.3, §3.2). With no viewport the medium is print. A query that does not fit the grammar
        /// is <c>not all</c> (§3.2).</para>
        ///
        /// <para><b>Width, height and orientation are evaluated against
        /// <paramref name="viewport"/></b> — <c>width</c>, <c>min-/max-width</c>, the deprecated
        /// <c>device-</c> forms, the same for height, and the Level 4 range syntax
        /// (<c>(width &gt;= 768px)</c>, <c>(400px &lt; width &lt; 700px)</c>). Unknown, as an unknown
        /// feature applies here, a responsive stylesheet would have every breakpoint in force at once:
        /// the last tier's widths winning and every <c>hidden-*</c> rule hiding.</para>
        ///
        /// <para><b>The other features a static page can answer are answered</b>
        /// (<see cref="MediaNumber"/>, <see cref="DiscreteFeature"/>): its aspect ratio, a
        /// resolution of 1dppx, 8-bit colour and no monochrome, palette or grid; no hover and no
        /// pointer; no preference for reduced motion, contrast or transparency and a light scheme;
        /// no forced or inverted colours; an sRGB gamut; paged overflow in print and scrolling on a
        /// screen. Unknown, a stylesheet's <c>(hover: hover)</c> rules and its dark scheme would
        /// apply.</para>
        ///
        /// <para><b>Any other feature is <i>unknown</i>, not assumed true</b>, and §3.1's
        /// three-valued logic lets a known part decide: <c>screen and (color)</c> is false and
        /// <c>not screen and (color)</c> true whatever the feature is. A result still unknown at the
        /// end applies — this engine's standing approximation. The spec turns unknown into false;
        /// this does not, because unknown here means "not evaluated", not "not understood". With
        /// no viewport, width and height are unknown too.</para>
        ///
        /// <para>Not substring tests, which get the keywords backwards: <c>not print</c> contains
        /// "print", <c>not screen</c> contains "screen", and <c>speech</c> contains neither.</para>
        /// </remarks>
        internal static bool MediaApplies(string condition, MediaViewport? viewport = null)
        {
            List<string> queries = SplitMediaQueryList(condition);
            if (queries.Count == 1 && queries[0].Trim().Length == 0) return true;   // empty list

            foreach (string query in queries)
                if (EvaluateMediaQuery(query, viewport) != MediaTruth.False) return true;
            return false;
        }

        private enum MediaTruth { False, True, Unknown }

        private static MediaTruth And(MediaTruth a, MediaTruth b) =>
            a == MediaTruth.False || b == MediaTruth.False ? MediaTruth.False
            : a == MediaTruth.Unknown || b == MediaTruth.Unknown ? MediaTruth.Unknown
            : MediaTruth.True;

        private static MediaTruth Or(MediaTruth a, MediaTruth b) =>
            a == MediaTruth.True || b == MediaTruth.True ? MediaTruth.True
            : a == MediaTruth.Unknown || b == MediaTruth.Unknown ? MediaTruth.Unknown
            : MediaTruth.False;

        private static MediaTruth Not(MediaTruth a) =>
            a == MediaTruth.True ? MediaTruth.False : a == MediaTruth.False ? MediaTruth.True : MediaTruth.Unknown;

        private static MediaTruth Truth(bool b) => b ? MediaTruth.True : MediaTruth.False;

        /// <summary>Splits on commas outside parentheses.</summary>
        private static List<string> SplitMediaQueryList(string list)
            => SplitTopLevel(list, ch => ch == ',', keepEmpty: true);

        /// <summary>One media query; a grammar mismatch is <c>not all</c>, which is false.</summary>
        private static MediaTruth EvaluateMediaQuery(string query, MediaViewport? viewport)
        {
            List<string>? tokens = TokenizeMediaQuery(query);
            if (tokens == null || tokens.Count == 0) return MediaTruth.False;

            // <media-condition>: starts with a parenthesised term, or `not (`.
            if (tokens[0][0] == '(' || (tokens[0] == "not" && tokens.Count > 1 && tokens[1][0] == '('))
                return EvaluateMediaCondition(tokens, 0, allowOr: true, viewport, 0) ?? MediaTruth.False;

            // [ not | only ]? <media-type> [ and <media-condition-without-or> ]?
            int i = 0;
            bool negate = tokens[0] == "not";
            if (negate || tokens[0] == "only") i++;
            if (i >= tokens.Count || tokens[i][0] == '(' || IsMediaQueryKeyword(tokens[i]))
                return MediaTruth.False;

            string medium = viewport?.Medium == HtmlMediaType.Screen ? "screen" : "print";
            MediaTruth result = Truth(tokens[i] == "all" || tokens[i] == medium);
            i++;

            if (i < tokens.Count)
            {
                if (tokens[i] != "and" || i + 1 >= tokens.Count) return MediaTruth.False;
                MediaTruth? condition = EvaluateMediaCondition(tokens, i + 1, allowOr: false, viewport, 0);
                if (condition == null) return MediaTruth.False;

                // `false and unknown` is false; `true and unknown` is unknown (§3.1).
                result = And(result, condition.Value);
            }

            return negate ? Not(result) : result;
        }

        /// <summary>
        /// The value of the <c>&lt;media-condition&gt;</c> formed by the tokens from
        /// <paramref name="start"/> to the end, or <c>null</c> when they do not form one.
        /// </summary>
        private static MediaTruth? EvaluateMediaCondition(List<string> tokens, int start, bool allowOr,
            MediaViewport? viewport, int depth)
        {
            if (tokens[start] == "not")
                return tokens.Count == start + 2 && tokens[start + 1][0] == '('
                    ? Not(EvaluateMediaTerm(tokens[start + 1], viewport, depth))
                    : null;

            if (tokens[start][0] != '(') return null;
            MediaTruth value = EvaluateMediaTerm(tokens[start], viewport, depth);
            string? joiner = null;
            for (int i = start + 1; i < tokens.Count; i += 2)
            {
                string word = tokens[i];
                if (word != "and" && !(allowOr && word == "or")) return null;
                if (joiner != null && word != joiner) return null;        // and/or cannot mix
                if (i + 1 >= tokens.Count || tokens[i + 1][0] != '(') return null;
                joiner = word;
                MediaTruth next = EvaluateMediaTerm(tokens[i + 1], viewport, depth);
                value = word == "and" ? And(value, next) : Or(value, next);
            }
            return value;
        }

        /// <summary>
        /// One parenthesised term: a nested condition, or a media feature. Anything this does not
        /// evaluate is unknown — a condition nested past <see cref="MaxCssNestingDepth"/> too.
        /// </summary>
        /// <remarks>
        /// The evaluator recursed once a nested parenthesis, copying the rest of the text at each: a
        /// <c>media</c> attribute of ten thousand nested pairs overflowed a one-megabyte stack, which
        /// cannot be caught, and the host died.
        /// </remarks>
        private static MediaTruth EvaluateMediaTerm(string term, MediaViewport? viewport, int depth)
        {
            string inner = term.Substring(1, term.Length - 2).Trim();
            if (inner.Length == 0) return MediaTruth.Unknown;

            // A nested condition — `((a) or (b))`, `(not (a))` — is evaluated as one; anything
            // else in the parentheses that does not parse is <general-enclosed>, which is unknown.
            if (inner[0] == '(' || inner.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
            {
                if (depth >= MaxCssNestingDepth) return MediaTruth.Unknown;
                List<string>? nested = TokenizeMediaQuery(inner);
                return nested != null && nested.Count > 0
                    ? EvaluateMediaCondition(nested, 0, allowOr: true, viewport, depth + 1) ?? MediaTruth.Unknown
                    : MediaTruth.Unknown;
            }

            return viewport is { } vp ? EvaluateMediaFeature(inner, vp) : MediaTruth.Unknown;
        }

        /// <summary>
        /// A media feature in the range syntax (Media Queries 4 §2.4.3) — <c>a op b</c> or
        /// <c>a op b op c</c>, each op one of <c>&lt;=</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&gt;</c>,
        /// <c>=</c> — its parts trimmed (one of white space alone is its last character).
        /// </summary>
        /// <remarks>
        /// Split by hand at the operators. The pattern, <c>[^&lt;&gt;=]+?</c> beside <c>\s*</c> three
        /// times, split a run of spaces every way there is when the match failed: a feature with runs
        /// of a hundred spaces between three operators did not finish in ten minutes.
        /// </remarks>
        internal static bool TryMediaRange(string feature, out string a, out string op1, out string b,
            out string? op2, out string? c)
        {
            a = op1 = b = "";
            op2 = c = null;
            var parts = new List<string>(3);
            var ops = new List<string>(2);
            int start = 0, i = 0;
            while (i < feature.Length)
            {
                if (!IsOperatorChar(feature[i])) { i++; continue; }
                int opStart = i;
                while (i < feature.Length && IsOperatorChar(feature[i])) i++;
                string op = feature.Substring(opStart, i - opStart);
                if ((op != "<=" && op != ">=" && op != "<" && op != ">" && op != "=") || ops.Count == 2) return false;
                parts.Add(feature.Substring(start, opStart - start));
                ops.Add(op);
                start = i;
            }
            parts.Add(feature.Substring(start));
            if (ops.Count == 0) return false;

            for (int p = 0; p < parts.Count; p++)
            {
                if (parts[p].Length == 0) return false;
                string trimmed = parts[p].Trim();
                parts[p] = trimmed.Length > 0 ? trimmed : parts[p].Substring(parts[p].Length - 1);
            }

            a = parts[0];
            op1 = ops[0];
            b = parts[1];
            if (ops.Count == 2) { op2 = ops[1]; c = parts[2]; }
            return true;

            static bool IsOperatorChar(char ch) => ch == '<' || ch == '>' || ch == '=';
        }

        /// <summary>
        /// Whether a trimmed media feature value is a keyword: an optional <c>-</c>, an ASCII letter or
        /// <c>_</c>, then ASCII letters, digits, <c>_</c> and <c>-</c>. Scanned by hand.
        /// </summary>
        internal static bool IsMediaKeyword(string value)
        {
            int i = value.Length > 0 && value[0] == '-' ? 1 : 0;
            if (i >= value.Length || !(IsAsciiLetter(value[i]) || value[i] == '_')) return false;
            for (i++; i < value.Length; i++)
            {
                char c = value[i];
                if (!(IsAsciiLetter(c) || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            }
            return true;

            static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        /// <summary>
        /// A feature against the viewport: orientation, a numeric feature (<see cref="MediaNumber"/>,
        /// with <c>min-</c>/<c>max-</c> and the range syntax) or a keyword one
        /// (<see cref="DiscreteFeature"/>); any other feature is unknown, as is a value this cannot
        /// read.
        /// </summary>
        private static MediaTruth EvaluateMediaFeature(string feature, MediaViewport vp)
        {
            int colon = feature.IndexOf(':');
            if (colon > 0)
            {
                string name = MediaFeatureName(feature.Substring(0, colon));
                string value = feature.Substring(colon + 1).Trim();

                if (name == "orientation")
                    return Eq(value, "portrait") ? Truth(vp.Height >= vp.Width)
                         : Eq(value, "landscape") ? Truth(vp.Width > vp.Height)
                         : MediaTruth.Unknown;

                if (DiscreteFeature(vp, name) is string current)
                    // A keyword is a CSS identifier (p3 is one; 3 is not), else the value is unreadable.
                    return IsMediaKeyword(value) ? Truth(Eq(value, current)) : MediaTruth.Unknown;

                string prefix = name.StartsWith("min-", StringComparison.Ordinal) ? "min-"
                              : name.StartsWith("max-", StringComparison.Ordinal) ? "max-" : "";
                string bareName = name.Substring(prefix.Length);
                double? size = MediaNumber(vp, bareName);
                double? length = ParseMediaFeatureValue(bareName, value);
                if (size == null || length == null) return MediaTruth.Unknown;
                return prefix == "min-" ? Truth(size >= length)
                     : prefix == "max-" ? Truth(size <= length)
                     : Truth(Math.Abs(size.Value - length.Value) < 0.001);
            }

            if (TryMediaRange(feature, out string a, out string op1, out string b, out string? op2, out string? c))
            {
                if (c != null)
                {
                    // <value> <op> <name> <op> <value>
                    string between = MediaFeatureName(b);
                    double? size = MediaNumber(vp, between);
                    double? low = ParseMediaFeatureValue(between, a), high = ParseMediaFeatureValue(between, c);
                    if (size == null || low == null || high == null) return MediaTruth.Unknown;
                    return And(Truth(Compare(low.Value, op1, size.Value)),
                               Truth(Compare(size.Value, op2!, high.Value)));
                }

                string leftName = MediaFeatureName(a);
                double? left = MediaNumber(vp, leftName);
                if (left != null)                                   // <name> <op> <value>
                {
                    double? v = ParseMediaFeatureValue(leftName, b);
                    return v == null ? MediaTruth.Unknown : Truth(Compare(left.Value, op1, v.Value));
                }
                string rightName = MediaFeatureName(b);
                double? right = MediaNumber(vp, rightName);
                if (right != null)                                  // <value> <op> <name>
                {
                    double? v = ParseMediaFeatureValue(rightName, a);
                    return v == null ? MediaTruth.Unknown : Truth(Compare(v.Value, op1, right.Value));
                }
                return MediaTruth.Unknown;
            }

            // Boolean context (§2.4.4): a numeric feature is true when it is not 0 — `(width)`,
            // `(color)` — and a keyword one when it is not `none` or `no-preference` — `(hover)`.
            string bareFeature = MediaFeatureName(feature);
            if (MediaNumber(vp, bareFeature) is double n) return Truth(n != 0);
            if (DiscreteFeature(vp, bareFeature) is string keyword)
                return Truth(!Eq(keyword, "none") && !Eq(keyword, "no-preference"));
            return MediaTruth.Unknown;
        }

        /// <summary>A feature's name, lower-cased, with WebKit's prefix off — <c>-webkit-min-device-pixel-ratio</c> is <c>min-device-pixel-ratio</c>.</summary>
        private static string MediaFeatureName(string raw)
        {
            string name = raw.Trim().ToLowerInvariant();
            return name.StartsWith("-webkit-", StringComparison.Ordinal) ? name.Substring("-webkit-".Length) : name;
        }

        /// <summary>
        /// A numeric feature's value for the viewport, in the unit <see cref="ParseMediaFeatureValue"/>
        /// reads its values in, or <c>null</c> for a feature that is not numeric: the page area in
        /// CSS pixels and its aspect ratio; a resolution of 1dppx — a CSS pixel is the output's
        /// pixel, as a browser prints it — so <c>device-pixel-ratio</c> 1; 8 bits of colour a
        /// component; no monochrome, no colour palette and no grid (Media Queries 4 §5–6).
        /// </summary>
        private static double? MediaNumber(MediaViewport vp, string name) => name switch
        {
            "width" or "device-width" => vp.Width,
            "height" or "device-height" => vp.Height,
            "aspect-ratio" or "device-aspect-ratio" => vp.Height > 0 ? vp.Width / vp.Height : (double?)null,
            "resolution" or "device-pixel-ratio" => 1,
            "color" => 8,
            "color-index" or "monochrome" or "grid" => 0,
            _ => null,
        };

        /// <summary>
        /// A keyword feature's value for the viewport, or <c>null</c> for one this does not answer.
        /// The output is a static page: nothing hovers or points; it states no preference and a
        /// light scheme; its colours are neither forced nor inverted, its gamut sRGB and its range
        /// standard; it overflows onto pages in print and scrolls on a screen, which also updates
        /// (Media Queries 4 §4, §6–7; Media Queries 5 §6, §11).
        /// </summary>
        private static string? DiscreteFeature(MediaViewport vp, string name)
        {
            bool screen = vp.Medium == HtmlMediaType.Screen;
            return name switch
            {
                "hover" or "any-hover" or "pointer" or "any-pointer" => "none",
                "update" => screen ? "fast" : "none",
                "overflow-block" => screen ? "scroll" : "paged",
                "overflow-inline" => screen ? "scroll" : "none",
                "prefers-color-scheme" => "light",
                "prefers-reduced-motion" or "prefers-contrast" or "prefers-reduced-transparency"
                    or "prefers-reduced-data" => "no-preference",
                "forced-colors" or "inverted-colors" => "none",
                "color-gamut" => "srgb",
                "dynamic-range" or "video-dynamic-range" => "standard",
                "display-mode" => "browser",
                _ => null,
            };
        }

        /// <summary>
        /// A value of the numeric feature <paramref name="name"/>: a length in CSS pixels for the
        /// sizes; a ratio (<c>16/9</c>, or one number) for the aspect ratios; a resolution in dppx
        /// (<c>2dppx</c>, <c>2x</c>, <c>192dpi</c>, <c>75.6dpcm</c>) for <c>resolution</c>; a plain
        /// number for the pixel ratio and the colour depths. <c>null</c> when it is none of these.
        /// </summary>
        private static double? ParseMediaFeatureValue(string name, string value)
        {
            value = value.Trim();
            switch (name)
            {
                case "width" or "device-width" or "height" or "device-height":
                    return ParseMediaLength(value);
                case "aspect-ratio" or "device-aspect-ratio":
                {
                    string[] parts = value.Split('/');
                    if (parts.Length > 2 || !TryMediaNumber(parts[0], out double num)) return null;
                    if (parts.Length == 1) return num;
                    return TryMediaNumber(parts[1], out double den) && den > 0 ? num / den : (double?)null;
                }
                case "resolution":
                {
                    if (value.Length > MaxLengthChars) return null;
                    Match m;
                    try { m = ResolutionPattern.Match(value); }
                    catch (RegexMatchTimeoutException) { return null; }
                    if (!m.Success || !TryMediaNumber(m.Groups[1].Value, out double r)) return null;
                    return m.Groups[2].Value.ToLowerInvariant() switch
                    {
                        "dpi" => r / 96,
                        "dpcm" => r * 2.54 / 96,
                        _ => r,
                    };
                }
                case "device-pixel-ratio" or "color" or "color-index" or "monochrome" or "grid":
                    return TryMediaNumber(value, out double plain) ? plain : (double?)null;
                default:
                    return null;
            }
        }

        private static bool TryMediaNumber(string s, out double n) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && n >= 0;

        private static bool Compare(double left, string op, double right) => op switch
        {
            "<" => left < right,
            "<=" => left <= right,
            ">" => left > right,
            ">=" => left >= right,
            _ => Math.Abs(left - right) < 0.001,
        };

        /// <summary>
        /// A media query's resolution and its length: the number unambiguous (<c>\d+(?:\.\d+)?|\.\d+</c>, CSS's
        /// own, where <c>\d*\.?\d+</c> split a digit run every way there is on a failed match), the value refused
        /// past <see cref="MaxLengthChars"/> before the pattern runs, and a match timeout behind both — as
        /// <see cref="LengthPattern"/> has them.
        /// </summary>
        private static readonly Regex ResolutionPattern =
            new Regex(@"^(\d+(?:\.\d+)?|\.\d+)\s*(dppx|x|dpi|dpcm)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        /// <inheritdoc cref="ResolutionPattern"/>
        private static readonly Regex MediaLengthPattern =
            new Regex(@"^(-?(?:\d+(?:\.\d+)?|\.\d+))\s*([a-zA-Z]*)$", RegexOptions.None, TimeSpan.FromSeconds(1));

        /// <summary>
        /// A media-query length in CSS pixels. <c>em</c> and <c>rem</c> are the initial font size,
        /// 16px, as Media Queries specifies — never the document's own.
        /// </summary>
        private static double? ParseMediaLength(string value)
        {
            string trimmed = value.Trim();
            if (trimmed.Length > MaxLengthChars) return null;
            Match m;
            try { m = MediaLengthPattern.Match(trimmed); }
            catch (RegexMatchTimeoutException) { return null; }
            if (!m.Success) return null;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return null;
            string unit = m.Groups[2].Value.ToLowerInvariant();
            if (unit == "em" || unit == "rem") return n * 16;
            if (unit.Length == 0) return n == 0 ? 0 : (double?)null;

            // The absolute units through Core.Text.CssLength, the one unit table (shared with
            // ParseLength and the SVG converter). Its pixels form is the arithmetic this switch
            // had, operation for operation, so every media query evaluates as it did.
            return Tetitulz.Pdf.Core.Text.CssLength.TryToPixels(n, unit, out double pixels) ? pixels : (double?)null;
        }

        /// <summary>Lower-cased words and whole parenthesised groups; null for an unclosed group.</summary>
        private static List<string>? TokenizeMediaQuery(string query)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < query.Length)
            {
                char c = query[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(')
                {
                    int close = ClosingParen(query, i);
                    if (close < 0) return null;
                    tokens.Add(query.Substring(i, close - i + 1));
                    i = close + 1;
                    continue;
                }
                int wordStart = i;
                while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] != '(') i++;
                tokens.Add(query.Substring(wordStart, i - wordStart).ToLowerInvariant());
            }
            return tokens;
        }

        /// <summary>§3: keywords that the &lt;media-type&gt; production excludes.</summary>
        private static bool IsMediaQueryKeyword(string word)
            => word == "only" || word == "not" || word == "and" || word == "or" || word == "layer";


    }
}
