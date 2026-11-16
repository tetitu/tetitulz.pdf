// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// ISO 19005's rules on actions, for one part, as the published validation profiles state them.
        /// </summary>
        /// <remarks>
        /// <para><b>The source.</b> ISO 19005 is sold, not published, and the repository holds only
        /// Technical Corrigendum 1 of Part 1. These rules come from the published validation profiles, the PDF
        /// Association's machine-checkable statement of the standard's clauses. Each rule
        /// id below is the profile's (<c>clause-test</c>), and each message cites it with the clause.</para>
        /// <para><b>The parts differ.</b> Part 1 has no rule
        /// about a page's <c>/AA</c>; parts 2 and 3 forbid it. Part 4 permits JavaScript and allows
        /// <c>/AA</c> with the mouse and focus triggers only, and puts no limit on a widget's or a
        /// field's.</para>
        /// </remarks>
        private sealed class ActionRules
        {
            /// <summary>Named actions every part permits (rule 6.6.1-2 / 6.5.1-2).</summary>
            internal static readonly string[] PermittedNamedActions = { "NextPage", "PrevPage", "FirstPage", "LastPage" };

            /// <summary>The keys a PDF/A-4 <c>/AA</c> may hold on a catalog, a page or a non-widget annotation (6.6.3-1).</summary>
            internal static readonly string[] Part4AdditionalActionKeys = { "E", "X", "D", "U", "Fo", "Bl" };

            internal string Standard { get; private set; }
            internal string Iso { get; private set; }
            internal string[] PermittedTypes { get; private set; }
            internal string TypeRule { get; private set; }
            internal string NamedRule { get; private set; }
            internal string WidgetActionRule { get; private set; }

            // An /AA rule per owner; null where the part has none.
            internal string CatalogAaRule { get; private set; }
            internal string PageAaRule { get; private set; }
            internal string WidgetAaRule { get; private set; }
            internal string FieldAaRule { get; private set; }

            /// <summary>Part 4's key allow-list, for a catalog, a page and a non-widget annotation.</summary>
            internal string AaKeysRule { get; private set; }

            internal string Cite(string rule)
                => $"{Iso} {rule.Substring(0, rule.LastIndexOf('-'))}; rule {rule}";

            /// <param name="part">The part validated against.</param>
            /// <param name="conformance">The XMP's <c>pdfaid:conformance</c>; PDF/A-4e (<c>E</c>)
            /// also permits <c>/SetOCGState</c> and <c>/GoTo3DView</c> (the PDF/A-4e profile,
            /// rule 6.6.1-1: "The SetOCGState and GoTo3DView actions shall only be permitted in a
            /// PDF/A-4e compliant file as described in Annex B").</param>
            internal static ActionRules For(PdfAConformancePart part, string conformance)
            {
                switch (part)
                {
                    case PdfAConformancePart.Part1:
                        return new ActionRules
                        {
                            Standard = "PDF/A-1", Iso = "ISO 19005-1",
                            // Hide is not permitted either (Corrigendum 2).
                            PermittedTypes = new[] { "GoTo", "GoToR", "Thread", "URI", "Named", "SubmitForm" },
                            TypeRule = "6.6.1-1", NamedRule = "6.6.1-2", WidgetActionRule = "6.6.1-3",
                            WidgetAaRule = "6.6.2-1", FieldAaRule = "6.6.2-2", CatalogAaRule = "6.6.2-3",
                        };
                    case PdfAConformancePart.Part2:
                    case PdfAConformancePart.Part3:
                        return new ActionRules
                        {
                            Standard = part == PdfAConformancePart.Part2 ? "PDF/A-2" : "PDF/A-3",
                            Iso = part == PdfAConformancePart.Part2 ? "ISO 19005-2" : "ISO 19005-3",
                            PermittedTypes = new[] { "GoTo", "GoToR", "GoToE", "Thread", "URI", "Named", "SubmitForm" },
                            TypeRule = "6.5.1-1", NamedRule = "6.5.1-2", WidgetActionRule = "6.4.1-1",
                            WidgetAaRule = "6.4.1-1", FieldAaRule = "6.4.1-2",
                            CatalogAaRule = "6.5.2-1", PageAaRule = "6.5.2-2",
                        };
                    default:
                        bool engineering = conformance == "E";
                        var permitted = new List<string>
                        {
                            "GoTo", "GoToR", "GoToE", "Thread", "URI", "Named", "SubmitForm",
                            "JavaScript", "RichMediaExecute", "GoToDp",
                        };
                        if (engineering) permitted.AddRange(new[] { "SetOCGState", "GoTo3DView" });
                        return new ActionRules
                        {
                            Standard = engineering ? "PDF/A-4e" : "PDF/A-4", Iso = "ISO 19005-4",
                            PermittedTypes = permitted.ToArray(),
                            TypeRule = "6.6.1-1", NamedRule = "6.6.1-2", WidgetActionRule = "6.4.1-1",
                            AaKeysRule = "6.6.3-1",
                        };
                }
            }
        }

        /// <summary>
        /// A02 (an action type the part does not permit), A22 (a named action other than page
        /// navigation), A23 (a widget with an <c>/A</c>) and A04 (an <c>/AA</c> where the part forbids
        /// it, or in PDF/A-4 a trigger it does not list).
        /// </summary>
        /// <remarks>
        /// <para><b>Every action, each part's rule.</b> A link's <c>/Launch</c>, a script anywhere, a
        /// script second on a <c>/Next</c> chain, a named <c>/Print</c> and a widget's action are all
        /// found; PDF/A-4's permitted JavaScript and PDF/A-1's unregulated page <c>/AA</c> are not
        /// reported. A05 is retired: a JavaScript <c>/OpenAction</c> is one more forbidden action
        /// type, an A02, and its message names where it was found.</para>
        /// <para><b>The types come from <see cref="PdfActionDispatcher"/>'s list</b>, which reaches
        /// document scripts, the <c>/OpenAction</c>, every <c>/AA</c>, fields, annotations and
        /// outline items, and every action on each one's <c>/Next</c> chain. When it could not list
        /// everything, the check says so as an A99. The keys come from
        /// <see cref="ActionOwnerWalk"/>, because an <c>/AA</c> breaks its rule whether or not it
        /// holds an action.</para>
        /// <para>An action with no <c>/S</c> fails the profile's test (<c>S == "GoTo" || …</c> is false
        /// for a missing value), so it is an A02 too.</para>
        /// </remarks>
        private static void CheckActions(PdfDocument doc, PdfAConformancePart part, string conformance,
            List<PdfAViolation> v)
        {
            var rules = ActionRules.For(part, conformance);
            var findings = new ActionFindings();

            var actions = PdfActionDispatcher.ExtractActions(doc);
            string cut = PdfActionDispatcher.LastChainDiagnostic;
            if (cut != null)
                ReportPartial("The A02/A22 action-type check did not run in full — " + cut, "A02", "A22");

            foreach (var action in actions)
            {
                var dict = action.Dictionary;
                if (dict == null) continue;

                string type = (doc.ResolveObject(dict[PdfName.S]) as PdfName)?.Value;
                if (type == null)
                {
                    findings.Add("A02", $"{rules.Standard} does not permit an action with no /S type " +
                                        $"({rules.Cite(rules.TypeRule)})", ActionFindings.Where(action));
                }
                else if (Array.IndexOf(rules.PermittedTypes, type) < 0)
                {
                    findings.Add("A02", $"{rules.Standard} does not permit a /{type} action " +
                                        $"({rules.Cite(rules.TypeRule)})", ActionFindings.Where(action));
                }
                else if (type == "Named")
                {
                    string name = (doc.ResolveObject(dict[PdfName.N]) as PdfName)?.Value;
                    if (Array.IndexOf(ActionRules.PermittedNamedActions, name) < 0)
                        findings.Add("A22", $"{rules.Standard} permits only the NextPage, PrevPage, FirstPage " +
                                            $"and LastPage named actions, and this one is " +
                                            $"{(name == null ? "missing its /N" : "/" + name)} " +
                                            $"({rules.Cite(rules.NamedRule)})", ActionFindings.Where(action));
                }
            }

            var walk = ActionOwnerWalk.Walk(doc, obj => obj == null ? null : doc.ResolveObject(obj));
            if (walk.FieldWalk.BudgetExhausted || walk.FieldWalk.DepthExceeded)
                ReportPartial("The A04 additional-actions check did not run in full — the AcroForm field " +
                              "walk stopped at its " + (walk.FieldWalk.BudgetExhausted
                                  ? $"budget of {AcroFormFieldWalk.MaxFieldNodes} nodes"
                                  : $"depth cap of {AcroFormFieldWalk.MaxFieldDepth}") +
                              ", so fields beyond that point were not examined for /AA.", "A04");

            foreach (var owner in walk.Owners)
            {
                var dict = owner.Dictionary;
                bool hasAa = dict[PdfName.AA] != null;

                if (owner.Kind == ActionOwnerKind.Widget && dict[PdfName.A] != null)
                    findings.Add("A23", $"{rules.Standard} does not permit an /A action on a widget annotation " +
                                        $"({rules.Cite(rules.WidgetActionRule)})", ActionFindings.Where(owner));

                string aaRule = owner.Kind switch
                {
                    ActionOwnerKind.Catalog => rules.CatalogAaRule,
                    ActionOwnerKind.Page    => rules.PageAaRule,
                    ActionOwnerKind.Widget  => rules.WidgetAaRule,
                    ActionOwnerKind.Field   => rules.FieldAaRule,
                    _                       => null,
                };
                if (hasAa && aaRule != null)
                    findings.Add("A04", $"{rules.Standard} does not permit an /AA entry on {OwnerNoun(owner.Kind)} " +
                                        $"({rules.Cite(aaRule)})", ActionFindings.Where(owner));

                if (hasAa && rules.AaKeysRule != null
                    && owner.Kind != ActionOwnerKind.Widget && owner.Kind != ActionOwnerKind.Field
                    && doc.ResolveObject(dict[PdfName.AA]) is PdfDictionary aa)
                {
                    foreach (var key in aa.Select(entry => entry.Key.Value))
                        if (Array.IndexOf(ActionRules.Part4AdditionalActionKeys, key) < 0)
                            findings.Add("A04", $"{rules.Standard} permits only the E, X, D, U, Fo and Bl triggers " +
                                                $"in the /AA of a catalog, a page or an annotation other than a " +
                                                $"widget, and this one holds /{key} ({rules.Cite(rules.AaKeysRule)})",
                                         ActionFindings.Where(owner));
                }
            }

            findings.Emit((code, message) => v.Add(new PdfAViolation(code, message)));
        }

        private static string OwnerNoun(ActionOwnerKind kind) => kind switch
        {
            ActionOwnerKind.Catalog    => "the document catalog",
            ActionOwnerKind.Page       => "a page",
            ActionOwnerKind.Widget     => "a widget annotation",
            ActionOwnerKind.Field      => "a form field",
            _                          => "an annotation",
        };
    }
}
