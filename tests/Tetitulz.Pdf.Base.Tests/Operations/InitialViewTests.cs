// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §7.7.2 / §12.2 initial-view authoring — <c>PdfInitialView</c>.
/// </summary>
/// <remarks>
/// <para>All four settings were readable and none writable: <c>PdfViewerPreferences</c> is get-only
/// with an <c>internal</c> constructor, <c>PdfCatalog</c> exposes no <c>/PageMode</c>,
/// <c>/PageLayout</c> or <c>/OpenAction</c> at all, and the one viewer-preference key anything in
/// the tree wrote was <c>/DisplayDocTitle</c> — as a side effect of an accessibility title.</para>
///
/// <para><b>Assertions read the catalog back out of the produced bytes</b>, not the settings object
/// that was passed in: a writer that returned a plausible <c>InitialViewResult</c> and wrote nothing
/// would satisfy anything weaker.</para>
/// </remarks>
public class InitialViewTests
{
    [Fact]
    public void PageModeAndPageLayout_AreWrittenToTheCatalog()
    {
        byte[] result = Write(OnePage(), new InitialViewSettings
        {
            PageMode = "UseOutlines",
            PageLayout = "TwoPageLeft",
        });

        Assert.Equal("UseOutlines", CatalogName(result, "PageMode"));
        Assert.Equal("TwoPageLeft", CatalogName(result, "PageLayout"));
    }

    /// <summary>A null member leaves the document alone — it is not written as a default.</summary>
    [Fact]
    public void ANullMemberWritesNothing()
    {
        byte[] result = Write(OnePage(), new InitialViewSettings { PageMode = "UseThumbs" });

        Assert.Equal("UseThumbs", CatalogName(result, "PageMode"));
        Assert.Null(CatalogName(result, "PageLayout"));
    }

