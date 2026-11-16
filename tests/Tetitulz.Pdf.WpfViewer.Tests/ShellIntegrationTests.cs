// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;
using Microsoft.Win32;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.WinForms.PdfViewer;
using Tetitulz.Pdf.WpfViewer.Shell;

namespace Tetitulz.Pdf.WpfViewer.Tests;

/// <summary>
/// The viewer in Windows' shell: the registration that makes it a PDF app and Explorer's preview
/// handler, the Settings page that offers it as the default, the preview itself (read-only, the
/// pane's colours, a message where pages cannot be shown), and the installer writing the same names.
/// </summary>
public sealed class ShellIntegrationTests
{
    // ── Registration ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registration writes the file type with its open command and preview handler, the Open with
    /// entry, the capabilities and their Default apps entry, and the handler's local server — and
    /// unregistering removes exactly those, leaving another program's Open with entry.
    /// </summary>
    [Fact]
    public void Registration_WritesTheAssociationAndThePreviewHandler_AndUnregisteringRemovesOnlyThem()
    {
        string scratch = @"Software\Tetitulz\Tests\" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(scratch);
        try
        {
            using (var other = root.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
                other.SetValue("Another.App.pdf", Array.Empty<byte>(), RegistryValueKind.None);
            Assert.False(ShellRegistration.IsRegistered(root));

            // A path with spaces, as Program Files is: every command and server path must quote it.
            string exe = Path.Combine(Path.GetTempPath(), "Tetitulz Tests", "PDF Viewer", "TetitulzPdfViewer.exe");
            ShellRegistration.Register(root, exe);

            Assert.True(ShellRegistration.IsRegistered(root));
            Assert.Equal("\"" + exe + "\" \"%1\"", Read(root, @"Software\Classes\Tetitulz.PdfViewer.Document\shell\open\command"));
            Assert.Equal("\"" + exe + "\",0", Read(root, @"Software\Classes\Tetitulz.PdfViewer.Document\DefaultIcon"));
            Assert.Equal("{4CFF0BCE-5B8E-48CD-8BEE-4B85F6C0B932}",
                Read(root, @"Software\Classes\Tetitulz.PdfViewer.Document\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}"));
            Assert.Equal("\"" + exe + "\"", Read(root, @"Software\Classes\CLSID\{4CFF0BCE-5B8E-48CD-8BEE-4B85F6C0B932}\LocalServer32"));
            Assert.Equal("Tetitulz.PdfViewer.Document", Read(root, @"Software\Tetitulz\PDF Viewer\Capabilities\FileAssociations", ".pdf"));
            Assert.Equal(@"Software\Tetitulz\PDF Viewer\Capabilities", Read(root, @"Software\RegisteredApplications", "Tetitulz PDF Viewer"));
            using (var withProgIds = root.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids")!)
                Assert.Equal(new[] { "Another.App.pdf", "Tetitulz.PdfViewer.Document" }, withProgIds.GetValueNames().OrderBy(n => n).ToArray());
            Assert.Null(root.OpenSubKey($@"Software\Classes\.pdf\shellex"));        // .pdf's own handler left alone

            ShellRegistration.Unregister(root);
            Assert.False(ShellRegistration.IsRegistered(root));
            Assert.Null(root.OpenSubKey(@"Software\Classes\Tetitulz.PdfViewer.Document"));
            Assert.Null(root.OpenSubKey(@"Software\Classes\CLSID\{4CFF0BCE-5B8E-48CD-8BEE-4B85F6C0B932}"));
            Assert.Null(root.OpenSubKey(@"Software\Tetitulz\PDF Viewer\Capabilities"));
            using (var withProgIds = root.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids")!)
                Assert.Equal(new[] { "Another.App.pdf" }, withProgIds.GetValueNames());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// The offer to become the default comes when the installer asked for it, once a user, and not
    /// when the user's choice for .pdf is the viewer already.
    /// </summary>
    [Fact]
    public void TheOfferToBecomeDefault_ComesWhenTheInstallerAskedForIt_OnceAUser_AndNotWhenItIsDefault()
    {
        string scratch = @"Software\Tetitulz\Tests\" + Guid.NewGuid().ToString("N");
        using var machine = Registry.CurrentUser.CreateSubKey(scratch + @"\machine");
        using var user = Registry.CurrentUser.CreateSubKey(scratch + @"\user");
        try
        {
            Assert.False(ShellRegistration.ShouldOfferDefault(machine, user));            // the installer did not ask

            using (var k = machine.CreateSubKey(@"Software\Tetitulz\PDF Viewer")) k.SetValue("OfferToBecomeDefault", 1, RegistryValueKind.DWord);
            Assert.True(ShellRegistration.ShouldOfferDefault(machine, user));

            using (var choice = user.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice"))
            {
                choice.SetValue("ProgId", "Tetitulz.PdfViewer.Document");
                Assert.False(ShellRegistration.ShouldOfferDefault(machine, user));        // already the default
                choice.SetValue("ProgId", "MSEdgePDF");
            }
            Assert.True(ShellRegistration.ShouldOfferDefault(machine, user));            // another app is

            ShellRegistration.MarkDefaultOffered(user);
            Assert.False(ShellRegistration.ShouldOfferDefault(machine, user));            // asked once
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void DefaultApps_OpensAtTheViewersOwnEntry_ForAMachineOrAUserRegistration()
    {
        Assert.Equal("ms-settings:defaultapps?registeredAppMachine=Tetitulz%20PDF%20Viewer", ShellRegistration.DefaultAppsUri(machine: true));
        Assert.Equal("ms-settings:defaultapps?registeredAppUser=Tetitulz%20PDF%20Viewer", ShellRegistration.DefaultAppsUri(machine: false));
    }

    [Theory]
    [InlineData("-Embedding", true)]
    [InlineData("/Embedding", true)]
    [InlineData("-embedding", true)]
    [InlineData(@"C:\docs\Embedding.pdf", false)]
    public void TheProgramServesPreviews_OnlyWhenCOMStartsIt(string arg, bool serves)
        => Assert.Equal(serves, PreviewServer.IsEmbeddingStart(new[] { arg }));

    /// <summary>
    /// The installer writes the names the program does — the file type, the handler's class, the
    /// Default apps entry and the capabilities' key; a change to one side alone breaks the other.
    /// </summary>
    [Fact]
    public void TheInstaller_WritesTheSameNamesAsTheProgram()
    {
        string wxs = File.ReadAllText(Path.Combine(RepositoryRoot(), "apps", "installers", "Viewer.wxs"));
        Assert.Contains(ShellIdentity.ProgId, wxs, StringComparison.Ordinal);
        Assert.Contains(ShellIdentity.PreviewHandlerClsid, wxs, StringComparison.Ordinal);
        Assert.Contains(ShellIdentity.ApplicationName, wxs, StringComparison.Ordinal);
        Assert.Contains(ShellIdentity.CapabilitiesKey, wxs, StringComparison.Ordinal);
        Assert.Contains(ShellIdentity.PreviewHandlerShellEx, wxs, StringComparison.OrdinalIgnoreCase);
    }

    // ── The preview ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void APreview_ShowsTheDocument_ReadOnly_FittedToThePane()
    {
        RunSta(() =>
        {
            using var pane = new Form { ClientSize = new Size(400, 600) };
            var handler = new PdfPreviewHandler();
            ((IInitializeWithStream)handler).Initialize(new BytesStream(MinimalPdfBuilder.CreateMultiPagePdf(3)), 0);
            var rect = new NativeRect(0, 0, 400, 600);
            handler.SetWindow(pane.Handle, ref rect);
            handler.DoPreview();

            PdfViewerControl viewer = handler.Viewer!;
            Assert.Equal(3, viewer.PageCount);
            Assert.False(viewer.ShowToolbar);
            Assert.False(viewer.ShowSidebar);
            Assert.True(viewer.ShowFloatingControls);
            Assert.Equal(PdfZoomMode.FitWidth, viewer.ZoomMode);
            Assert.Equal(PdfViewerFeatures.None, viewer.Features & (PdfViewerFeatures.OpenFile | PdfViewerFeatures.Print | PdfViewerFeatures.Links));
            Assert.Equal(PdfViewerFeatures.Search, viewer.Features & PdfViewerFeatures.Search);   // potency: reading kept
            handler.GetWindow(out IntPtr window);
            Assert.NotEqual(IntPtr.Zero, window);
            Assert.NotEqual(pane.Handle, window);                                  // its own window, inside the pane

            var smaller = new NativeRect(0, 0, 300, 200);
            handler.SetRect(ref smaller);
            Assert.Equal(new Size(300, 200), viewer.Parent!.Size);

            handler.Unload();
            Assert.Null(handler.Viewer);
            Assert.False(handler.IsShowing);
            Assert.True(viewer.IsDisposed);
        });
    }

    [Theory]
    [InlineData(0x202020u, PdfViewerTheme.Dark)]
    [InlineData(0xFFFFFFu, PdfViewerTheme.Light)]
    [InlineData(0xF3F3F3u, PdfViewerTheme.Light)]
    public void APreview_TakesThePanesColours(uint colorref, PdfViewerTheme expected)
    {
        RunSta(() =>
        {
            using var pane = new Form();
            var handler = new PdfPreviewHandler();
            ((IInitializeWithStream)handler).Initialize(new BytesStream(MinimalPdfBuilder.CreateMinimalPdf()), 0);
            handler.SetBackgroundColor(colorref);
            var rect = new NativeRect(0, 0, 300, 300);
            handler.SetWindow(pane.Handle, ref rect);
            handler.DoPreview();
            Assert.Equal(expected, handler.Viewer!.Theme);
            handler.SetBackgroundColor(expected == PdfViewerTheme.Dark ? 0xFFFFFFu : 0x202020u);   // the pane changes
            Assert.NotEqual(expected, handler.Viewer!.Theme);
            handler.Unload();
        });
    }

    [Fact]
    public void APasswordProtectedPdf_IsDescribed_NotShown()
    {
        RunSta(() =>
        {
            using var pane = new Form();
            byte[] encrypted;
            using (var i = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf(), writable: false))
            using (var o = new MemoryStream())
            {
                PdfEncryptor.Encrypt(i, o, new EncryptionParameters { UserPassword = "secret", OwnerPassword = "owner", Algorithm = EncryptionAlgorithm.AES256 });
                encrypted = o.ToArray();
            }
            var handler = Previewed(pane, encrypted);
            Assert.Null(handler.Viewer);
            Assert.Contains("password", handler.Message, StringComparison.Ordinal);
            handler.Unload();
        });
    }

    [Fact]
    public void AFileThatIsNoPdf_IsDescribed_NotShown()
    {
        RunSta(() =>
        {
            using var pane = new Form();
            var handler = Previewed(pane, "not a PDF at all"u8.ToArray());
            Assert.Null(handler.Viewer);
            Assert.StartsWith("This PDF cannot be previewed", handler.Message, StringComparison.Ordinal);

            var control = Previewed(pane, MinimalPdfBuilder.CreateMinimalPdf());          // the control: a PDF shows
            Assert.NotNull(control.Viewer);
            Assert.Null(control.Message);
            handler.Unload();
            control.Unload();
        });
    }

    /// <summary>A file over the ceiling is described from its size alone; none of it is read.</summary>
    [Fact]
    public void AnOversizedFile_IsRefusedFromItsSize_UnRead()
    {
        RunSta(() =>
        {
            using var pane = new Form();
            var stream = new BytesStream(MinimalPdfBuilder.CreateMinimalPdf()) { ReportedSize = PdfPreviewHandler.MaxPreviewBytes + 1 };
            var handler = new PdfPreviewHandler();
            ((IInitializeWithStream)handler).Initialize(stream, 0);
            var rect = new NativeRect(0, 0, 300, 300);
            handler.SetWindow(pane.Handle, ref rect);
            handler.DoPreview();
            Assert.Equal(0, stream.Reads);
            Assert.Contains("too large", handler.Message, StringComparison.Ordinal);
            handler.Unload();
        });
    }

    [Fact]
    public void KeysThePreviewDoesNotUse_GoBackToTheFrame()
    {
        var handler = new PdfPreviewHandler();
        var msg = new NativeMsg();
        Assert.Equal(1, handler.TranslateAccelerator(ref msg));                     // S_FALSE: not handled, no frame
        var frame = new RecordingFrame();
        handler.SetSite(frame);
        Assert.Equal(0, handler.TranslateAccelerator(ref msg));                     // the frame's answer
        Assert.Equal(1, frame.Calls);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────

    private static PdfPreviewHandler Previewed(Form pane, byte[] bytes)
    {
        var handler = new PdfPreviewHandler();
        ((IInitializeWithStream)handler).Initialize(new BytesStream(bytes), 0);
        var rect = new NativeRect(0, 0, 300, 300);
        handler.SetWindow(pane.Handle, ref rect);
        handler.DoPreview();
        return handler;
    }

    private static string? Read(RegistryKey root, string key, string name = "")
    {
        using var k = root.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "apps", "installers", "Viewer.wxs"))) return d.FullName;
        throw new DirectoryNotFoundException("No apps\\installers\\Viewer.wxs above " + AppContext.BaseDirectory);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "the STA test did not finish");
        if (failure != null) throw new Exception("STA test failed: " + failure.Message, failure);
    }

    /// <summary>The frame Explorer is to a preview handler, counting the keys handed back to it.</summary>
    private sealed class RecordingFrame : IPreviewHandlerFrame
    {
        public int Calls;
        public void GetWindowContext(IntPtr info) { }
        public int TranslateAccelerator(ref NativeMsg msg) { Calls++; return 0; }
    }

    /// <summary>A read-only stream over bytes, as Explorer hands a preview handler.</summary>
    private sealed class BytesStream : IStream
    {
        private readonly byte[] _data;
        private long _position;

        public BytesStream(byte[] data) => _data = data;

        /// <summary>The size <c>Stat</c> reports, the data's own unless a test says otherwise.</summary>
        public long? ReportedSize { get; init; }

        public int Reads { get; private set; }

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            Reads++;
            int n = (int)Math.Min(cb, _data.Length - _position);
            Array.Copy(_data, _position, pv, 0, n);
            _position += n;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, n);
        }

        public void Stat(out STATSTG stat, int flag) => stat = new STATSTG { cbSize = ReportedSize ?? _data.Length, type = 2 };
        public void Seek(long offset, int origin, IntPtr newPosition) => throw new NotSupportedException();
        public void Write(byte[] pv, int cb, IntPtr written) => throw new NotSupportedException();
        public void SetSize(long size) => throw new NotSupportedException();
        public void CopyTo(IStream target, long cb, IntPtr read, IntPtr written) => throw new NotSupportedException();
        public void Commit(int flags) { }
        public void Revert() { }
        public void LockRegion(long offset, long cb, int type) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long cb, int type) => throw new NotSupportedException();
        public void Clone(out IStream clone) => throw new NotSupportedException();
    }
}
