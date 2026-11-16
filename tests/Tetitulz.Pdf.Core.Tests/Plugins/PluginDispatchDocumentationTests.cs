// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Core.Tests.Plugins;

/// <summary>
/// <c>PdfPluginHost</c>'s remarks carry a table headed "Which plugin types the library dispatches
/// automatically". Its first row used to be <c>IPdfProcessingHook</c> → "Before/after every
/// <c>PdfOperations</c> write operation", and <b>nothing in the library has ever called
/// <c>InvokeBeforeProcess</c> or <c>InvokeAfterProcess</c></b> — <c>PdfOperations</c>' own source
/// says as much four lines into the file, so the two documents contradicted each other and the
/// wrong one was the document a plugin author reads.
///
/// <para>The failure it enabled is silent by construction: a compliance plugin implementing
/// <c>OnBeforeProcess</c> to strip embedded JavaScript would register, report as registered, and
/// never once be called, with every document written unsanitized and nothing thrown or reported.
/// Its own unit tests — which call <c>host.InvokeBeforeProcess</c> directly — would pass, because
/// they exercise the host method rather than a dispatch that exists.</para>
///
/// <para>This guard is the enforcement, and it is <b>self-cleaning in both directions</b>: an
/// interface may sit in the auto-dispatch table if and only if something outside
/// <c>PdfPluginHost.cs</c> actually invokes it. Wiring the processing hooks into the
/// Stream-in/Stream-out operations later lets the row go back with no change here; re-adding the
/// row without the wiring fails.</para>
/// </summary>
public class PluginDispatchDocumentationTests
{
    private const string HostSource = "src/tetitulz.pdf/Tetitulz.Pdf.Core/Plugins/PdfPluginHost.cs";

    /// <summary>The three hook contracts, each with the single host method that dispatches it.</summary>
    private static readonly (string Interface, string[] InvokeMethods)[] Hooks =
    {
        ("IPdfProcessingHook",  new[] { "InvokeBeforeProcess", "InvokeAfterProcess" }),
        ("ITextExtractionHook", new[] { "InvokeTextExtracted" }),
        ("IRenderHook",         new[] { "InvokePageRendered" }),
    };

    [RepoSourceFact]
    public void AHookIsInTheAutoDispatchTableIfAndOnlyIfSomethingActuallyDispatchesIt()
    {
        string table = AutoDispatchTable();
        var failures = new List<string>();

        foreach (var (iface, invokeMethods) in Hooks)
        {
            bool listedAsAutoDispatched = table.Contains(iface, StringComparison.Ordinal);
            bool hasDispatchSite = invokeMethods.Any(HasCallSiteOutsideTheHost);

            if (listedAsAutoDispatched && !hasDispatchSite)
                failures.Add(
                    $"{iface} is listed in PdfPluginHost's auto-dispatch table, but nothing in " +
                    $"src/ outside PdfPluginHost.cs calls {string.Join(" or ", invokeMethods)}. " +
                    "Either wire the dispatch or move the row into the discovery-contracts " +
                    "paragraph — the table is a promise to plugin authors.");

            if (!listedAsAutoDispatched && hasDispatchSite)
                failures.Add(
                    $"{iface} IS dispatched by src/, but it is missing from PdfPluginHost's " +
                    "auto-dispatch table, so a plugin author has no way to learn that " +
                    "implementing it does something.");
        }

        Assert.Empty(failures);
    }

    [RepoSourceFact]
    public void TheProcessingHookInterfaceSaysThatTheLibraryDoesNotCallIt()
    {
        // The negative fact is not observable at runtime — an unwired hook is silent — so it has
        // to be stated where the implementer will read it.
        string source = RepoSource.ReadText(
            "src/tetitulz.pdf/Tetitulz.Pdf.Core/Plugins/IPdfProcessingHooks.cs");
        int declaration = source.IndexOf("interface IPdfProcessingHook", StringComparison.Ordinal);
        Assert.True(declaration > 0, "IPdfProcessingHook declaration not found.");

        string docComment = source.Substring(0, declaration);
        Assert.Contains("never calls this", docComment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The body of the <c>&lt;list type="table"&gt;</c> that follows the "dispatches
    /// automatically" heading. Failing to find it fails the test rather than passing vacuously —
    /// a guard that silently matches nothing is worse than no guard.
    /// </summary>
    private static string AutoDispatchTable()
    {
        string source = RepoSource.ReadText(HostSource);

        int heading = source.IndexOf("dispatches automatically", StringComparison.Ordinal);
        Assert.True(heading > 0,
            "PdfPluginHost's 'Which plugin types the library dispatches automatically' heading " +
            "was not found; this guard can no longer locate the table it checks.");

        int start = source.IndexOf("<list type=\"table\">", heading, StringComparison.Ordinal);
        int end = source.IndexOf("</list>", heading, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "The auto-dispatch table body was not found.");

        string table = source.Substring(start, end - start);
        Assert.Contains("IRendererPlugin", table, StringComparison.Ordinal);   // sanity: real rows
        return table;
    }

    private static bool HasCallSiteOutsideTheHost(string invokeMethod) =>
        _srcOutsideTheHost.Value.Any(
            text => text.Contains(invokeMethod + "(", StringComparison.Ordinal));

    /// <summary>
    /// Every <c>src/</c> source except <c>PdfPluginHost.cs</c> itself, read once. The scan is
    /// otherwise re-run per interface and the whole tree is read four times over.
    /// </summary>
    private static readonly Lazy<string[]> _srcOutsideTheHost = new(() =>
        Directory.EnumerateFiles(Path.Combine(RepoSource.Root!, "src"), "*.cs",
                SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && Path.GetFileName(f) != "PdfPluginHost.cs")
            .Select(File.ReadAllText)
            .ToArray());
}
