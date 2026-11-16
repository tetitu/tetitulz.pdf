// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// Fetches a remote <c>http(s)</c> subresource — an image, a background image, a stylesheet or
    /// an <c>@font-face</c> source — for the <see cref="HtmlRenderEngine.Managed"/> engine.
    /// </summary>
    /// <remarks>
    /// <para><b>The engine performs no network I/O of its own.</b> With no loader set
    /// (<see cref="HtmlToPdfOptions.RemoteResourceLoader"/> is <c>null</c>, the default) every remote
    /// subresource is declined and reported on <see cref="HtmlToPdfOptions.OnDiagnostic"/>. Setting one is the caller's statement that the document's URLs may
    /// be fetched.</para>
    ///
    /// <para><see cref="GuardedRemoteResourceLoader"/> is the implementation to use for HTML you do
    /// not control: it refuses private and reserved addresses, re-checks every redirect, and bounds
    /// each fetch in bytes and time. Implement this interface yourself to add what only the host can
    /// know — an allow-list of hosts, a cache, credentials.</para>
    ///
    /// <para>The engine calls the loader once per distinct URL per conversion, at most
    /// <see cref="HtmlToPdfOptions.MaxRemoteResources"/> times, and never with anything but an
    /// absolute <c>http</c> or <c>https</c> URL.</para>
    /// </remarks>
    public interface IRemoteResourceLoader
    {
        /// <summary>
        /// Fetches <paramref name="url"/>. Returns the body, or <c>null</c> with
        /// <paramref name="failure"/> saying why — the engine puts that reason in the diagnostic it
        /// reports, so a caller can tell a refused URL from an unreachable one. Should not throw; an
        /// exception is caught and reported the same way.
        /// </summary>
        /// <param name="url">An absolute <c>http</c> or <c>https</c> URL taken from the document.</param>
        /// <param name="failure">Why nothing was returned, or <c>null</c>.</param>
        byte[]? Load(Uri url, out string? failure);
    }
}
