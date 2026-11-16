// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// Level A's structure rules, PDF/A-1 to -3. A74 — the catalog has a <c>/StructTreeRoot</c>
        /// (ISO 19005-1 6.8.3.3, 19005-2/3 6.7.3.3). A75 — every structure element of a non-standard
        /// type maps through the <c>/RoleMap</c> to a standard one, without a circular mapping, and in
        /// PDF/A-2 and -3 no standard type is remapped (19005-1 6.8.3.4, 19005-2/3 6.7.3.4). Standard is
        /// the PDF Reference 1.4's vocabulary for PDF/A-1 and ISO 32000-1 Table 333's after
        /// (<see cref="PdfStructureTypes.IsStandardInPdf14"/>, <see cref="PdfStructureTypes.IsStandardInIso32000Part1"/>).
        /// the validation rules of those numbers.
        /// </summary>
        /// <remarks>
        /// The chain is followed here rather than through <see cref="PdfRoleMap.Resolve"/>: it stops at
        /// the first standard type (§14.7.4 forbids remapping one, so a conforming chain ends there),
        /// and a cycle is itself the finding, which <c>Resolve</c>'s stable answer on a cycle hides.
        /// </remarks>
        private static void CheckLevelAStructure(PdfDocument doc, PdfAConformancePart part, string level, List<PdfAViolation> v)
        {
            var catalog = doc?.Catalog?.Dictionary;
            if (catalog == null || level != "A" || part == PdfAConformancePart.Part4) return;
            bool part1 = part == PdfAConformancePart.Part1;
            string clause3 = part1 ? "ISO 19005-1 6.8.3.3; rule 6.8.3.3-1" : $"ISO 19005-{(int)part} 6.7.3.3; rule 6.7.3.3-1";
            string clause4 = part1 ? "ISO 19005-1 6.8.3.4; rules 6.8.3.4-1/-2" : $"ISO 19005-{(int)part} 6.7.3.4; rules 6.7.3.4-1 to -3";

            if (!(doc.ResolveObject(catalog[PdfName.StructTreeRoot]) is PdfDictionary root))
            {
                v.Add(new PdfAViolation("A74", $"A level A file has no /StructTreeRoot in its catalog ({clause3})."));
                return;
            }

            System.Func<string, bool> standard = part1
                ? (System.Func<string, bool>)PdfStructureTypes.IsStandardInPdf14
                : PdfStructureTypes.IsStandardInIso32000Part1;
            var roleMap = PdfRoleMap.Build(root, o => doc.ResolveObject(o));
            var problems = new Occurrences();

            if (!part1)
                foreach (var kvp in roleMap)
                    if (standard(kvp.Key) && (kvp.Value == null || !standard(kvp.Value)))
                        problems.Add($"/RoleMap remaps the standard /{kvp.Key} to " + (kvp.Value == null ? "a non-name" : "/" + kvp.Value));

            var judged = new Dictionary<string, string>(System.StringComparer.Ordinal);
            var walk = new StructureTreeWalk(doc)
            {
                Enter = node =>
                {
                    string role = node.Role;
                    if (role == null || standard(role) || judged.ContainsKey(role)) return;
                    string problem = WhyNotMapped(role, roleMap, standard);
                    judged[role] = problem;
                    if (problem != null) problems.Add($"/{role} {problem}");
                },
            };
            walk.Run(root);
            if (walk.FirstCeiling != null)
                ReportPartial($"The A75 structure-type check did not walk the whole structure tree ({walk.FirstCeiling}).", "A75");

            if (problems.Count > 0)
                v.Add(new PdfAViolation("A75", $"A structure type is not mapped to a standard one: {problems} ({clause4})."));
        }

        /// <summary>
        /// Why a non-standard role does not reach a standard type through the role map — unmapped,
        /// mapped to a non-name, ending on a non-standard type, or circular — or <see langword="null"/>.
        /// </summary>
        private static string WhyNotMapped(string role, IReadOnlyDictionary<string, string> roleMap, System.Func<string, bool> standard)
        {
            var seen = new HashSet<string>(System.StringComparer.Ordinal) { role };
            string current = role;
            for (int hop = 0; hop < PdfRoleMap.MaxHops; hop++)
            {
                if (!roleMap.TryGetValue(current, out string next))
                    return current == role ? "is not in the /RoleMap" : $"maps to /{current}, which is neither standard nor mapped";
                if (next == null) return $"maps /{current} to a non-name";
                if (standard(next)) return null;
                if (!seen.Add(next)) return $"maps in a circle through /{next}";
                current = next;
            }
            return $"maps through more than {PdfRoleMap.MaxHops.ToString(CultureInfo.InvariantCulture)} types";
        }
    }
}
