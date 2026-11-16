// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Base.Scripting
{
    /// <summary>
    /// The JavaScript API's <c>event.type</c> and <c>event.name</c> for an action's trigger — the
    /// reference's event type/name combinations (<c>Field/Keystroke</c>, <c>Doc/Open</c>,
    /// <c>Page/Open</c>, <c>Field/Mouse Up</c>).
    /// </summary>
    internal static class PdfJsEventNames
    {
        /// <summary>The type and name of the event <paramref name="trigger"/> fires from <paramref name="source"/>.</summary>
        internal static (string Type, string Name) For(PdfActionSource source, string trigger)
        {
            switch (trigger)
            {
                case "WillClose": case "WillSave": case "DidSave": case "WillPrint": case "DidPrint":
                    return ("Doc", trigger);
                case "PageOpen":  return ("Page", "Open");
                case "PageClose": return ("Page", "Close");
                case "Keystroke": case "Format": case "Validate": case "Calculate": case "Focus": case "Blur":
                    return ("Field", trigger);
                case "MouseEnter": return ("Field", "Mouse Enter");
                case "MouseExit":  return ("Field", "Mouse Exit");
                case "MouseDown":  return ("Field", "Mouse Down");
                case "MouseUp":    return ("Field", "Mouse Up");
            }
            // The open action and the document-level scripts run when the document opens.
            return source == PdfActionSource.Page ? ("Page", "Open") : ("Doc", "Open");
        }
    }
}
