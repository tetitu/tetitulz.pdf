// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Marks a plugin that discovery never registers: <see cref="PdfPluginHost.RegisterFromAssembly"/>,
    /// <see cref="PdfPluginHost.RegisterFromLoadedAssemblies"/> and
    /// <see cref="PdfPluginHost.RegisterFromDirectory"/> pass it over, and only a host's own
    /// <see cref="PdfPluginHost.Register"/> enables it.
    /// </summary>
    /// <remarks>
    /// For a plugin that runs what the document brings — its JavaScript — so that referencing the
    /// package, or discovering plugins for another reason, does not expose the host to it.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class PdfPluginExplicitRegistrationAttribute : Attribute
    {
    }
}
