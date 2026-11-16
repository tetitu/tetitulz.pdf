// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// <see cref="IConformanceConversionPlugin"/> over <see cref="PdfAConverter"/> and
    /// <see cref="PdfXConverter"/>, so a caller holding only <c>PdfOperations</c> can reach them.
    /// </summary>
    /// <remarks>
    /// <para>Register one per target, or all at once:</para>
    /// <code>
    ///   PdfPluginHost.Instance.Register(new ConformanceConversionPlugin("PDF/A-2b"));
    ///   ConformanceConversionPlugin.RegisterAll(PdfPluginHost.Instance);
    /// </code>
    /// <para><b>The target vocabulary is the validators'</b>, deliberately — convert to a level and
    /// then hand the same string to <c>PdfOperations.Validate</c> over the output. A converter's own
    /// verdict is its opinion of its own work; the validator is the second opinion.</para>
    ///
    /// <para><b>Options are per-instance</b>, because conversion needs things this contract cannot
    /// carry — an ICC profile, a document title, whether declaring conformance regardless is
    /// acceptable. A caller who needs them constructs the plugin with them; a caller who does not
    /// gets the converter's own defaults.</para>
    /// </remarks>
    public sealed class ConformanceConversionPlugin : IConformanceConversionPlugin
    {
        /// <summary>Every target this plugin can be constructed for.</summary>
        /// <remarks>
        /// PDF/A-4 is deliberately absent: <see cref="PdfAConverter"/> exposes 1b, 2b and 3b, and
        /// naming a target here that nothing converts to would be a registration a caller could
        /// discover through <c>GetSupportedConversionTargets</c> and then fail to use.
        /// </remarks>
        public static IReadOnlyList<string> SupportedTargets { get; } = new[]
        {
            "PDF/A-1b", "PDF/A-2b", "PDF/A-3b", "PDF/X-1a", "PDF/X-3", "PDF/X-4",
        };

        private readonly string _target;
        private readonly PdfAConversionOptions _pdfAOptions;
        private readonly PdfXConversionOptions _pdfXOptions;

        /// <summary>Creates a plugin for one target, with the converters' own default options.</summary>
        /// <param name="targetConformanceLevel">One of <see cref="SupportedTargets"/>.</param>
        public ConformanceConversionPlugin(string targetConformanceLevel)
            : this(targetConformanceLevel, null, null) { }

        /// <summary>Creates a plugin for one target with explicit conversion options.</summary>
        /// <param name="targetConformanceLevel">One of <see cref="SupportedTargets"/>.</param>
        /// <param name="pdfAOptions">Used when the target is a PDF/A level; may be null.</param>
        /// <param name="pdfXOptions">
        /// Used when the target is a PDF/X level; may be null. Its <c>Level</c> is set from the
        /// target rather than read, so the two cannot disagree about what is being produced.
        /// </param>
        public ConformanceConversionPlugin(string targetConformanceLevel,
            PdfAConversionOptions pdfAOptions, PdfXConversionOptions pdfXOptions)
        {
            if (string.IsNullOrWhiteSpace(targetConformanceLevel))
                throw new ArgumentException("A target conformance level is required.",
                    nameof(targetConformanceLevel));

            _target = Canonical(targetConformanceLevel)
                ?? throw new ArgumentException(
                    $"Unsupported conversion target '{targetConformanceLevel}'. Supported: " +
                    string.Join(", ", SupportedTargets) + ".", nameof(targetConformanceLevel));

            _pdfAOptions = pdfAOptions;
            _pdfXOptions = pdfXOptions;
        }

        public string TargetConformanceLevel => _target;
        public string Name        => $"Tetitulz.Plugins.ConformanceConversion.{_target}";
        public string Description => $"Converts a document towards {_target}.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        public PluginConversionResult Convert(Stream input, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            return _target.StartsWith("PDF/A", StringComparison.Ordinal)
                ? ConvertPdfA(input, output)
                : ConvertPdfX(input, output);
        }

        private PluginConversionResult ConvertPdfA(Stream input, Stream output)
        {
            var options = _pdfAOptions ?? new PdfAConversionOptions();
            PdfAConversionResult inner = _target switch
            {
                "PDF/A-1b" => PdfAConverter.ConvertToPdfA1b(input, output, options),
                "PDF/A-2b" => PdfAConverter.ConvertToPdfA2b(input, output, options),
                "PDF/A-3b" => PdfAConverter.ConvertToPdfA3b(input, output, options),
                // Unreachable: the constructor refuses anything not in SupportedTargets. Present
                // because a target added to that list and not to this switch must fail loudly
                // rather than silently convert to the wrong part.
                _ => throw new InvalidOperationException(
                         $"No PDF/A converter is wired for '{_target}'."),
            };

            var result = new PluginConversionResult
            {
                TargetConformanceLevel = _target,
                OutputWritten          = inner.OutputWritten,
                IsConformant           = inner.IsConformant,
            };
            foreach (string b in inner.Blockers) result.Blockers.Add(b);
            foreach (string w in inner.Warnings) result.Warnings.Add(w);
            return result;
        }

        private PluginConversionResult ConvertPdfX(Stream input, Stream output)
        {
            var options = _pdfXOptions ?? new PdfXConversionOptions();
            options.Level = _target switch
            {
                "PDF/X-1a" => PdfXConformanceLevel.X1a,
                "PDF/X-3"  => PdfXConformanceLevel.X3,
                "PDF/X-4"  => PdfXConformanceLevel.X4,
                _ => throw new InvalidOperationException(
                         $"No PDF/X level is wired for '{_target}'."),
            };

            PdfXConversionResult inner = PdfXConverter.Convert(input, output, options);

            // OutputWritten has no counterpart on the PDF/X result, and inferring it from
            // IsConformant would be wrong in the direction that matters: the converter writes an
            // improved file it could not fully repair, and reporting "nothing was written" would
            // send a caller looking for an output that is sitting there. The stream itself is the
            // only honest source.
            var result = new PluginConversionResult
            {
                TargetConformanceLevel = _target,
                OutputWritten          = output.CanSeek ? output.Length > 0 : true,
                IsConformant           = inner.IsConformant,
            };
            foreach (string b in inner.Blockers) result.Blockers.Add(b);
            foreach (string w in inner.Warnings) result.Warnings.Add(w);
            foreach (string p in inner.PageBoxesLeftAlone)
                result.Warnings.Add($"page boxes left alone: {p}");
            return result;
        }

        /// <summary>
        /// The canonical spelling of <paramref name="target"/>, or null when nothing converts to it.
        /// Case-insensitive, so a caller may pass "pdf/a-2b".
        /// </summary>
        private static string Canonical(string target)
        {
            foreach (string s in SupportedTargets)
                if (string.Equals(s, target, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>Registers a plugin for every target in <see cref="SupportedTargets"/>.</summary>
        public static void RegisterAll(PdfPluginHost host)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            foreach (string target in SupportedTargets)
                host.Register(new ConformanceConversionPlugin(target));
        }
    }
}