    /// <summary>
    /// <b>An unrecognised name is refused, not written.</b> A viewer that does not know a name value
    /// falls back to its default, so a typo yields a document that silently ignores the setting —
    /// indistinguishable from a viewer that does not honour it.
    /// </summary>
    [Theory]
    [InlineData("UseOutline")]      // singular — the plausible typo
    [InlineData("usethumbs")]       // PDF names are case-sensitive (§7.3.5)
    [InlineData("SinglePage")]      // a valid /PageLayout, which is not a valid /PageMode
    public void AnUnrecognisedPageMode_IsRefused(string pageMode)
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfInitialView.SetInitialView(input, output, new InitialViewSettings { PageMode = pageMode }));

        Assert.Contains("PageMode", ex.Message);
    }

    // ------------------------------------------------------------------
    //  Viewer preferences
    // ------------------------------------------------------------------

    /// <summary>The Hebrew/Arabic case: right-to-left page layout.</summary>
    [Fact]
    public void Direction_R2L_IsWritten()
    {
        byte[] result = Write(OnePage(), new InitialViewSettings
        {
            ViewerPreferences = new ViewerPreferenceSettings { Direction = "R2L" },
        });

        Assert.Equal("R2L", PreferenceName(result, "Direction"));
    }

    [Fact]
    public void BooleanAndIntegerPreferences_AreWritten()
    {
        byte[] result = Write(OnePage(), new InitialViewSettings
        {
            ViewerPreferences = new ViewerPreferenceSettings
            {
                HideToolbar = true,
                FitWindow = false,
                NumCopies = 3,
            },
        });

        Assert.True(PreferenceBool(result, "HideToolbar"));
        Assert.False(PreferenceBool(result, "FitWindow"));
        Assert.Equal(3, PreferenceInt(result, "NumCopies"));
    }

    /// <summary>
    /// <b>Existing preferences are merged, not rebuilt</b> — the same rule <c>/Info</c> had to learn.
    /// §12.2 Table 150 has entries this API does not model, and rebuilding would delete them.
    /// </summary>
    [Fact]
    public void AnExistingPreferenceTheApiDoesNotModel_Survives()
    {
        byte[] result = Write(WithViewerPrefs("/Enforce [/PrintScaling] /HideMenubar true"),
            new InitialViewSettings
            {
                ViewerPreferences = new ViewerPreferenceSettings { Direction = "R2L" },
            });

        Assert.Equal("R2L", PreferenceName(result, "Direction"));
        Assert.True(PreferenceBool(result, "HideMenubar"));
        Assert.NotNull(Preference(result, "Enforce"));
    }

    /// <summary>A non-positive copy count is refused rather than written (§12.2 Table 150).</summary>
    [Fact]
    public void ANonPositiveNumCopies_IsRefused()
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();

        Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfInitialView.SetInitialView(input, output, new InitialViewSettings
            {
                ViewerPreferences = new ViewerPreferenceSettings { NumCopies = 0 },
            }));
    }

    /// <summary>
    /// §12.2 forbids <c>FullScreen</c> for <c>/NonFullScreenPageMode</c> — it is the mode you leave
    /// full screen *for*. A shared name list would have accepted it.
    /// </summary>
    [Fact]
    public void NonFullScreenPageMode_RefusesFullScreen()
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();

        Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfInitialView.SetInitialView(input, output, new InitialViewSettings
            {
                ViewerPreferences = new ViewerPreferenceSettings { NonFullScreenPageMode = "FullScreen" },
            }));
    }

    // ------------------------------------------------------------------
    //  /OpenAction
    // ------------------------------------------------------------------

    /// <summary>
    /// The destination names the page by <b>reference</b>, and unset coordinates are written as
    /// <c>null</c> — §12.3.2.2 makes null "keep the viewer's current value", which is not 0.
    /// </summary>
    [Fact]
    public void OpenAction_IsAnXyzDestinationNamingThePageByReference()
    {
        byte[] result = Write(TwoPages(), new InitialViewSettings { OpenAtPageIndex = 1, OpenAtTop = 700 });

        var dest = CatalogEntry(result, "OpenAction") as PdfArray;
        Assert.NotNull(dest);
        Assert.IsType<PdfReference>(dest![0]);
        Assert.Equal("XYZ", ((PdfName)dest[1]).Value);
        Assert.IsType<PdfNull>(dest[2]);                       // left unset
        Assert.Equal(700d, ((PdfNumber)dest[3]).DoubleValue);   // top
        Assert.IsType<PdfNull>(dest[4]);                       // zoom unset
    }

    /// <summary>…and it points at the page that was asked for, not page 0.</summary>
    [Fact]
    public void OpenAction_PointsAtTheRequestedPage()
    {
        byte[] result = Write(TwoPages(), new InitialViewSettings { OpenAtPageIndex = 1 });

        Assert.Equal(1, PdfOperations.GetInitialView(new MemoryStream(result)).OpenAtPageIndex);
    }

    [Fact]
    public void OpenAtPageIndex_OutOfRange_IsRefused()
    {
        using var input = new MemoryStream(TwoPages());
        using var output = new MemoryStream();

        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            PdfInitialView.SetInitialView(input, output, new InitialViewSettings { OpenAtPageIndex = 5 }));
    }

    /// <summary>
    /// <b>An existing /OpenAction that is an action dictionary is not silently replaced.</b> It can
    /// be a JavaScript or launch entry point the document depends on; "go to page 2" would remove
    /// behaviour the caller did not know was there.
    /// </summary>
    [Fact]
    public void AnExistingOpenAction_ThatIsAnAction_IsRefusedByDefault()
    {
        using var input = new MemoryStream(WithOpenAction("<< /S /JavaScript /JS (app.alert\\(1\\)) >>"));
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfInitialView.SetInitialView(input, output, new InitialViewSettings { OpenAtPageIndex = 0 }));

        Assert.Contains("JavaScript", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>…and is replaced when the caller says so.</summary>
    [Fact]
    public void AnExistingOpenAction_IsReplacedWhenTheCallerOptsIn()
    {
        byte[] result = Write(WithOpenAction("<< /S /JavaScript /JS (app.alert\\(1\\)) >>"),
            new InitialViewSettings { OpenAtPageIndex = 0, ReplaceExistingOpenAction = true });

        Assert.IsType<PdfArray>(CatalogEntry(result, "OpenAction"));
    }

    // ------------------------------------------------------------------
    //  Round trip
    // ------------------------------------------------------------------

    /// <summary>What was written is what comes back.</summary>
    [Fact]
    public void GetInitialView_ReadsBackWhatSetInitialViewWrote()
    {
        byte[] result = Write(OnePage(), new InitialViewSettings
        {
            PageMode = "UseThumbs",
            PageLayout = "OneColumn",
            ViewerPreferences = new ViewerPreferenceSettings { Direction = "R2L", HideToolbar = true },
        });

        var read = PdfOperations.GetInitialView(new MemoryStream(result));

        Assert.Equal("UseThumbs", read.PageMode);
        Assert.Equal("OneColumn", read.PageLayout);
        Assert.Equal("R2L", read.ViewerPreferences.Direction);
        Assert.True(read.ViewerPreferences.HideToolbar);
    }

    /// <summary>Nothing asked for is reported as nothing done, rather than silently.</summary>
    [Fact]
    public void AnEmptySettingsObject_IsReportedAsNothingWritten()
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();
        var result = PdfInitialView.SetInitialView(input, output, new InitialViewSettings());

        Assert.Empty(result.KeysWritten);
        Assert.NotEmpty(result.Warnings);
    }

    /// <summary>The result names the keys the revision actually carries.</summary>
    [Fact]
    public void TheResultNamesWhatWasWritten()
    {
        using var input = new MemoryStream(OnePage());
        using var output = new MemoryStream();
        var result = PdfInitialView.SetInitialView(input, output, new InitialViewSettings
        {
            PageMode = "UseNone",
            ViewerPreferences = new ViewerPreferenceSettings { Direction = "L2R" },
        });

        Assert.Contains("/PageMode", result.KeysWritten);
        Assert.Contains("/ViewerPreferences", result.KeysWritten);
        Assert.DoesNotContain("/PageLayout", result.KeysWritten);
    }

    // ------------------------------------------------------------------

    private static byte[] Write(byte[] pdf, InitialViewSettings settings)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfInitialView.SetInitialView(input, output, settings);
        return output.ToArray();
    }

    private static PdfObject CatalogEntry(byte[] pdf, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return doc.ResolveObject(doc.Catalog.Dictionary[new PdfName(key)]);
    }

    private static string CatalogName(byte[] pdf, string key)
        => (CatalogEntry(pdf, key) as PdfName)?.Value;

    private static PdfObject Preference(byte[] pdf, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var prefs = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.ViewerPreferences]) as PdfDictionary;
        Assert.NotNull(prefs);
        return doc.ResolveObject(prefs![new PdfName(key)]);
    }

    private static string PreferenceName(byte[] pdf, string key) => (Preference(pdf, key) as PdfName)?.Value;
    private static bool? PreferenceBool(byte[] pdf, string key) => (Preference(pdf, key) as PdfBoolean)?.Value;
    private static int? PreferenceInt(byte[] pdf, string key) => (Preference(pdf, key) as PdfNumber)?.IntValue;

    private static byte[] OnePage() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""));

    private static byte[] TwoPages() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>");

    /// <summary>A document whose <c>/ViewerPreferences</c> is an indirect dictionary already.</summary>
    private static byte[] WithViewerPrefs(string entries) => Build(
        "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""),
        "<< " + entries + " >>");

    private static byte[] WithOpenAction(string action) => Build(
        "<< /Type /Catalog /Pages 2 0 R /OpenAction " + action + " >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""));

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
