// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Windows.Forms;

namespace Tetitulz.Pdf.CompareViewer;

/// <summary>
/// Adobe Acrobat Reader's own viewer — the <c>AcroPDF.PDF</c> ActiveX control Reader registers for
/// browsers and Office — hosted by its CLSID, so the build needs no generated interop assembly and
/// no Reader on the build machine. The control's methods are reached late-bound through
/// <see cref="AxHost.GetOcx"/>. A 64-bit process needs 64-bit Reader: a 32-bit Reader registers the
/// control only in the WOW6432Node view, where <see cref="IsRegistered"/> does not see it.
/// </summary>
internal sealed class AcrobatViewer : AxHost
{
    private const string ProgId = "AcroPDF.PDF";
    private const string Clsid  = "CA8A9780-280D-11CF-A24D-444553540000";

    public AcrobatViewer() : base(Clsid) { }

    /// <summary>Whether the control is registered for this process's bitness.</summary>
    internal static bool IsRegistered => Type.GetTypeFromProgID(ProgId) != null;

    private dynamic Ocx => GetOcx() ?? throw new InvalidOperationException("The Acrobat Reader control has not been created.");

    /// <summary>
    /// Shows <paramref name="path"/> as one continuous column fitted to the pane's width, the way the
    /// left pane lays its pages out. <c>LoadFile</c>'s return value is not read: Reader DC answers
    /// <c>false</c> for files it opens.
    /// </summary>
    public void Open(string path)
    {
        Ocx.LoadFile(path);
        Ocx.setShowToolbar(false);
        Ocx.setPageMode("none");
        Ocx.setLayoutMode("OneColumn");
        Ocx.setView("FitH");
    }

    /// <summary>Scrolls to the zero-based <paramref name="pageIndex"/>.</summary>
    public void GoToPage(int pageIndex) => Ocx.setCurrentPage(pageIndex + 1);
}
