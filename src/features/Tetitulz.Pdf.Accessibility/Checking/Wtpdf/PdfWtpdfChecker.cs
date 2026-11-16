// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;

namespace Tetitulz.Pdf.Accessibility.Checking.Wtpdf
{
    /// <summary>A Well-Tagged PDF (WTPDF 1.0) conformance level (§6.1).</summary>
    public enum WtpdfConformanceLevel
    {
        /// <summary>§6.1.2 — the conformance level for reuse.</summary>
        Reuse,

        /// <summary>§6.1.3 — the conformance level for accessibility, which also meets ISO 14289-2.</summary>
        Accessibility,
    }

    /// <summary>
    /// Checks a file against <b>Well-Tagged PDF (WTPDF) 1.0</b> (PDF Association, 2024) — the
    /// declared conformance level, and the requirements of that level.
    /// </summary>
    /// <remarks>
    /// <para><b>WTPDF is PDF/UA-2's clause text with two levels drawn through it.</b> Its Clause 8
    /// mirrors ISO 14289-2's clause for clause, numbering included (verified against
    /// <c>docs/iso/wtpdf-1.0.txt</c>: the same-namespace role-map sentence, the explicitly numbered
    /// headings, the <c>Note</c> prohibition, the table regularity rule, §8.9.2.4.19's 3D and RichMedia
    /// and §8.11.2's <c>DisplayDocTitle</c> all appear at the same numbers). §6.1.3 says a file at the
    /// accessibility level "also meets all the requirements for ISO 14289-2". So this is a thin layer
    /// over <see cref="PdfUa2StructureChecker"/>, not a second checker, and its findings keep the
    /// <c>UA2-</c> codes — which name the same clause numbers in both documents.</para>
    ///
    /// <para><b>The reuse level drops exactly the paragraphs WTPDF marks "[Conformance level for
    /// accessibility]"</b> among those the UA-2 checker implements: §8.2.5.28.2 (a Figure's
    /// <c>/Alt</c> or <c>/ActualText</c>), §8.4.3's closing sentence (an <c>/ActualText</c> or <c>/Alt</c>
    /// for real content mapped to the Unicode PUA — a "should" at reuse), §8.7 (the whole optional-content subclause), §8.9.2.4.8
    /// (an Ink annotation's <c>/Contents</c>), §8.9.2.4.19 (3D and RichMedia <c>/Contents</c>) and
    /// §8.11.2 (<c>DisplayDocTitle</c>). Everything else is a general provision both levels share.</para>
    ///
    /// <para><b>Which level is judged.</b> §6.1.1: "A file conforming with this document shall
    /// identify conformance with at least one of the conformance levels", through a PDF Declaration
    /// whose <c>pdfd:conformsTo</c> is <see cref="ReuseDeclarationUri"/> or
    /// <see cref="AccessibilityDeclarationUri"/>, read by <see cref="PdfDeclarationsReader"/>. A file
    /// declaring accessibility is judged at accessibility (its requirements include reuse's); one
    /// declaring only reuse, at reuse. A file declaring neither is reported under <c>WTPDF-6.1.1</c>
    /// and judged at reuse, so its general findings are still visible. Metadata that cannot be read
    /// (an encrypted document, a packet that is not XML) is reported under <c>WTPDF-99</c> rather than
    /// as a missing declaration — an unreadable claim is not an absent one — and judged at reuse.</para>
    ///
    /// <para><b>A clean result is not a certificate</b>, for the same reason
    /// <see cref="PdfUa2StructureResult.IsConforming"/> is not: the UA-2 checker implements the
    /// mechanically checkable sentences, not the ones that turn on intent.</para>
    /// </remarks>
    public static class PdfWtpdfChecker
    {
        /// <summary>WTPDF 1.0 §6.1.2's declaration URI for the conformance level for reuse.</summary>
        public const string ReuseDeclarationUri = "http://pdfa.org/declarations/wtpdf/#reuse1.0";

        /// <summary>WTPDF 1.0 §6.1.3's declaration URI for the conformance level for accessibility.</summary>
        public const string AccessibilityDeclarationUri = "http://pdfa.org/declarations/wtpdf/#accessibility1.0";

