// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace Tetitulz.Pdf.WpfViewer.Shell;

// The shell interfaces a preview handler implements, each IID and method order as the Windows SDK
// declares it (10.0.26100.0): IPreviewHandler, IPreviewHandlerVisuals and IPreviewHandlerFrame in
// ShObjIdl_core.h, IInitializeWithStream in propsys.h, IObjectWithSite in ocidl.h, IOleWindow in
// oleidl.h, IClassFactory in Unknwn.h. The proxies Windows registers for them carry the calls across
// processes, so the server here needs none of its own.

[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    public int Left, Top, Right, Bottom;

    public NativeRect(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }

    public System.Drawing.Rectangle ToRectangle() => System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeMsg
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int PointX, PointY;
}

[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandler
{
    void SetWindow(IntPtr hwnd, ref NativeRect rect);
    void SetRect(ref NativeRect rect);
    void DoPreview();
    void Unload();
    void SetFocus();
    void QueryFocus(out IntPtr hwnd);
    [PreserveSig] int TranslateAccelerator(ref NativeMsg msg);
}

[ComImport, Guid("196bf9a5-b346-4ef0-aa1e-5dcdb76768b1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerVisuals
{
    void SetBackgroundColor(uint colorref);
    void SetFont(IntPtr logfont);
    void SetTextColor(uint colorref);
}

[ComImport, Guid("fec87aaf-35f9-447a-adb7-20234491401a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerFrame
{
    void GetWindowContext(IntPtr info);
    [PreserveSig] int TranslateAccelerator(ref NativeMsg msg);
}

[ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    void Initialize(IStream stream, uint mode);
}

[ComImport, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectWithSite
{
    void SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
    void GetSite(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? site);
}

[ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleWindow
{
    void GetWindow(out IntPtr hwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr instance);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
}

/// <summary>The few platform calls the preview server makes — this is an application, not the library.</summary>
internal static class PreviewNative
{
    public const uint ClsctxLocalServer = 0x4;
    public const uint RegclsMultipleUse = 0x1;
    public const int SFalse = 1;
    public const int ClassENoAggregation = unchecked((int)0x80040110);

    [DllImport("ole32.dll")]
    public static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object unknown,
        uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    public static extern int CoRevokeClassObject(uint cookie);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();
}
