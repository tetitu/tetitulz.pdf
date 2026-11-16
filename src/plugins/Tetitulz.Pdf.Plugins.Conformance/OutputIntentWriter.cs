// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The output intent both converters write, through Base's one writer
    /// (<see cref="PdfIccEmbedder.WriteOutputIntent"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>One writer.</b> A second would disagree with the first on everything a validator
    /// checks: the caller's <c>/N</c> written over the profile's (a CMYK profile under the PDF/A
    /// converter's default of 3 then fails this plugin's own A20); arbitrary bytes accepted as a
    /// "conformant" <c>/DestOutputProfile</c>, which the validators, reading "cannot verify" as not
    /// "wrong", let through; an encrypted document not refused; objects written unattached when the
    /// trailer's <c>/Root</c> is not a reference; an intent of the same subtype appended beside the
    /// old one, which X11 and A15 fail. What is here is only what is particular to the
    /// converters.</para>
    ///
    /// <para>The <b>conformance decision</b> deliberately does not live here: this writes what it is
    /// told and reports what it wrote. Whether a missing profile blocks conformance is the caller's
    /// rule, because it differs by standard and by level.</para>
    /// </remarks>
    internal static class OutputIntentWriter
    {
        /// <param name="subtype"><c>GTS_PDFA1</c> or <c>GTS_PDFX</c> — both validators key on it.</param>
        /// <param name="conditionIdentifier">
        /// <c>/OutputConditionIdentifier</c>, also written as <c>/Info</c>, with the ICC registry as
        /// <c>/RegistryName</c>.
        /// </param>
        /// <param name="iccProfile">
        /// The profile, or null to write the intent without one. <b>Writing none is not silently
        /// fine</b> — it is what the caller's conformance rule must account for.
        /// </param>
        /// <param name="trapped"><c>/Trapped</c> for <c>/Info</c>, or null to leave it alone.</param>
        /// <param name="input">The source PDF.</param>
        /// <param name="output">Receives the original bytes plus the appended revision.</param>
        internal static OutputIntentWritten Write(Stream input, Stream output, string subtype,
            string conditionIdentifier, byte[] iccProfile, bool? trapped)
            => PdfIccEmbedder.WriteOutputIntent(input, output, new OutputIntentSpec
            {
                Subtype = subtype,
                OutputConditionIdentifier = conditionIdentifier,
                RegistryName = "http://www.color.org",
                Info = conditionIdentifier,
                Profile = iccProfile,
                Trapped = trapped,
            });
    }
}
