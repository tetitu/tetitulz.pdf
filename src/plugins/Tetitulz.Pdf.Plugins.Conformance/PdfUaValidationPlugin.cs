// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// <see cref="IValidationPlugin"/> that validates PDF documents against PDF/UA-1
    /// (ISO 14289-1) using the built-in <see cref="PdfUaChecker"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new PdfUaValidationPlugin());
    /// </code>
    /// </summary>
    public sealed class PdfUaValidationPlugin : IValidationPlugin
    {
        public string Name           => "Tetitulz.Plugins.PdfUaValidation";
        public string Description    => "Validates PDF/UA-1 conformance (ISO 14289-1).";
        public Version Version       => new Version(1, 0, 0);
        public string ConformanceLevel => "PDF/UA-1";

        public void Initialize() { }
        public void Shutdown()   { }

        public PluginValidationResult Validate(Stream pdfStream)
        {
            PdfUaValidationResult inner = ValidationPluginAdapter.RunAt(
                pdfStream, s => PdfUaChecker.Validate(s), "PdfUaChecker");

            var result = new PluginValidationResult { ConformanceLevel = ConformanceLevel };

            // UA99 is INFORMATIONAL, and must not flip IsConformant: it says a check did not run,
            // not that the document is wrong. PdfXValidationPlugin states the same rule for X99 and
            // CLAUDE.md states it for UA99 — but this plugin copied every entry into Violations, so
            // a conformant, encrypted PDF/UA document (legal under ISO 14289-1 §7.1, and the case
            // CheckEncryption exists to permit) came back IsConformant == false with one
            // "violation" whose text says the audit was incomplete. The same host validating the
            // same file for PDF/X got the opposite answer from the sibling plugin next door.
            foreach (var v in inner.Violations)
            {
                var mapped = ValidationPluginAdapter.Entry(v.Code, v.Message);
                if (mapped.Code == "UA99") result.Informational.Add(mapped);
                else result.Violations.Add(mapped);
            }

            // Computed from what is left, rather than copied from inner.IsConforming, which counts
            // UA99 — otherwise the split above would move the entry and change nothing.
            result.IsConformant = result.Violations.Count == 0;
            return result;
        }
    }
}
