// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>
    /// Shared implementation for the <c>.ps</c> and <c>.eps</c> import plugins.
    ///
    /// <para>Two plugin classes rather than one because <c>IImportPlugin</c> carries a single
    /// <see cref="IImportPlugin.FormatKey"/> and <c>PdfPluginHost.FindImportPlugin</c> matches one
    /// key per plugin — so a caller writing <c>PdfOperations.Import(src, dst, "eps")</c> would
    /// otherwise miss a plugin registered as <c>"ps"</c>. Registration also requires distinct
    /// <see cref="IPdfPlugin.Name"/>s. This mirrors the Docx plugin's import/export pair.</para>
    /// </summary>
    public abstract class PostScriptImportPluginBase : IImportPlugin
    {
        private readonly PostScriptConversionOptions _options;

        /// <summary>Uses <see cref="PostScriptConversionOptions.Default"/>.</summary>
        protected PostScriptImportPluginBase() : this(null) { }

        /// <summary>Uses <paramref name="options"/>, for a host that needs different bounds.</summary>
        protected PostScriptImportPluginBase(PostScriptConversionOptions options)
            => _options = options;

        /// <inheritdoc/>
        public abstract string Name { get; }

        /// <inheritdoc/>
        public abstract string FormatKey { get; }

        /// <inheritdoc/>
        public abstract string Description { get; }

        /// <summary>Both <c>.ps</c> and <c>.eps</c> are <c>application/postscript</c>.</summary>
        public string SourceMimeType => "application/postscript";

        /// <inheritdoc/>
        public Version Version => new(1, 0, 0);

        public void Initialize() { }
        public void Shutdown() { }

        /// <summary>
        /// Converts <paramref name="source"/> to a PDF written to <paramref name="pdfOutput"/>.
        ///
        /// <para>Options are cloned per call: <c>ConvertDocument</c> forces
        /// <see cref="ShowPageBehavior.NewPage"/>, and mutating a shared instance would make one
        /// import silently change the behaviour of a concurrent fragment conversion using the same
        /// object.</para>
        /// </summary>
        public void Import(Stream source, Stream pdfOutput, ImportOptions options)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (pdfOutput == null) throw new ArgumentNullException(nameof(pdfOutput));

            // Not cloned here: ConvertDocument copies the options itself, precisely so that forcing
            // ShowPage cannot reach into a caller's object. A second hand-written copy here would
            // list every property again and leave two places to update whenever a property is
            // added; one of them would eventually be missed.
            PostScriptConversion.ConvertDocument(source, pdfOutput,
                                                 _options ?? PostScriptConversionOptions.Default);
        }
    }

    /// <summary>
    /// Imports a PostScript program (<c>.ps</c>) as a PDF, one page per <c>showpage</c>.
    ///
    /// <code>
    ///   PdfPluginHost.Instance.Register(new PostScriptImportPlugin());
    ///   PdfOperations.Import(psStream, pdfStream, "ps");
    /// </code>
    /// </summary>
    public sealed class PostScriptImportPlugin : PostScriptImportPluginBase
    {
        public PostScriptImportPlugin() { }
        public PostScriptImportPlugin(PostScriptConversionOptions options) : base(options) { }

        public override string Name => "Tetitulz.Plugins.PostScriptImport";
        public override string FormatKey => "ps";
        public override string Description =>
            "Imports PostScript (.ps) as PDF, one page per showpage.";
    }

    /// <summary>
    /// Imports an Encapsulated PostScript illustration (<c>.eps</c>) as a PDF.
    ///
    /// <para>Identical machinery to <see cref="PostScriptImportPlugin"/> — an EPS is a PostScript
    /// program with extra rules about what it may not do. The practical difference is that an EPS
    /// declares <c>%%BoundingBox</c> and does not call <c>showpage</c>, so it yields one page sized
    /// to its declared box rather than to a paper size.</para>
    /// </summary>
    public sealed class EpsImportPlugin : PostScriptImportPluginBase
    {
        public EpsImportPlugin() { }
        public EpsImportPlugin(PostScriptConversionOptions options) : base(options) { }

        public override string Name => "Tetitulz.Plugins.EpsImport";
        public override string FormatKey => "eps";
        public override string Description =>
            "Imports Encapsulated PostScript (.eps) as a single-page PDF sized to its %%BoundingBox.";
    }
}
