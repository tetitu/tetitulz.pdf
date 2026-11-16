// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Reflection;

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// The <c>/Producer</c> string this library stamps on documents it writes (ISO 32000-1
    /// §14.3.3, Table 317).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One place, because a version that drifts is worse than no version.</b> Every
    /// document-creation path writes it, so a file produced by this library is distinguishable from
    /// one produced by anything else — which is exactly what a support request needs to know
    /// first.
    /// </para>
    /// <para>
    /// <b>Producer, not Creator.</b> §14.3.3 splits them: <c>/Creator</c> names the application
    /// that authored the <i>original document</i> (Word, a report generator, the end user's tool)
    /// and must be preserved across a conversion; <c>/Producer</c> names whatever turned it into
    /// the PDF in front of you, which is this library. Converters therefore set Producer and leave
    /// any inbound Creator alone.
    /// </para>
    /// <para>
    /// The version comes from the assembly's informational version, which MinVer derives from the
    /// git tag — so a released build says <c>Tetitulz.Pdf 1.2.3</c> and an untagged local build says
    /// <c>Tetitulz.Pdf 0.0.0-alpha.0.71</c> rather than claiming a release it is not. The build
    /// metadata after <c>+</c> (the commit hash) is trimmed: it is noise in a document property and
    /// makes byte-comparison of two otherwise identical outputs fail.
    /// </para>
    /// </remarks>
    public static class PdfProducer
    {
        /// <summary>The product name alone, without a version.</summary>
        public const string ProductName = "Tetitulz.Pdf";

        private static string _value;

        /// <summary>
        /// The full producer string, e.g. <c>Tetitulz.Pdf 1.2.3</c>. Computed once.
        /// </summary>
        public static string Value => _value ??= Build();

        private static string Build()
        {
            try
            {
                var asm = typeof(PdfProducer).GetTypeInfo().Assembly;
                string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                              ?.InformationalVersion;
                if (string.IsNullOrEmpty(v))
                    v = asm.GetName().Version?.ToString();

                if (string.IsNullOrEmpty(v)) return ProductName;

                // Trim "+<commit>" build metadata — noise in a document property, and it would make
                // two otherwise byte-identical outputs differ.
                int plus = v.IndexOf('+');
                if (plus >= 0) v = v.Substring(0, plus);

                return ProductName + " " + v;
            }
            catch
            {
                // Reflection is not worth failing a document write over.
                return ProductName;
            }
        }
    }
}
