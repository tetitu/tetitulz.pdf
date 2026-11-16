// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>What <see cref="PdfInitialView.SetInitialView"/> reports about the preferences it was given.</summary>
public class InitialViewWriteReportTests
{
    /// <summary>
    /// A preferences object whose every member is null writes nothing — and says so, because the
    /// caller asked for something and got a document without it. With one member set, the key is
    /// written and there is no such warning: the control.
    /// </summary>
    [Fact]
    public void PreferencesWithEveryMemberNullAreReportedAsNotWritten()
    {
        var empty = Set(new ViewerPreferenceSettings());
        Assert.DoesNotContain("/ViewerPreferences", empty.KeysWritten);
        Assert.Contains(empty.Warnings, w => w.Contains("every member was null", StringComparison.Ordinal));

        var one = Set(new ViewerPreferenceSettings { HideToolbar = true });
        Assert.Contains("/ViewerPreferences", one.KeysWritten);
        Assert.DoesNotContain(one.Warnings, w => w.Contains("every member was null", StringComparison.Ordinal));
    }

    private static InitialViewResult Set(ViewerPreferenceSettings prefs)
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        using var output = new MemoryStream();
        return PdfInitialView.SetInitialView(new MemoryStream(pdf), output,
            new InitialViewSettings { ViewerPreferences = prefs });
    }
}
