// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.WpfViewer.Shell;

/// <summary>
/// The names Windows knows the viewer by: its file type, its entry in Default apps, and its preview
/// handler. The installer (<c>apps/installers/Viewer.wxs</c>) writes the same values; a test holds the
/// two to each other.
/// </summary>
internal static class ShellIdentity
{
    /// <summary>The name in Default apps and in Open with; also the <c>RegisteredApplications</c> value name.</summary>
    public const string ApplicationName = "Tetitulz PDF Viewer";

    public const string ApplicationDescription = "Read, fill, mark up, sign and convert PDF files.";

    /// <summary>The file type a PDF opened with the viewer has.</summary>
    public const string ProgId = "Tetitulz.PdfViewer.Document";

    public const string FileTypeName = "PDF Document";

    /// <summary>Under HKLM or HKCU: where the viewer's capabilities are described.</summary>
    public const string CapabilitiesKey = @"Software\Tetitulz\PDF Viewer\Capabilities";

    /// <summary>The preview handler's class: an out-of-process server, the viewer's own program run with <c>-Embedding</c>.</summary>
    public const string PreviewHandlerClsid = "4CFF0BCE-5B8E-48CD-8BEE-4B85F6C0B932";

    public const string PreviewHandlerName = "Tetitulz PDF preview";

    /// <summary>The shell's key for a preview handler under a file type's <c>shellex</c> (IPreviewHandler's IID).</summary>
    public const string PreviewHandlerShellEx = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
}
