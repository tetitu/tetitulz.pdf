// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// <see cref="IValidationPlugin"/> that validates PDF documents against PDF/A
    /// conformance levels (ISO 19005-1/2/3/4) using the built-in <see cref="PdfAValidator"/>.
    ///
    /// Register a separate instance per conformance part you wish to support:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new PdfAValidationPlugin(PdfAConformancePart.Part1));
    ///   PdfPluginHost.Instance.Register(new PdfAValidationPlugin(PdfAConformancePart.Part2));
    /// </code>
    /// Or register all four at once:
    /// <code>
    ///   PdfAValidationPlugin.RegisterAll(PdfPluginHost.Instance);
    /// </code>
    /// Each judges a file at the level it declares; one built with a <see cref="PdfAConformanceLevel"/>
    /// judges it at that level, under a name of its own, so a host can register several levels of a part:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new PdfAValidationPlugin(PdfAConformancePart.Part4, PdfAConformanceLevel.E));
    /// </code>
    /// </summary>
    public sealed class PdfAValidationPlugin : IValidationPlugin
    {
        private readonly PdfAConformancePart _part;
        private readonly PdfAConformanceLevel _level;

        public PdfAValidationPlugin(PdfAConformancePart part = PdfAConformancePart.Part1)
            => _part = part;

        /// <summary>A plugin judging every file at <paramref name="level"/> of <paramref name="part"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a level of <paramref name="part"/>.</exception>
        public PdfAValidationPlugin(PdfAConformancePart part, PdfAConformanceLevel level)
        {
            PdfAValidator.RequireLevelOf(part, level);
            _part = part;
            _level = level;
        }

        public string Name => $"Tetitulz.Plugins.PdfAValidation.Part{(int)_part}"
                              + (_level == PdfAConformanceLevel.Declared ? "" : "." + _level);
        public string Description => $"Validates PDF/A-{(int)_part} conformance (ISO 19005-{(int)_part}).";
        public Version Version    => new Version(1, 0, 0);

        public string ConformanceLevel => _level != PdfAConformanceLevel.Declared
            ? $"PDF/A-{(int)_part}{PdfAValidator.LetterOf(_level)?.ToLowerInvariant()}"
            : _part switch
            {
                PdfAConformancePart.Part1 => "PDF/A-1b",
                PdfAConformancePart.Part2 => "PDF/A-2b",
                PdfAConformancePart.Part3 => "PDF/A-3b",
                PdfAConformancePart.Part4 => "PDF/A-4",
                _ => $"PDF/A-{(int)_part}"
            };

        public void Initialize() { }
        public void Shutdown()   { }

        public PluginValidationResult Validate(Stream pdfStream)
        {
            PdfAValidationResult inner = ValidationPluginAdapter.RunAt(
                pdfStream, s => PdfAValidator.Validate(s, _part, _level), "PdfAValidator");

            var result = new PluginValidationResult
            {
                ConformanceLevel = ConformanceLevel,
                IsConformant     = inner.IsConformant,
            };
            foreach (var v in inner.Violations)
                result.Violations.Add(ValidationPluginAdapter.Entry(v.Code, v.Message));
            // A99 — a check that did not run in full. Forwarded as PdfXValidationPlugin forwards
            // X99, and for the same reason: dropped here, a partial run reaches the host as a clean
            // one. Like X99 it leaves IsConformant alone.
            foreach (var i in inner.Informational)
                result.Informational.Add(ValidationPluginAdapter.Entry(i.Code, i.Message));
            return result;
        }

        /// <summary>Registers plugins for all four PDF/A conformance parts.</summary>
        public static void RegisterAll(PdfPluginHost host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            foreach (PdfAConformancePart part in Enum.GetValues(typeof(PdfAConformancePart)))
                host.Register(new PdfAValidationPlugin(part));
        }
    }
}
