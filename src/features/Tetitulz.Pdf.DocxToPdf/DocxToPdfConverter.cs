// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using Tetitulz.Pdf.DocxToPdf.Engines;
using Tetitulz.Pdf.DocxToPdf.Rendering;

namespace Tetitulz.Pdf.DocxToPdf
{
    public static class DocxToPdfConverter
    {
        /// <summary>
        /// The most recent conversion anomaly reported on the <b>current thread</b>, or
        /// <c>null</c> if the last conversion rendered everything the source described.
        ///
        /// <para>
        /// A converter tolerates partial failures rather than refusing the document: content it
        /// cannot lay out is skipped, and content it cannot reproduce exactly is approximated.
        /// Both look, in the PDF, like content the DOCX never contained. Read this after a
        /// conversion to tell the two apart. Cleared at the start of every conversion, so a
        /// message here always belongs to the conversion that just ran on this thread.
        /// </para>
        /// </summary>
        public static string LastDiagnostic => Rendering.DocxDiagnostics.LastError;

        /// <summary>
        /// Raised for every tolerated failure, approximation or omission during a conversion, on
        /// the converting thread. Subscribe to capture all of them — <see cref="LastDiagnostic"/>
        /// keeps only the most recent, and one conversion commonly reports several.
        /// Each handler is invoked on its own (<c>Core.Util.DiagnosticFanOut</c>), so a
        /// throwing handler is contained and costs only its own notification.
        /// </summary>
        public static event Action<string> DiagnosticReported
        {
            add { Rendering.DocxDiagnostics.OnEvent += value; }
            remove { Rendering.DocxDiagnostics.OnEvent -= value; }
        }

        /// <summary>
        /// Converts a DOCX file to PDF.
        /// </summary>
        public static void Convert(string inputPath, string outputPath, DocxToPdfOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));

            options = options ?? DocxToPdfOptions.Default;
            Validate(options);
            BeginConversion();

