// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Tetitulz.Pdf.WpfViewer.Shell;

/// <summary>
/// The viewer as File Explorer's PDF preview handler: an out-of-process COM server. COM starts the
/// viewer's program with <c>-Embedding</c>; it registers the handler's class factory and serves
/// previews on its UI thread until it has shown none for <see cref="IdleExit"/>.
/// </summary>
/// <remarks>
/// <para><b>Out of process, on purpose.</b> An in-process handler is loaded into Windows' preview
/// host, whose bitness is Explorer's: a 32-bit build could not serve a 64-bit Explorer, and a .NET
/// runtime would have to load into a host that is not ours. A local server works for either build,
/// and a document that brings it down brings down only the viewer's own process.</para>
/// <para>It runs at the user's integrity level, as the viewer does when it opens the same file; Windows'
/// in-process preview host would run it at low integrity.</para>
/// </remarks>
internal sealed class PreviewServer
{
    /// <summary>How long the server lingers with no preview on screen before it exits.</summary>
    internal static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(2);

    private readonly HashSet<PdfPreviewHandler> _showing = new();
    private DateTime _lastActivity = DateTime.UtcNow;
    private uint _cookie;
    private DispatcherTimer? _timer;

    /// <summary>Whether the program was started by COM to serve its classes.</summary>
    public static bool IsEmbeddingStart(string[] args)
        => args.Any(a => string.Equals(a, "-Embedding", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(a, "/Embedding", StringComparison.OrdinalIgnoreCase));

    /// <summary>Registers the class factory and starts the idle clock; <paramref name="exit"/> ends the program.</summary>
    public void Start(Action exit)
    {
        Guid clsid = new(ShellIdentity.PreviewHandlerClsid);
        int hr = PreviewNative.CoRegisterClassObject(ref clsid, new ClassFactory(this),
            PreviewNative.ClsctxLocalServer, PreviewNative.RegclsMultipleUse, out _cookie);
        if (hr < 0) { exit(); return; }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) =>
        {
            if (_showing.Count > 0 || DateTime.UtcNow - _lastActivity < IdleExit) return;
            _timer.Stop();
            PreviewNative.CoRevokeClassObject(_cookie);
            exit();
        };
        _timer.Start();
    }

    /// <summary>A handler showing (or no longer showing) a preview.</summary>
    internal void OnActivity(PdfPreviewHandler handler, bool showing)
    {
        _lastActivity = DateTime.UtcNow;
        if (showing) _showing.Add(handler);
        else _showing.Remove(handler);
    }

    /// <summary>Makes preview handlers for COM.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ClassFactory : IClassFactory
    {
        private readonly PreviewServer _server;

        internal ClassFactory(PreviewServer server) => _server = server;

        public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance)
        {
            instance = IntPtr.Zero;
            if (outer != IntPtr.Zero) return PreviewNative.ClassENoAggregation;
            _server._lastActivity = DateTime.UtcNow;
            var handler = new PdfPreviewHandler(_server.OnActivity);
            IntPtr unknown = Marshal.GetIUnknownForObject(handler);
            try { return Marshal.QueryInterface(unknown, in riid, out instance); }
            finally { Marshal.Release(unknown); }
        }

        public int LockServer(bool lockServer)
        {
            _server._lastActivity = DateTime.UtcNow;
            return 0;
        }
    }
}
