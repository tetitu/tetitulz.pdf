// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// <see cref="IValidationPlugin"/> that validates PDF documents against PDF/X
    /// conformance levels (ISO 15930) using the built-in <see cref="PdfXValidator"/>.
    ///
    /// Register one instance per conformance level:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new PdfXValidationPlugin(PdfXConformanceLevel.X1a));
    ///   PdfPluginHost.Instance.Register(new PdfXValidationPlugin(PdfXConformanceLevel.X3));
    /// </code>
    /// Or register all at once:
    /// <code>
    ///   PdfXValidationPlugin.RegisterAll(PdfPluginHost.Instance);
    /// </code>
    /// </summary>
    public sealed class PdfXValidationPlugin : IValidationPlugin
    {
        private readonly PdfXConformanceLevel _level;

        public PdfXValidationPlugin(PdfXConformanceLevel level = PdfXConformanceLevel.X1a)
            => _level = level;

        public string Name        => $"Tetitulz.Plugins.PdfXValidation.{_level}";
        public string Description => $"Validates PDF/X conformance level {_level} (ISO 15930).";
        public Version Version    => new Version(1, 0, 0);

        // Exhaustive over the enum on purpose: a fallback arm interpolating the *member name*
        // would report PDF/X-4 as "PDF/X-X4". A new level must be named here.
        public string ConformanceLevel => _level switch
        {
            PdfXConformanceLevel.X1a => "PDF/X-1a",
            PdfXConformanceLevel.X3  => "PDF/X-3",
            PdfXConformanceLevel.X4  => "PDF/X-4",
            PdfXConformanceLevel.X6  => "PDF/X-6",
            _ => "PDF/X"
        };

        public void Initialize() { }
        public void Shutdown()   { }

        public PluginValidationResult Validate(Stream pdfStream)
        {
            PdfXValidationResult inner = ValidationPluginAdapter.RunAt(
                pdfStream, s => PdfXValidator.Validate(s, _level), "PdfXValidator");

            var result = new PluginValidationResult
            {
                ConformanceLevel = ConformanceLevel,
                IsConformant     = inner.IsConforming,
            };
            foreach (var v in inner.Violations)
                result.Violations.Add(ValidationPluginAdapter.Entry(v.Code, v.Message));

            // The X99 entries. Dropping them would turn an otherwise-conforming document whose
            // content checks were all skipped by a ceiling into `IsConformant == true` with an
            // empty violation list and nothing at all to say that nothing had been examined. X99
            // must not touch IsConformant: it says a check did not run, not that the document is
            // wrong.
            foreach (var i in inner.Informational)
                result.Informational.Add(ValidationPluginAdapter.Entry(i.Code, i.Message));
            return result;
        }

        /// <summary>Registers plugins for all PDF/X conformance levels.</summary>
        public static void RegisterAll(PdfPluginHost host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            foreach (PdfXConformanceLevel level in Enum.GetValues(typeof(PdfXConformanceLevel)))
                host.Register(new PdfXValidationPlugin(level));
        }
    }
}