            switch (options.Engine)
            {
                case ConversionEngine.LibreOffice:
                    LibreOfficeConverter.ConvertFile(inputPath, outputPath, options.LibreOfficePath);
                    break;
                case ConversionEngine.MicrosoftWord:
                    WordComConverter.ConvertFile(inputPath, outputPath);
                    break;
                case ConversionEngine.BuiltIn:
                default:
                    using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        ConvertBuiltIn(input, output, options);
                    }
                    break;
            }
        }

        /// <summary>
        /// Converts a DOCX stream to PDF.
        /// </summary>
        public static void Convert(Stream input, Stream output, DocxToPdfOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            options = options ?? DocxToPdfOptions.Default;
            Validate(options);
            BeginConversion();

            switch (options.Engine)
            {
                case ConversionEngine.LibreOffice:
                    LibreOfficeConverter.ConvertStream(input, output, options.LibreOfficePath);
                    break;
                case ConversionEngine.MicrosoftWord:
                    WordComConverter.ConvertStream(input, output);
                    break;
                case ConversionEngine.BuiltIn:
                default:
                    ConvertBuiltIn(input, output, options);
                    break;
            }
        }

        /// <summary>
        /// Converts a DOCX file to a PDF byte array.
        /// </summary>
        public static byte[] ConvertToBytes(string inputPath, DocxToPdfOptions options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));

            options = options ?? DocxToPdfOptions.Default;
            Validate(options);

            if (options.Engine != ConversionEngine.BuiltIn)
            {
                // The PDF is written inside a directory of this conversion's own, never beside every
                // other account's files in the temporary directory: a copy takes its source's mode.
                string holder = LibreOfficeConverter.CreatePrivateDirectory();
                try
                {
                    string tempOutput = Path.Combine(holder, "output.pdf");
                    Convert(inputPath, tempOutput, options);
                    return File.ReadAllBytes(tempOutput);
                }
                finally
                {
                    try { Directory.Delete(holder, recursive: true); } catch { }
                }
            }

            using (var ms = new MemoryStream())
            {
                using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ConvertBuiltIn(input, ms, options);
                }
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Converts a DOCX byte array to a PDF byte array.
        /// </summary>
        public static byte[] ConvertToBytes(byte[] docxData, DocxToPdfOptions options = null)
        {
            if (docxData == null) throw new ArgumentNullException(nameof(docxData));

            using (var input = new MemoryStream(docxData))
            using (var output = new MemoryStream())
            {
                Convert(input, output, options);
                return output.ToArray();
            }
        }

        /// <summary>
        /// Clears the per-thread diagnostic slot at the start of a conversion.
        /// </summary>
        /// <remarks>
        /// <para><b><see cref="LastDiagnostic"/> promised this and only half-delivered it.</b>
        /// <c>DocxRenderContext.RenderToPdf</c> cleared the slot on entry, so the legacy renderer
        /// honoured the contract — and the Builder path (<c>UseLegacyRenderer = false</c>) never
        /// cleared it at all, nor did the LibreOffice and Word engines. A caller converting a clean
        /// document on that path was handed the previous conversion's message and had no way to know
        /// it was stale.</para>
        /// <para>Here rather than in each renderer, because this is the one prologue every engine
        /// and both renderers pass through. <c>DocxRenderContext</c> keeps its own clear: it is
        /// reachable directly within the assembly, and a slot cleared twice costs nothing while a
        /// slot cleared nowhere is this defect.</para>
        /// </remarks>
        private static void BeginConversion() => Rendering.DocxDiagnostics.Clear();

        /// <summary>
        /// The options a caller's mistake turns into a degenerate document: the page sides and the
        /// default font size positive and finite, the margins non-negative and finite. The legacy
        /// renderer writes the page size into <c>/MediaBox</c> itself, so Core's <c>AddPage</c>
        /// refusal never sees it, and a NaN margin or size would reach <c>PdfRealFormat</c> as
        /// <c>0</c> and a successful conversion.
        /// </summary>
        private static void Validate(DocxToPdfOptions options)
        {
            Tetitulz.Pdf.Core.Util.NumericArguments.RequirePositive(options.PageWidth, nameof(options), nameof(options.PageWidth));
            Tetitulz.Pdf.Core.Util.NumericArguments.RequirePositive(options.PageHeight, nameof(options), nameof(options.PageHeight));
            Tetitulz.Pdf.Core.Util.NumericArguments.RequireNonNegative(options.MarginTop, nameof(options), nameof(options.MarginTop));
            Tetitulz.Pdf.Core.Util.NumericArguments.RequireNonNegative(options.MarginBottom, nameof(options), nameof(options.MarginBottom));
            Tetitulz.Pdf.Core.Util.NumericArguments.RequireNonNegative(options.MarginLeft, nameof(options), nameof(options.MarginLeft));
            Tetitulz.Pdf.Core.Util.NumericArguments.RequireNonNegative(options.MarginRight, nameof(options), nameof(options.MarginRight));
            Tetitulz.Pdf.Core.Util.NumericArguments.RequirePositive(options.DefaultFontSize, nameof(options), nameof(options.DefaultFontSize));

            // Margins that leave no room on the page lay a document out a character a line and a line a page.
            // A section of the file's own that does so gives way (SectionPageSize.Resolve); a caller's is refused.
            if (options.PageWidth - options.MarginLeft - options.MarginRight < Rendering.SectionPageSize.MinContentPt)
                throw new ArgumentOutOfRangeException(nameof(options),
                    $"MarginLeft and MarginRight leave {options.PageWidth - options.MarginLeft - options.MarginRight:0.##} pt of the "
                    + $"{options.PageWidth:0.##} pt PageWidth; at least {Rendering.SectionPageSize.MinContentPt} pt is needed.");
            if (options.PageHeight - options.MarginTop - options.MarginBottom < Rendering.SectionPageSize.MinContentPt)
                throw new ArgumentOutOfRangeException(nameof(options),
                    $"MarginTop and MarginBottom leave {options.PageHeight - options.MarginTop - options.MarginBottom:0.##} pt of the "
                    + $"{options.PageHeight:0.##} pt PageHeight; at least {Rendering.SectionPageSize.MinContentPt} pt is needed.");
        }

        /// <summary>
        /// Refuses a package whose any part declares an inflated size past
        /// <see cref="Tetitulz.Pdf.Core.Constants.PdfLimits.MaxDocxPartBytes"/>, before the Open XML
        /// SDK reads that part into memory whole. A DOCX is a ZIP: a <c>document.xml</c> deflated
        /// from gigabytes to kilobytes would exhaust the process before a paragraph was laid out, and
        /// the two image readers' own caps never see it. ZIP enforces an entry's declared
        /// length as it is read, so a declaration that lies fails the read rather than this check.
        /// </summary>
        /// <remarks>
        /// <b>And a part that nests its elements past <see cref="Tetitulz.Pdf.Core.Constants.PdfLimits.MaxDocxXmlDepth"/>.</b>
        /// The SDK loads a part's elements by recursion; content controls nested a few hundred deep overflow the
        /// stack, which ends the process. Every part is read once by a streaming
        /// reader that keeps no tree — by its content, not its name, since the package's content types may give
        /// any name an XML type. A part that is not well-formed is left to the SDK, which refuses it itself.
        /// </remarks>
        internal static void RefuseOversizedParts(Stream seekable)
        {
            long cap = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxDocxPartBytes;
            int depthCap = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxDocxXmlDepth;
            using (var zip = new System.IO.Compression.ZipArchive(seekable, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
            {
                foreach (var entry in zip.Entries)
                {
                    if (entry.Length > cap)
                        throw new DocxToPdfException(
                            $"The package part '{entry.FullName}' declares {entry.Length:N0} inflated bytes, past the " +
                            $"{cap:N0}-byte cap on one part of a DOCX (PdfLimits.MaxDocxPartBytes); the document was not converted.");
                }
                foreach (var entry in zip.Entries)
                {
                    int depth = XmlDepth(entry, depthCap);
                    if (depth > depthCap)
                        throw new DocxToPdfException(
                            $"The package part '{entry.FullName}' nests its elements more than {depthCap} deep " +
                            "(PdfLimits.MaxDocxXmlDepth); the document was not converted.");
                }
            }
            seekable.Position = 0;
        }

        /// <summary>
        /// How deep <paramref name="entry"/>'s elements nest, the root at 1, read until the depth passes
        /// <paramref name="cap"/>; for a part that is not XML (an image fails at its first byte) or stops being
        /// well-formed, how deep it got first. The reader detects the encoding as the SDK does, so a part in
        /// UTF-16 is read too.
        /// </summary>
        private static int XmlDepth(System.IO.Compression.ZipArchiveEntry entry, int cap)
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true,
            };
            int deepest = 0;
            try
            {
                using (var stream = entry.Open())
                using (var reader = System.Xml.XmlReader.Create(stream, settings))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;
                        if (reader.Depth + 1 > deepest) deepest = reader.Depth + 1;
                        if (deepest > cap) return deepest;
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
                // Not XML, or not well-formed past this point: the SDK stops at the same point, no deeper.
            }
            return deepest;
        }

        private static void ConvertBuiltIn(Stream input, Stream output, DocxToPdfOptions options)
        {
            BeginConversion();
            Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
            try
            {
                RefuseOversizedParts(seekable);
                using (var wordDoc = WordprocessingDocument.Open(seekable, false))
                {
                    if (options.UseLegacyRenderer)
                    {
                        // The legacy renderer resolves mc:AlternateContent itself, after the clear
                        // its RenderToPdf makes, so the resolution's report survives it.
                        var context = new DocxRenderContext(wordDoc, options);
                        context.RenderToPdf(output);
                    }
                    else
                    {
                        // Every mc:AlternateContent replaced by its choice (Rendering.MarkupCompatibility):
                        // Word writes each wps shape and text box inside one, and the run held nothing
                        // the mapper could draw.
                        Rendering.MarkupCompatibility.ResolveAlternateContent(wordDoc);

                        // Map OOXML to the centralized Builder document model and render via
                        // the shared PDF exporter.
                        var doc = DocxToBuilderMapper.Map(wordDoc, options);
                        doc.SaveAsPdf(output, new Tetitulz.Pdf.Builder.Export.PdfExportOptions
                        {
                            MaxPages = options.MaxPages,
                            // The page limit's report reaches this converter's own channel; the Builder's
                            // other diagnostics stay on BuilderDiagnostics.OnEvent, as they always have.
                            OnDiagnostic = d =>
                            {
                                if (d.Kind == Tetitulz.Pdf.Builder.Export.BuilderDiagnosticKind.PageLimitReached)
                                    Rendering.DocxDiagnostics.Report("DocxToPdfConverter.MaxPages", d.Detail);
                            },
                        });
                    }
                }
            }
            catch (Exception ex) when (!(ex is DocxToPdfException))
            {
                throw new DocxToPdfException("Failed to convert DOCX to PDF.", ex);
            }
            finally
            {
                // The copy SeekableStream made of a non-seekable input is ours; the caller's own
                // stream is not.
                if (ownsSeekable) seekable.Dispose();
            }
        }
    }
}