        /// <summary>The UA-2 codes WTPDF marks "[Conformance level for accessibility]" — dropped at reuse.</summary>
        internal static readonly HashSet<string> AccessibilityOnlyCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "UA2-8.2.5.28.2", "UA2-8.4.3", "UA2-8.7", "UA2-8.9.2.4.8", "UA2-8.9.2.4.19", "UA2-8.11.2",
        };

        /// <summary>Checks a PDF file.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="pdfPath"/> is null.</exception>
        public static PdfWtpdfResult Validate(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Validate(fs);
        }

        /// <summary>Checks a PDF stream.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        public static PdfWtpdfResult Validate(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            byte[] bytes = StreamBytes.ReadAll(input);
            using var ms = new MemoryStream(bytes);
            using var doc = PdfDocument.Open(ms);

            var violations = new List<PdfUa2StructureViolation>();
            var levels = new List<WtpdfConformanceLevel>();

            IReadOnlyList<string> declared = PdfDeclarationsReader.ReadConformsTo(doc);
            if (declared == null)
            {
                violations.Add(new PdfUa2StructureViolation("WTPDF-99",
                    "The file's PDF Declarations could not be read (" + PdfDeclarationsReader.LastError +
                    "), so its WTPDF conformance level is unknown; it was judged at the conformance " +
                    "level for reuse (WTPDF 1.0 §6.1.1)."));
            }
            else
            {
                foreach (string uri in declared)
                {
                    if (uri == ReuseDeclarationUri && !levels.Contains(WtpdfConformanceLevel.Reuse))
                        levels.Add(WtpdfConformanceLevel.Reuse);
                    else if (uri == AccessibilityDeclarationUri && !levels.Contains(WtpdfConformanceLevel.Accessibility))
                        levels.Add(WtpdfConformanceLevel.Accessibility);
                }

                if (levels.Count == 0)
                    violations.Add(new PdfUa2StructureViolation("WTPDF-6.1.1",
                        "The file declares no WTPDF conformance level; §6.1.1 requires a PDF Declaration " +
                        "whose pdfd:conformsTo is " + ReuseDeclarationUri + " or " + AccessibilityDeclarationUri +
                        " (WTPDF 1.0 §6.1.1). It was judged at the conformance level for reuse."));
            }

            bool accessibility = levels.Contains(WtpdfConformanceLevel.Accessibility);
            foreach (PdfUa2StructureViolation finding in PdfUa2StructureChecker.ValidateDocument(doc).Violations)
            {
                if (!accessibility && AccessibilityOnlyCodes.Contains(finding.Code)) continue;
                violations.Add(finding);
            }

            return new PdfWtpdfResult(levels, violations, accessibility
                ? WtpdfConformanceLevel.Accessibility
                : WtpdfConformanceLevel.Reuse);
        }
    }

    /// <summary>Result of a WTPDF 1.0 check.</summary>
    public sealed class PdfWtpdfResult
    {
        /// <summary>The conformance levels the file declares, in declaration order; empty when none.</summary>
        public IReadOnlyList<WtpdfConformanceLevel> DeclaredLevels { get; }

        /// <summary>The level the file was judged at: accessibility when declared, otherwise reuse.</summary>
        public WtpdfConformanceLevel JudgedLevel { get; }

        /// <summary>Findings — <c>WTPDF-</c> codes for the declaration itself, <c>UA2-</c> codes for
        /// Clause 8 requirements (the clause numbers WTPDF shares with ISO 14289-2).</summary>
        public IReadOnlyList<PdfUa2StructureViolation> Violations { get; }

        /// <summary>True when there are no findings. Not a conformance certificate — see
        /// <see cref="PdfWtpdfChecker"/>.</summary>
        public bool IsConforming => Violations.Count == 0;

        internal PdfWtpdfResult(
            IReadOnlyList<WtpdfConformanceLevel> declaredLevels,
            IReadOnlyList<PdfUa2StructureViolation> violations,
            WtpdfConformanceLevel judgedLevel)
        {
            DeclaredLevels = declaredLevels;
            Violations = violations;
            JudgedLevel = judgedLevel;
        }
    }
}
