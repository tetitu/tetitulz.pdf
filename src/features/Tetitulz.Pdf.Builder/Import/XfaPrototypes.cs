// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Tetitulz.Pdf.Builder.Import
{
    /// <summary>
    /// Resolves a template's <c>use</c> and <c>usehref</c> prototype references in place, before the
    /// template is read — XFA 3.3, chapter 6, "Prototypes" (<c>docs/iso/XFA-3_3.pdf</c>, pp. 225–233).
    /// </summary>
    /// <remarks>
    /// <para><b>The rules, as the specification states them.</b> A reference names an element of the
    /// same type in the template packet: <c>use</c> holds <c>#ID</c> or a SOM expression,
    /// <c>usehref</c> <c>#ID</c>, <c>#som(expression)</c> or a URI before either; <c>usehref</c> wins
    /// when both are non-empty. The referencing element inherits the prototype's attributes except
    /// <c>id</c>, <c>name</c>, <c>use</c> and <c>usehref</c> and except those it sets itself (an
    /// attribute set to the empty string is set); its data content unless it has its own (empty
    /// content cannot override); and its children, matched "first by type and then by ordinal number
    /// within type" — a child present in both takes the prototype's child as its own prototype, a
    /// child only in the prototype is inherited. Prototypes may reference prototypes, and a
    /// prototype's descendants may too.</para>
    /// <para><b>What stays unresolved, and is counted for the layout to report:</b> a reference to
    /// another document (the reader opens no URI), a SOM expression the resolver does not read (it reads
    /// names and <c>#class</c> steps with their <c>[n]</c> indexes, <c>..</c> descendant searches,
    /// qualified from <c>$template</c> or unqualified from the referencing element outward — see
    /// <c>FindBySom</c>; not <c>[*]</c>, <c>.*</c> or another model), a target that is missing or
    /// of another type, and a loop ("It is the responsibility of the form creator to ensure that
    /// there are no endless prototyping loops" — the reader keeps the element as it declares
    /// itself).</para>
    /// <para><b>Hostile input.</b> Each prototype layer can multiply what the next copies — two
    /// children that each use a prototype of two children that each use another doubles the
    /// template per level — so every element the expansion adds is charged to
    /// <see cref="MaxAddedElements"/>, and a reference that would exceed it is left unresolved and
    /// counted — and every character the elements carry to <see cref="MaxAddedCharacters"/>, since a
    /// reference costs an element or two and what it copies can be an image, decoded again by each
    /// reference. The <c>Resolve</c> walk is bounded by <see cref="XfaTemplate.MaxDepth"/>; the subtree
    /// <b>clone</b> (<c>new XElement</c>, whose copy constructor recurses per level) is bounded instead
    /// by <see cref="XfaTemplate.MaxXmlDepth"/>, which the packet's load refuses a tree deeper than —
    /// without it a few thousand nested elements, well under the element budget, overflowed the stack.</para>
    /// </remarks>
    internal static class XfaPrototypes
    {
        /// <summary>Elements the expansion may add to a template, across every reference.</summary>
        internal const int MaxAddedElements = 100_000;

        /// <summary>
        /// Characters of attributes and text the expansion may add, each attribute and text counted one
        /// more whatever its length. The strings are shared, but what reads them is not: a base64 body is
        /// decoded and a measure parsed per reference, so one prototype's weight multiplies by them.
        /// </summary>
        internal const long MaxAddedCharacters = 64L * 1024 * 1024;

        /// <summary>
        /// Elements the SOM searches may examine for one template, all references together: an
        /// unqualified or <c>..</c> step searches the scopes outward or every descendant, so each
        /// reference costs up to the template, and a template of references is that squared.
        /// </summary>
        internal const int MaxSomExamined = 4_000_000;

        /// <summary>The outcome: how many references were left unresolved, and whether the budget stopped any.</summary>
        internal readonly struct Outcome
        {
            internal Outcome(int resolved, int unresolved, bool budgetExhausted, int added, long examined)
            {
                Resolved = resolved;
                Unresolved = unresolved;
                BudgetExhausted = budgetExhausted;
                Added = added;
                Examined = examined;
            }

            /// <summary>Elements counted and searched — prototypes measured, SOM candidates read: the work, which the budgets bound.</summary>
            internal long Examined { get; }

            internal int Resolved { get; }
            internal int Unresolved { get; }
            internal bool BudgetExhausted { get; }

            /// <summary>Elements the expansion added — the work the budget measures.</summary>
            internal int Added { get; }
        }

        /// <summary>Resolves every reference under <paramref name="template"/>, in place.</summary>
        internal static Outcome Expand(XElement template)
        {
            var state = new State(template);
            Resolve(template, 0, state);
            return new Outcome(state.Resolved, state.Unresolved, state.BudgetExhausted, state.Added, state.Examined + state.SomExamined);
        }

        private sealed class State
        {
            internal State(XElement template)
            {
                Template = template;
                foreach (XElement e in template.DescendantsAndSelf())
                    if (Attr(e, "id") is string id && id.Length > 0 && !ById.ContainsKey(id))
                        ById[id] = e;
            }

            internal readonly XElement Template;
            internal readonly Dictionary<string, XElement> ById = new Dictionary<string, XElement>(StringComparer.Ordinal);
            internal readonly HashSet<XElement> InProgress = new HashSet<XElement>();
            internal int Resolved, Unresolved, Added;
            internal long AddedCharacters;
            internal long Examined, SomExamined;
            internal bool BudgetExhausted;
        }

        private static void Resolve(XElement e, int depth, State state)
        {
            if (depth > XfaTemplate.MaxDepth)
            {
                // Not silently: every reference this deep or below stays unresolved and is counted —
                // the subtree enumerated without recursion.
                foreach (XElement d in e.DescendantsAndSelf().ToList())
                    if (Reference(d) != null) Unresolvable(d, state);
                return;
            }
            if (Reference(e) != null) ApplyPrototype(e, depth, state);
            // A snapshot: resolving a child can append inherited children to it, never to e.
            foreach (XElement child in e.Elements().ToList())
            {
                if (child.Name.LocalName == "proto") continue;   // read only through a reference
                Resolve(child, depth + 1, state);
            }
        }

        /// <summary>Merges the prototype <paramref name="e"/> names into it, or counts it unresolved.</summary>
        private static void ApplyPrototype(XElement e, int depth, State state)
        {
            // A spent budget resolves nothing more, and measures nothing more: each reference past it
            // counted its prototype afresh, so N references to one of N elements was N² for none added.
            if (state.BudgetExhausted)
            {
                Unresolvable(e, state);
                return;
            }
            XElement? prototype = Find(Reference(e)!, e, state);
            if (prototype == null || prototype.Name.LocalName != e.Name.LocalName
                || prototype == e || state.InProgress.Contains(prototype))
            {
                Unresolvable(e, state);
                return;
            }

            // Counted no further than the budget's remainder decides.
            int cost = 0;
            long characters = 0;
            foreach (XElement counted in prototype.DescendantsAndSelf().Take(MaxAddedElements - state.Added + 1))
            {
                cost++;
                characters += CarriedCharacters(counted);
            }
            state.Examined += cost;
            if (state.Added + cost > MaxAddedElements || state.AddedCharacters + characters > MaxAddedCharacters)
            {
                state.BudgetExhausted = true;
                Unresolvable(e, state);
                return;
            }
            state.Added += cost;
            state.AddedCharacters += characters;

            // The prototype resolved first, as a copy: its own references, and its descendants'.
            var copy = new XElement(prototype);
            state.InProgress.Add(prototype);
            Resolve(copy, depth + 1, state);
            state.InProgress.Remove(prototype);

            e.SetAttributeValue("use", null);
            e.SetAttributeValue("usehref", null);
            Inherit(e, copy, depth, state);
            state.Resolved++;
        }

        /// <summary>What one element carries: its attributes' values and its own text, each one more.</summary>
        private static long CarriedCharacters(XElement e)
        {
            long characters = 0;
            foreach (XAttribute a in e.Attributes()) characters += 1 + a.Value.Length;
            foreach (XText t in e.Nodes().OfType<XText>()) characters += 1 + t.Value.Length;
            return characters;
        }

        /// <summary>
        /// Counts a reference left unresolved and drops it, so the element is read as it declares
        /// itself and a copy of it reached again through an enclosing prototype is not counted twice.
        /// </summary>
        private static void Unresolvable(XElement e, State state)
        {
            state.Unresolved++;
            e.SetAttributeValue("use", null);
            e.SetAttributeValue("usehref", null);
        }

        /// <summary>Gives <paramref name="target"/> what it does not override of <paramref name="prototype"/>.</summary>
        private static void Inherit(XElement target, XElement prototype, int depth, State state)
        {
            if (depth > XfaTemplate.MaxDepth) return;

            foreach (XAttribute a in prototype.Attributes())
            {
                string local = a.Name.LocalName;
                if (a.IsNamespaceDeclaration || local == "id" || local == "name" || local == "use" || local == "usehref") continue;
                if (target.Attribute(a.Name) == null) target.SetAttributeValue(a.Name, a.Value);
            }

            if (!HasDataContent(target) && HasDataContent(prototype))
                target.AddFirst(new XText(DataContent(prototype)));

            var targetByType = target.Elements().GroupBy(c => c.Name.LocalName)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var ordinal = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (XElement child in prototype.Elements().ToList())
            {
                string type = child.Name.LocalName;
                if (type == "proto") continue;
                ordinal.TryGetValue(type, out int n);
                ordinal[type] = n + 1;
                if (targetByType.TryGetValue(type, out var same) && n < same.Count)
                {
                    // The prototype's child acts as a prototype for the target's child — unless that
                    // child names one of its own, which Resolve applies when it reaches it.
                    if (Reference(same[n]) == null) Inherit(same[n], child, depth + 1, state);
                }
                else
                {
                    target.Add(new XElement(child));
                }
            }
        }

        /// <summary>The reference an element makes: a non-empty <c>usehref</c>, else a non-empty <c>use</c>.</summary>
        private static string? Reference(XElement e)
        {
            string? href = Attr(e, "usehref");
            if (!string.IsNullOrEmpty(href)) return "href:" + href;
            string? use = Attr(e, "use");
            return string.IsNullOrEmpty(use) ? null : "use:" + use;
        }

        private static XElement? Find(string reference, XElement from, State state)
        {
            bool href = reference.StartsWith("href:", StringComparison.Ordinal);
            string value = reference.Substring(href ? 5 : 4).Trim();

            if (href)
            {
                int hash = value.IndexOf('#');
                if (hash != 0) return null;                    // a URI: another document
                value = value.Substring(1);
                if (value.StartsWith("som(", StringComparison.Ordinal) && value.EndsWith(")", StringComparison.Ordinal))
                    return FindBySom(value.Substring(4, value.Length - 5), from, state);
                return state.ById.TryGetValue(value, out var byId) ? byId : null;
            }

            if (value.StartsWith("#", StringComparison.Ordinal))
                return state.ById.TryGetValue(value.Substring(1), out var byId) ? byId : null;
            return FindBySom(value, from, state);
        }

        /// <summary>A SOM expression's longest text this reads.</summary>
        internal const int MaxSomLength = 512;

        /// <summary>One step of a SOM expression: a name or a <c>#class</c>, its index, and whether it searches every descendant (<c>..</c>).</summary>
        private readonly struct SomStep
        {
            internal SomStep(string name, bool isClass, int index, bool descendant) { Name = name; IsClass = isClass; Index = index; Descendant = descendant; }
            internal string Name { get; }
            internal bool IsClass { get; }
            internal int Index { get; }
            internal bool Descendant { get; }
        }

        /// <summary>
        /// The node a SOM expression names in the template (XFA 3.3 chapter 3, "Scripting Object Model"),
        /// or null. A step is a name or <c>#class</c> with an optional <c>[n]</c> index (0 by default);
        /// <c>..</c> before a step searches every descendant. A name is found through the transparent
        /// nodes — an unnamed subform, subform set, area, exclusion group or page set or area, whose children
        /// answer as their parent's ("Transparent Nodes"), and a <c>proto</c> wrapper — and a class among the
        /// true children, as the chapter has it. A qualified expression starts at <c>$template</c> (or
        /// <c>xfa.template</c>); an unqualified one is looked for from <paramref name="from"/> outward —
        /// its children, then it and its siblings, then each ancestor's level ("Differences Between
        /// Qualified and Unqualified References"). Not read, so null: <c>[*]</c> and <c>.*</c> (several
        /// nodes, where a prototype is one), another model (<c>$data</c>, <c>$form</c>, …), a relative
        /// <c>$.</c>, and text past <see cref="MaxSomLength"/>.
        /// </summary>
        private static XElement? FindBySom(string expression, XElement from, State state)
        {
            string expr = expression.Trim();
            if (expr.Length == 0 || expr.Length > MaxSomLength) return null;
            string[] parts = expr.Split('.');
            int first;
            bool qualified;
            if (parts[0] == "$template") { qualified = true; first = 1; }
            else if (parts.Length > 1 && parts[0] == "xfa" && parts[1] == "template") { qualified = true; first = 2; }
            // Another model's root ($data, $form, $record, !…) or a relative $. names no template node, so
            // its first step, looked for as an unqualified name, finds nothing.
            else { qualified = false; first = 0; }

            var steps = new List<SomStep>();
            bool descendant = false;
            for (int i = first; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0)
                {
                    // "a..b": an empty part between two dots searches every descendant for the next step.
                    // Not at an unqualified expression's head, not last, not twice running ("...").
                    if ((i == first && !qualified) || i == parts.Length - 1 || descendant) return null;
                    descendant = true;
                    continue;
                }
                if (!TryStep(part, descendant, out SomStep step)) return null;
                steps.Add(step);
                descendant = false;
            }
            if (steps.Count == 0) return null;

            if (qualified) return Walk(state.Template, steps, 0, state);

            // Unqualified: the first step from the referencing element outward, the rest below the match.
            foreach (XElement scope in Scopes(from))
            {
                XElement? match = Select(scope, steps[0], state);
                if (match != null) return Walk(match, steps, 1, state);
            }
            return null;
        }

        /// <summary>Where an unqualified first step is looked for, in the chapter's order: the element's own children, then each level from its own up.</summary>
        private static IEnumerable<XElement> Scopes(XElement from)
        {
            yield return from;
            int levels = 0;
            for (XElement? level = from.Parent; level != null && levels <= XfaTemplate.MaxDepth; level = level.Parent, levels++)
                yield return level;
        }

        private static XElement? Walk(XElement current, List<SomStep> steps, int start, State state)
        {
            for (int i = start; i < steps.Count; i++)
            {
                XElement? next = Select(current, steps[i], state);
                if (next == null) return null;
                current = next;
            }
            return current;
        }

        private static bool TryStep(string part, bool descendant, out SomStep step)
        {
            step = default;
            string name = part;
            int index = 0;
            int open = part.IndexOf('[');
            if (open >= 0)
            {
                if (!part.EndsWith("]", StringComparison.Ordinal)) return false;
                string inner = part.Substring(open + 1, part.Length - open - 2).Trim();
                if (!int.TryParse(inner, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index))
                    return false;                                                   // [*], a negative or an expression
                name = part.Substring(0, open);
            }
            bool isClass = name.StartsWith("#", StringComparison.Ordinal);
            if (isClass) name = name.Substring(1);
            if (name.Length == 0 || name.IndexOfAny(new[] { '[', ']', '#', '*', '(', ')', ' ' }) >= 0) return false;
            step = new SomStep(name, isClass, index, descendant);
            return true;
        }

        /// <summary>The step's node under <paramref name="node"/>: among the visible children by name, the true children by class, or every descendant; the index-th, or null.</summary>
        private static XElement? Select(XElement node, SomStep step, State state)
        {
            IEnumerable<XElement> candidates = Charged(step.Descendant ? VisibleDescendants(node, 0)
                                             : step.IsClass ? TrueChildren(node)
                                             : Visible(node, 0), state);
            var matches = step.IsClass
                ? candidates.Where(c => c.Name.LocalName == step.Name)
                : candidates.Where(c => Attr(c, "name") == step.Name);
            return matches.Skip(step.Index).FirstOrDefault();
        }

        /// <summary>The candidates a step reads, each charged to <see cref="MaxSomExamined"/>; past it the search ends unmatched and the budget is spent.</summary>
        private static IEnumerable<XElement> Charged(IEnumerable<XElement> candidates, State state)
        {
            if (state.BudgetExhausted) yield break;
            foreach (XElement c in candidates)
            {
                if (++state.SomExamined > MaxSomExamined)
                {
                    state.BudgetExhausted = true;
                    yield break;
                }
                yield return c;
            }
        }

        /// <summary>A node's children with a <c>proto</c> wrapper seen through: the true children for <c>#class</c>.</summary>
        private static IEnumerable<XElement> TrueChildren(XElement node)
            => node.Elements().SelectMany(c => c.Name.LocalName == "proto" ? c.Elements() : new[] { c });

        /// <summary>The children a name is found among: the true ones, and a transparent child's children in its place (and so on down), a <c>proto</c> wrapper seen through.</summary>
        private static IEnumerable<XElement> Visible(XElement node, int depth)
        {
            foreach (XElement c in TrueChildren(node))
            {
                yield return c;
                if (IsTransparent(c) && depth < XfaTemplate.MaxDepth)
                    foreach (XElement adopted in Visible(c, depth + 1)) yield return adopted;
            }
        }

        /// <summary>Every descendant, in document order, a <c>proto</c> wrapper seen through.</summary>
        private static IEnumerable<XElement> VisibleDescendants(XElement node, int depth)
        {
            if (depth > XfaTemplate.MaxDepth) yield break;
            foreach (XElement c in TrueChildren(node))
            {
                yield return c;
                foreach (XElement d in VisibleDescendants(c, depth + 1)) yield return d;
            }
        }

        /// <summary>An explicitly nameable container left unnamed, whose children answer as its parent's.</summary>
        private static bool IsTransparent(XElement e)
            => e.Name.LocalName is "subform" or "subformSet" or "area" or "exclGroup" or "pageSet" or "pageArea"
               && string.IsNullOrEmpty(Attr(e, "name"));

        private static bool HasDataContent(XElement e)
            => e.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));

        private static string DataContent(XElement e)
            => string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value));

        private static string? Attr(XElement e, string local) => XfaXml.Attr(e, local);
    }
}
