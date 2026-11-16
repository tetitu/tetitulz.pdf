// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// <see cref="IAccessibilityPlugin"/> that checks and fixes PDF accessibility issues
    /// using the built-in <see cref="PdfUaChecker"/> and <see cref="PdfAccessibilityFixer"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new AccessibilityPlugin());
    /// </code>
    /// </summary>
    public sealed class AccessibilityPlugin : IAccessibilityPlugin
    {
        public string Name        => "Tetitulz.Plugins.Accessibility";
        public string Description => "Checks and fixes PDF/UA accessibility using the built-in engine.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        /// <summary>
        /// Runs the built-in PDF/UA checker and maps violations to
        /// <see cref="PluginAccessibilityViolation"/> entries.
        /// </summary>
        public IReadOnlyList<PluginAccessibilityViolation> Check(Stream pdfStream)
        {
            if (pdfStream == null) throw new ArgumentNullException(nameof(pdfStream));

            long pos = pdfStream.CanSeek ? pdfStream.Position : -1;
            PdfUaValidationResult result = PdfUaChecker.Validate(pdfStream)
                ?? throw new InvalidOperationException("PdfUaChecker returned null.");
            if (pdfStream.CanSeek && pos >= 0) pdfStream.Position = pos;

            var violations = new List<PluginAccessibilityViolation>();
            foreach (var v in result.Violations)
                violations.Add(new PluginAccessibilityViolation
                {
                    Code    = v.Code    ?? string.Empty,
                    Message = v.Message ?? string.Empty,
                });
            return violations;
        }

        /// <summary>
        /// Applies the built-in automated accessibility fixes, mapping
        /// <see cref="PluginAccessibilityFixOptions"/> to <see cref="AccessibilityFixOptions"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Two of the four options are not implementable here, and the plugin says
        /// so.</b> <c>TagContent</c> and <c>AddAltText</c> both default to <see langword="true"/>;
        /// left unreported, the caller would get tooltips and a tab order, no tagging and no alt
        /// text, and nothing saying that half the request was never attempted — the next PDF/UA
        /// check would still fail UA01 and UA07. Tagging and figure alt
        /// text live in <c>Tetitulz.Pdf.Accessibility</c> (<c>PdfAccessibility.AutoTag</c> and
        /// <c>SetFigureAltText</c>), which this plugin may not reference: <c>Accessibility</c>
        /// already references <c>Conformance</c>, so the edge back would close a cycle.</para>
        /// <para>Reported rather than thrown, because both flags default to <c>true</c>: throwing
        /// would refuse every default-constructed request, including the ones whose only real ask
        /// is the language. Every warning <see cref="PdfAccessibilityFixer"/> produced is forwarded
        /// too — the interface returns <c>void</c>, so this sink is the only channel the result
        /// object has.</para>
        /// </remarks>
        public void Fix(Stream input, Stream output, PluginAccessibilityFixOptions options)
        {
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var fixOptions = new AccessibilityFixOptions();
            if (options != null)
            {
                fixOptions.AddLanguage    = options.SetLanguage;
                fixOptions.DefaultLanguage = options.LanguageCode ?? "en";

                if (options.TagContent)
                    DocumentEditDiagnostics.Report("AccessibilityPlugin.Fix",
                        "TagContent was requested and is not implemented by this plugin; nothing " +
                        "was tagged. Use Tetitulz.Pdf.Accessibility.PdfAccessibility.AutoTag, which " +
                        "this plugin may not reference (Accessibility → Conformance already exists, " +
                        "so the reverse edge would be a cycle).");
                if (options.AddAltText)
                    DocumentEditDiagnostics.Report("AccessibilityPlugin.Fix",
                        "AddAltText was requested and is not implemented by this plugin; no figure " +
                        "received alternate text. Use " +
                        "Tetitulz.Pdf.Accessibility.PdfAccessibility.SetFigureAltText.");
            }

            AccessibilityFixResult result = PdfAccessibilityFixer.Fix(input, output, fixOptions);

            // The interface returns void, so a warning the fixer produced would otherwise be
            // discarded with the result object that carries it.
            if (result?.Warnings != null)
                foreach (string warning in result.Warnings)
                    DocumentEditDiagnostics.Report("AccessibilityPlugin.Fix", warning);
        }
    }
}
