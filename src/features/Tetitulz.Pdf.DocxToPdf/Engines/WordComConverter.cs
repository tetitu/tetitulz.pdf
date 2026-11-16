// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;

namespace Tetitulz.Pdf.DocxToPdf.Engines
{
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
        Justification = "Platform is validated at runtime via EnsureWindows()")]
    internal static class WordComConverter
    {
        // wdFormatPDF = 17
        private const int WdFormatPdf = 17;
        // wdAlertsNone = 0
        private const int WdAlertsNone = 0;

        /// <summary>
        /// <c>msoAutomationSecurityForceDisable</c>: no macro of a file opened programmatically
        /// runs, and no prompt asks. An application started through automation begins at
        /// <c>msoAutomationSecurityLow</c>, which enables every macro — the document's own and those
        /// of a template it names, a remote one included — so the value is set before a file is opened.
        /// </summary>
        internal const int MsoAutomationSecurityForceDisable = 3;

        /// <summary>
        /// The password handed to <c>Documents.Open</c>. A document that has none ignores it; one
        /// that has is refused with an error, where Word left to itself shows a password prompt
        /// that an invisible instance never answers.
        /// </summary>
        private const string NoPassword = "tetitulz-pdf-no-password";

        public static void ConvertFile(string inputPath, string outputPath)
        {
            EnsureWindows();

            string fullInputPath = Path.GetFullPath(inputPath);
            string fullOutputPath = Path.GetFullPath(outputPath);

            if (!File.Exists(fullInputPath))
                throw new DocxToPdfException($"Input file not found: {fullInputPath}");

            string outputDir = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            dynamic wordApp = null;
            dynamic doc = null;
            string holder = LibreOfficeConverter.CreatePrivateDirectory();

            try
            {
                // First, so a document the engine must not see is refused the same way with Word or without it.
                string cleaned = ExternalEngineInput.CopyInto(holder, fullInputPath);

                var wordType = Type.GetTypeFromProgID("Word.Application");
                if (wordType == null)
                {
                    throw new DocxToPdfException(
                        "Microsoft Word is not installed or Word.Application COM class is not registered.");
                }

                wordApp = Activator.CreateInstance(wordType);
                wordApp.Visible = false;
                wordApp.DisplayAlerts = WdAlertsNone;
                HardenForUntrustedInput(wordApp);

                doc = wordApp.Documents.Open(
                    cleaned,
                    ConfirmConversions: false,
                    ReadOnly: true,
                    AddToRecentFiles: false,
                    PasswordDocument: NoPassword,
                    PasswordTemplate: NoPassword,
                    Revert: false,
                    Visible: false,
                    OpenAndRepair: false,
                    NoEncodingDialog: true);
                doc.SaveAs2(fullOutputPath, FileFormat: WdFormatPdf);
            }
            catch (Exception ex) when (!(ex is DocxToPdfException))
            {
                throw new DocxToPdfException("Microsoft Word conversion failed.", ex);
            }
            finally
            {
                if (doc != null)
                {
                    try { doc.Close(false); } catch { }
                    try { Marshal.ReleaseComObject(doc); } catch { }
                }
                if (wordApp != null)
                {
                    try { wordApp.Quit(false); } catch { }
                    try { Marshal.ReleaseComObject(wordApp); } catch { }
                }
                try { Directory.Delete(holder, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Sets a freshly started Word so that a document it opens runs nothing on its own: macros off
        /// (<see cref="MsoAutomationSecurityForceDisable"/>), and the links a document carries — to
        /// files, to network shares, to URLs — not updated when it opens. Word fetches a template, an
        /// image or a subdocument the package names whatever these say;
        /// <see cref="ExternalEngineInput"/> takes those out first.
        /// </summary>
        /// <remarks>
        /// The macro setting is the security boundary and a failure to set it stops the conversion:
        /// a document converted with macros enabled is code run on the host. The link setting is a
        /// reduction, and a Word that refuses it is still used. What this does not bound is time —
        /// Word runs in its own process with no handle this library can end it by, so a document
        /// that keeps Word busy keeps the call.
        /// </remarks>
        internal static void HardenForUntrustedInput(dynamic wordApp)
        {
            wordApp.AutomationSecurity = MsoAutomationSecurityForceDisable;
            if ((int)wordApp.AutomationSecurity != MsoAutomationSecurityForceDisable)
                throw new DocxToPdfException(
                    "Microsoft Word did not accept the setting that disables macros; the document was not opened.");

            try { wordApp.Options.UpdateLinksAtOpen = false; }
            catch (Exception ex) when (ex is COMException || ex is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        }

        public static void ConvertStream(Stream input, Stream output)
        {
            EnsureWindows();

            // The document and its PDF are written inside a directory of their own, as LibreOffice's are.
            string holder = LibreOfficeConverter.CreatePrivateDirectory();
            string tempInput = Path.Combine(holder, "input.docx");
            string tempOutput = Path.Combine(holder, "output.pdf");

            try
            {
                using (var fs = new FileStream(tempInput, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(fs);
                }

                ConvertFile(tempInput, tempOutput);

                using (var fs = new FileStream(tempOutput, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.CopyTo(output);
                }
            }
            catch (Exception ex) when (!(ex is DocxToPdfException))
            {
                throw new DocxToPdfException("Microsoft Word conversion failed.", ex);
            }
            finally
            {
                try { Directory.Delete(holder, recursive: true); } catch { }
            }
        }

        private static void EnsureWindows()
        {
            var platform = Environment.OSVersion.Platform;
            bool isWindows = platform == PlatformID.Win32NT
                || platform == PlatformID.Win32S
                || platform == PlatformID.Win32Windows;

            if (!isWindows)
            {
                throw new DocxToPdfException(
                    "Microsoft Word COM automation is only supported on Windows.");
            }
        }

    }
}
