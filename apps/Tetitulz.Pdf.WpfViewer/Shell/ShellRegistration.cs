// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tetitulz.Pdf.WpfViewer.Shell;

/// <summary>
/// The viewer as Windows' file associations see it: a PDF file type that opens in it, an entry in
/// Default apps, and its preview handler. The installer writes these for the machine; a copy that was
/// not installed writes them for the user when asked to become the default app.
/// </summary>
/// <remarks>
/// <b>No program can make itself the default.</b> Windows keeps the user's choice behind a hash only
/// it writes; what a program may do is register as able to open the type and open Default apps at its
/// own entry, where the user chooses. That is what <see cref="OfferToBecomeDefault"/> does.
/// </remarks>
internal static class ShellRegistration
{
    private const string Classes = @"Software\Classes";
    private const string RegisteredApplications = @"Software\RegisteredApplications";

    /// <summary>
    /// Writes the registration under <paramref name="root"/> — <c>HKEY_CURRENT_USER</c> in use, a scratch
    /// key in a test: the file type and its open command, <c>.pdf</c>'s Open with entry, the
    /// capabilities and their Default apps entry, and the preview handler's class with the file type
    /// pointing at it. <c>.pdf</c>'s own preview handler is left as it is: replacing another program's
    /// is the installer's option, not a side effect.
    /// </summary>
    public static void Register(RegistryKey root, string exePath)
    {
        string open = Quote(exePath) + " \"%1\"";
        using (var type = root.CreateSubKey($@"{Classes}\{ShellIdentity.ProgId}"))
        {
            type.SetValue("", ShellIdentity.FileTypeName);
            type.SetValue("FriendlyTypeName", ShellIdentity.FileTypeName);
            using (var icon = type.CreateSubKey("DefaultIcon")) icon.SetValue("", Quote(exePath) + ",0");
            using (var command = type.CreateSubKey(@"shell\open\command")) command.SetValue("", open);
            using (var preview = type.CreateSubKey($@"shellex\{ShellIdentity.PreviewHandlerShellEx}"))
                preview.SetValue("", "{" + ShellIdentity.PreviewHandlerClsid + "}");
        }
        using (var withProgIds = root.CreateSubKey($@"{Classes}\.pdf\OpenWithProgids"))
            withProgIds.SetValue(ShellIdentity.ProgId, Array.Empty<byte>(), RegistryValueKind.None);

        using (var caps = root.CreateSubKey(ShellIdentity.CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", ShellIdentity.ApplicationName);
            caps.SetValue("ApplicationDescription", ShellIdentity.ApplicationDescription);
            using (var assoc = caps.CreateSubKey("FileAssociations")) assoc.SetValue(".pdf", ShellIdentity.ProgId);
        }
        using (var apps = root.CreateSubKey(RegisteredApplications))
            apps.SetValue(ShellIdentity.ApplicationName, ShellIdentity.CapabilitiesKey);

        // The preview handler: the viewer itself, started by COM with -Embedding (LocalServer32).
        using (var clsid = root.CreateSubKey($@"{Classes}\CLSID\{{{ShellIdentity.PreviewHandlerClsid}}}"))
        {
            clsid.SetValue("", ShellIdentity.PreviewHandlerName);
            clsid.SetValue("DisplayName", ShellIdentity.PreviewHandlerName);
            using (var server = clsid.CreateSubKey("LocalServer32")) server.SetValue("", Quote(exePath));
        }
    }

    /// <summary>Removes what <see cref="Register"/> wrote under <paramref name="root"/>, and nothing else.</summary>
    public static void Unregister(RegistryKey root)
    {
        root.DeleteSubKeyTree($@"{Classes}\{ShellIdentity.ProgId}", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree($@"{Classes}\CLSID\{{{ShellIdentity.PreviewHandlerClsid}}}", throwOnMissingSubKey: false);
        using (var withProgIds = root.OpenSubKey($@"{Classes}\.pdf\OpenWithProgids", writable: true))
            withProgIds?.DeleteValue(ShellIdentity.ProgId, throwOnMissingValue: false);
        using (var apps = root.OpenSubKey(RegisteredApplications, writable: true))
            apps?.DeleteValue(ShellIdentity.ApplicationName, throwOnMissingValue: false);
        root.DeleteSubKeyTree(ShellIdentity.CapabilitiesKey, throwOnMissingSubKey: false);
    }

    /// <summary>Whether <paramref name="root"/> lists the viewer among the registered applications.</summary>
    public static bool IsRegistered(RegistryKey root)
    {
        using var apps = root.OpenSubKey(RegisteredApplications);
        return apps?.GetValue(ShellIdentity.ApplicationName) is string;
    }

    /// <summary>The Settings page that offers the viewer as the default PDF app, for a machine's or a user's registration.</summary>
    public static string DefaultAppsUri(bool machine)
        => "ms-settings:defaultapps?" + (machine ? "registeredAppMachine=" : "registeredAppUser=")
           + Uri.EscapeDataString(ShellIdentity.ApplicationName);

    /// <summary>
    /// Opens Default apps at the viewer's entry, registering it for the user first when the installer
    /// has not registered it for the machine.
    /// </summary>
    public static void OfferToBecomeDefault()
    {
        bool machine = IsRegistered(Registry.LocalMachine);
        if (!machine)
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("The viewer's own path is unknown.");
            Register(Registry.CurrentUser, exe);
            NotifyAssociationsChanged();
        }
        Process.Start(new ProcessStartInfo(DefaultAppsUri(machine)) { UseShellExecute = true });
    }

    private const string ViewerKey = @"Software\Tetitulz\PDF Viewer";
    private const string UserChoiceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice";

    /// <summary>
    /// Whether to offer, on this start, to become the default PDF app: the installer asked for it
    /// (<c>OfferToBecomeDefault</c> under <paramref name="machine"/>), this user has not been asked, and
    /// the user's choice for <c>.pdf</c> is not the viewer already.
    /// </summary>
    public static bool ShouldOfferDefault(RegistryKey machine, RegistryKey user)
    {
        using (var installed = machine.OpenSubKey(ViewerKey))
            if (!(installed?.GetValue("OfferToBecomeDefault") is int asked && asked == 1)) return false;
        using (var mine = user.OpenSubKey(ViewerKey))
            if (mine?.GetValue("DefaultOffered") is int done && done == 1) return false;
        using (var choice = user.OpenSubKey(UserChoiceKey))
            return !string.Equals(choice?.GetValue("ProgId") as string, ShellIdentity.ProgId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records that this user has been asked, so the question comes once.</summary>
    public static void MarkDefaultOffered(RegistryKey user)
    {
        using var mine = user.CreateSubKey(ViewerKey);
        mine.SetValue("DefaultOffered", 1, RegistryValueKind.DWord);
    }

    private static string Quote(string path) => "\"" + path + "\"";

    /// <summary>Tells the shell its file associations changed, so Open with and the icons read them again.</summary>
    private static void NotifyAssociationsChanged() => SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);

    private const int ShcneAssocChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
