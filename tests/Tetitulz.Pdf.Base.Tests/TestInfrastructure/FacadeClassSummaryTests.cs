// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// <c>PdfOperations</c>' class summary is written on one partial only.
/// </summary>
/// <remarks>
/// The compiler joins the summaries of every part of a partial type. The 39-line summary had been
/// copied onto 45 of the facade's partials, so the generated XML documentation carried it 46 times
/// in one member, about 120 KB that IntelliSense showed for the type. Every copy repeated a false
/// claim about attachments in encrypted documents, and none could be corrected without the other 45.
/// </remarks>
public class FacadeClassSummaryTests
{
    private static readonly Regex SummaryAboveClass = new(
        @"///[^\n]*\n\s*public static partial class PdfOperations\b", RegexOptions.Compiled);

    [RepoSourcesFact]
    public void OnlyOnePartialCarriesTheClassSummary()
    {
        string baseDir = Path.Combine(RepoSources.SrcRoot!, "tetitulz.pdf", "Tetitulz.Pdf.Base");
        var partials = Directory.GetFiles(baseDir, "PdfOperations*.cs", SearchOption.TopDirectoryOnly);
        Assert.True(partials.Length > 40, $"only {partials.Length} PdfOperations partials found");

        var documented = partials
            .Where(p => SummaryAboveClass.IsMatch(File.ReadAllText(p).Replace("\r\n", "\n")))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(new[] { "PdfOperations.cs" }, documented);
    }
}
