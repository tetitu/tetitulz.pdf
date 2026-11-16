// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Html
{
    /// <summary>
    /// <see cref="IExportPlugin"/> that converts a PDF document to HTML using the
    /// built-in <see cref="PdfHtmlConverter"/>.
    /// Dispatched by <c>PdfOperations.Export(input, output, "html")</c>.
    ///
    /// <para><b>Recognised <see cref="ExportOptions.Extra"/> keys</b> (case-insensitive), each
    /// mapping onto the <see cref="HtmlConversionOptions"/> property of the same name:</para>
    /// <list type="table">
    ///   <item><term>EmbedImages</term><description><c>bool</c> — embed page images as data URIs (default true).</description></item>
    ///   <item><term>IncludeText</term><description><c>bool</c> — overlay selectable text (default true).</description></item>
    ///   <item><term>ImageDpi</term><description>positive <c>int</c> — page image resolution (default 96).</description></item>
    ///   <item><term>Title</term><description>non-empty <c>string</c> — the HTML title.</description></item>
    ///   <item><term>MaxEmbeddedImageBytes</term><description>positive <c>long</c> — cumulative base-64 budget (default 512 MiB).</description></item>
    ///   <item><term>Layout</term><description><c>bool</c> — emit positioned semantic text
    ///     (<see cref="PdfHtmlConverter.ConvertToLayoutHtml(System.IO.Stream, HtmlConversionOptions)"/>)
    ///     instead of a rasterized page with an invisible text overlay.</description></item>
    /// </list>
    /// <para>Any other key raises <see cref="ArgumentException"/>: a mistyped or unsupported setting
    /// would otherwise produce exactly the same output as an honoured one.</para>
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new HtmlExportPlugin());
    /// </code>
    /// </summary>
    public sealed class HtmlExportPlugin : IExportPlugin
    {
        public string Name           => "Tetitulz.Plugins.HtmlExport";
        public string Description    => "Exports PDF documents to HTML using the built-in converter.";
        public Version Version       => new Version(1, 0, 0);
        public string FormatKey      => "html";
        public string TargetMimeType => "text/html";

        public void Initialize() { }
        public void Shutdown()   { }

        /// <exception cref="PdfEncryptedDocumentException">
        /// The document is encrypted. <see cref="PdfDocument"/> loads neither the catalog nor the
        /// page tree of such a file, so the converter saw <b>zero pages</b> and emitted a page-less
        /// HTML skeleton — a successful-looking export carrying none of the document.
        /// </exception>
        public void Export(PdfDocument document, Stream output, ExportOptions? options)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (output == null)   throw new ArgumentNullException(nameof(output));
            Tetitulz.Pdf.Base.Operations.EncryptedRead.RefuseConversion(document, "HTML");

            var conversion = ReadOptions(options, out bool layout);

            long savedPos = document.BaseStream.Position;
            try
            {
                document.BaseStream.Position = 0;
                // The stream overloads, not the string ones: materialising the whole document as a
                // string and encoding that to a second full copy would keep the builder's chunks, the
                // string and the byte array live at once — at the default 512 MiB image budget.
                if (layout)
                    PdfHtmlConverter.ConvertToLayoutHtml(document.BaseStream, output, conversion);
                else
                    PdfHtmlConverter.ConvertToHtml(document.BaseStream, output, conversion);
            }
            finally
            {
                document.BaseStream.Position = savedPos;
            }
        }

        /// <summary>
        /// Maps <see cref="ExportOptions.Extra"/> onto <see cref="HtmlConversionOptions"/>, refusing
        /// anything it cannot honour. See the table on the class for the keys.
        /// </summary>
        private static HtmlConversionOptions ReadOptions(ExportOptions? options, out bool layout)
        {
            const string Plugin = "HtmlExportPlugin";
            var conversion = new HtmlConversionOptions();
            layout = false;

            var extra = options?.Extra;
            if (extra == null || extra.Count == 0) return conversion;

            PluginOptionReader.RejectUnknownKeys(Plugin, extra,
                nameof(HtmlConversionOptions.EmbedImages),
                nameof(HtmlConversionOptions.IncludeText),
                nameof(HtmlConversionOptions.ImageDpi),
                nameof(HtmlConversionOptions.Title),
                nameof(HtmlConversionOptions.MaxEmbeddedImageBytes),
                "Layout");

            bool embedImages = conversion.EmbedImages;
            bool includeText = conversion.IncludeText;
            int  imageDpi    = conversion.ImageDpi;
            string? title    = conversion.Title;
            long maxEmbedded = conversion.MaxEmbeddedImageBytes;
            bool useLayout   = false;

            PluginOptionReader.ReadBool(Plugin, extra, nameof(HtmlConversionOptions.EmbedImages), ref embedImages);
            PluginOptionReader.ReadBool(Plugin, extra, nameof(HtmlConversionOptions.IncludeText), ref includeText);
            PluginOptionReader.ReadPositiveInt(Plugin, extra, nameof(HtmlConversionOptions.ImageDpi), ref imageDpi);
            PluginOptionReader.ReadString(Plugin, extra, nameof(HtmlConversionOptions.Title), ref title);
            PluginOptionReader.ReadPositiveLong(Plugin, extra, nameof(HtmlConversionOptions.MaxEmbeddedImageBytes), ref maxEmbedded);
            PluginOptionReader.ReadBool(Plugin, extra, "Layout", ref useLayout);

            conversion.EmbedImages = embedImages;
            conversion.IncludeText = includeText;
            conversion.ImageDpi    = imageDpi;
            conversion.Title       = title;
            conversion.MaxEmbeddedImageBytes = maxEmbedded;
            layout = useLayout;
            return conversion;
        }
    }
}
