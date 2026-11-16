// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>
    /// The output intent <see cref="PdfIccEmbedder.WriteOutputIntent"/> writes: ISO 32000-2 Table 401's
    /// entries, the profile, and whether to set <c>/Trapped</c> alongside.
    /// </summary>
    public sealed class OutputIntentSpec
    {
        /// <summary>
        /// <c>/S</c> — <c>GTS_PDFA1</c>, <c>GTS_PDFX</c>, <c>ISO_PDFE1</c>, or an extension's name.
        /// An existing intent with the same subtype is replaced.
        /// </summary>
        public string Subtype { get; set; } = PdfIccEmbedder.PdfA1Subtype;

        /// <summary>
        /// <c>/OutputConditionIdentifier</c>, required by Table 401: a registered characterisation's
        /// name, or <see cref="PdfIccEmbedder.DefaultOutputConditionId"/>.
        /// </summary>
        public string OutputConditionIdentifier { get; set; } = PdfIccEmbedder.DefaultOutputConditionId;

        /// <summary><c>/OutputCondition</c> — a human-readable description; omitted when null.</summary>
        public string OutputCondition { get; set; }

        /// <summary><c>/RegistryName</c> — where the identifier is registered; omitted when null.</summary>
        public string RegistryName { get; set; }

        /// <summary><c>/Info</c> — further human-readable information; omitted when null.</summary>
        public string Info { get; set; }

        /// <summary>
        /// ICC profile bytes for <c>/DestOutputProfile</c>, or null to write the intent without one
        /// (a registered characterisation some standards accept in its place). Its <c>/N</c> is the
        /// profile's own component count, read from its header.
        /// </summary>
        public byte[] Profile { get; set; }

        /// <summary>
        /// <c>true</c> or <c>false</c> to write <c>/Trapped</c> into <c>/Info</c> in the same revision,
        /// or null to leave <c>/Info</c> alone.
        /// </summary>
        public bool? Trapped { get; set; }
    }

    /// <summary>What <see cref="PdfIccEmbedder.WriteOutputIntent"/> put in the document.</summary>
    public sealed class OutputIntentWritten
    {
        internal OutputIntentWritten() { }

        /// <summary>True when a profile was embedded as <c>/DestOutputProfile</c>.</summary>
        public bool EmbeddedProfile { get; internal set; }

        /// <summary>The profile's component count, written as its <c>/N</c>; 0 with no profile.</summary>
        public int ProfileComponents { get; internal set; }

        /// <summary>True when <c>/Trapped</c> was written into <c>/Info</c>.</summary>
        public bool SetTrapped { get; internal set; }

        /// <summary>Intents of the same subtype the document already had, which this one replaced.</summary>
        public int ReplacedIntents { get; internal set; }
    }
}
