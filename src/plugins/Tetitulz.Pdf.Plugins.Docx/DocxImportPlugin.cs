// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Docx
{
    /// <summary>
    /// <see cref="IImportPlugin"/> that converts DOCX (Office Open XML) documents to PDF.
    /// Dispatched by <c>PdfOperations.Import(source, pdfOutput, "docx")</c>.
    ///
    /// Drop <c>Tetitulz.Pdf.Plugins.Docx.dll</c> into the application plugin
    /// folder and call <c>PdfPluginHost.Instance.RegisterFromDirectory(pluginFolder)</c>
    /// to enable automatic DOCX-to-PDF conversion without a compile-time dependency.
    ///
    /// Or register explicitly at startup:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new DocxImportPlugin());
    /// </code>
    /// </summary>
    public sealed class DocxImportPlugin : IImportPlugin
    {
        public string Name        => "Tetitulz.Plugins.DocxImport";
        public string Description => "Converts DOCX (Office Open XML) documents to PDF.";
        public Version Version    => new Version(1, 0, 0);

        public string FormatKey    => "docx";
        public string SourceMimeType =>
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        public void Initialize() { }
        public void Shutdown()   { }

        /// <summary>
        /// Recognised <see cref="ImportOptions.Extra"/> keys (case-insensitive), each named after
        /// the <c>DocxToPdfOptions</c> property it sets: <c>PageWidth</c>, <c>PageHeight</c>, the
        /// four margins, <c>DefaultFontSize</c>, <c>EmbedImages</c>, <c>CompressStreams</c>,
        /// <c>UseDocumentMargins</c>, <c>UseLegacyRenderer</c>, <c>TaggedPdf</c> and
        /// <c>LibreOfficePath</c>. An unrecognised key is <b>refused</b>, not ignored.
        ///
        /// <para>Refused rather than ignored, so a mistyped key cannot produce the same output as an
        /// honoured one.</para>
        ///
        /// <para><c>Engine</c> is deliberately NOT settable here: selecting the LibreOffice engine
        /// spawns an external process, and that is not a decision a loosely-typed string bag from
        /// an unknown caller should make. <c>LibreOfficePath</c> is accepted because it is inert
        /// unless the engine was chosen in code.</para>
        /// </summary>
        public void Import(Stream source, Stream pdfOutput, ImportOptions options)
        {
            if (source == null)    throw new ArgumentNullException(nameof(source));
            if (pdfOutput == null) throw new ArgumentNullException(nameof(pdfOutput));

            Tetitulz.Pdf.DocxToPdf.DocxToPdfConverter.Convert(source, pdfOutput, MapOptions(options));
        }

        private const string Plugin = "Tetitulz.Plugins.DocxImport";

        private static Tetitulz.Pdf.DocxToPdf.DocxToPdfOptions? MapOptions(ImportOptions? options)
        {
            var extra = options?.Extra;
            if (extra == null || extra.Count == 0) return null;   // exactly the old behaviour

            var o = new Tetitulz.Pdf.DocxToPdf.DocxToPdfOptions();

            DocxPluginOptionReader.RejectUnknownKeys(
                Plugin, extra,
                nameof(o.PageWidth), nameof(o.PageHeight),
                nameof(o.MarginTop), nameof(o.MarginBottom), nameof(o.MarginLeft), nameof(o.MarginRight),
                nameof(o.DefaultFontSize), nameof(o.EmbedImages), nameof(o.CompressStreams),
                nameof(o.UseDocumentMargins), nameof(o.UseLegacyRenderer), nameof(o.TaggedPdf),
                nameof(o.LibreOfficePath));

            double pw = o.PageWidth, ph = o.PageHeight, fs = o.DefaultFontSize;
            DocxPluginOptionReader.ReadPositiveDouble(Plugin, extra, nameof(o.PageWidth), ref pw);
            DocxPluginOptionReader.ReadPositiveDouble(Plugin, extra, nameof(o.PageHeight), ref ph);
            DocxPluginOptionReader.ReadPositiveDouble(Plugin, extra, nameof(o.DefaultFontSize), ref fs);
            o.PageWidth = pw; o.PageHeight = ph; o.DefaultFontSize = fs;

            double mt = o.MarginTop, mb = o.MarginBottom, ml = o.MarginLeft, mr = o.MarginRight;
            DocxPluginOptionReader.ReadNonNegativeDouble(Plugin, extra, nameof(o.MarginTop), ref mt);
            DocxPluginOptionReader.ReadNonNegativeDouble(Plugin, extra, nameof(o.MarginBottom), ref mb);
            DocxPluginOptionReader.ReadNonNegativeDouble(Plugin, extra, nameof(o.MarginLeft), ref ml);
            DocxPluginOptionReader.ReadNonNegativeDouble(Plugin, extra, nameof(o.MarginRight), ref mr);
            o.MarginTop = mt; o.MarginBottom = mb; o.MarginLeft = ml; o.MarginRight = mr;

            bool ei = o.EmbedImages, cs = o.CompressStreams, ud = o.UseDocumentMargins;
            bool lr = o.UseLegacyRenderer, tp = o.TaggedPdf;
            DocxPluginOptionReader.ReadBool(Plugin, extra, nameof(o.EmbedImages), ref ei);
            DocxPluginOptionReader.ReadBool(Plugin, extra, nameof(o.CompressStreams), ref cs);
            DocxPluginOptionReader.ReadBool(Plugin, extra, nameof(o.UseDocumentMargins), ref ud);
            DocxPluginOptionReader.ReadBool(Plugin, extra, nameof(o.UseLegacyRenderer), ref lr);
            DocxPluginOptionReader.ReadBool(Plugin, extra, nameof(o.TaggedPdf), ref tp);
            o.EmbedImages = ei; o.CompressStreams = cs; o.UseDocumentMargins = ud;
            o.UseLegacyRenderer = lr; o.TaggedPdf = tp;

            string soffice = o.LibreOfficePath;
            DocxPluginOptionReader.ReadString(Plugin, extra, nameof(o.LibreOfficePath), ref soffice);
            o.LibreOfficePath = soffice;

            return o;
        }
    }
}
