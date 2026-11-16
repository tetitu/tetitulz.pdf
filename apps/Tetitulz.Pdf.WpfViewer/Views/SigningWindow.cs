// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Tetitulz.Pdf.Plugins.CertStoreSign;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.WpfViewer.Models;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Tetitulz.Pdf.WpfViewer.Views;

/// <summary>
/// What a signature is made with: its parameters (a Windows store certificate's signer, or a PFX
/// file), the picture a visible signature shows, and the signer to dispose of once it has signed.
/// </summary>
public sealed class SigningChoice : IDisposable
{
    public SigningChoice(SignatureParameters parameters, byte[] picture, IDisposable? signer)
    {
        Parameters = parameters;
        Picture = picture;
        _signer = signer;
    }

    private readonly IDisposable? _signer;

    public SignatureParameters Parameters { get; }

    /// <summary>The visible signature's picture: the one chosen, or the signer's name and the date.</summary>
    public byte[] Picture { get; }

    public void Dispose() => _signer?.Dispose();
}

/// <summary>
/// Asks how to sign: a certificate from the Windows store (a smart card's prompts for its PIN when
/// it signs) or a PFX file and its password; the reason, the place and a timestamp authority; and,
/// for a visible signature, a picture. Remembers everything but the password.
/// </summary>
public sealed class SigningWindow : Window
{
    private readonly ViewerSettings _settings;
    private readonly RadioButton _useStore = new() { Content = "A certificate in Windows (a smart card is asked for its PIN when it signs)", IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
    private readonly ComboBox _certificates = new() { MinWidth = 460, Margin = new Thickness(20, 0, 0, 8) };
    private readonly RadioButton _usePfx = new() { Content = "A certificate file (.pfx, .p12)", Margin = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _pfxPath = new() { MinWidth = 380 };
    private readonly PasswordBox _pfxPassword = new() { MinWidth = 200, Margin = new Thickness(20, 4, 0, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
    private readonly TextBox _reason = new() { MinWidth = 460 };
    private readonly TextBox _location = new() { MinWidth = 460 };
    private readonly TextBox _tsa = new() { MinWidth = 460 };
    private readonly TextBox _picture = new() { MinWidth = 380 };
    private readonly bool _visible;

    public SigningChoice? Choice { get; private set; }

    /// <param name="settings">The remembered defaults, updated when the user signs.</param>
    /// <param name="visible">Whether the signature shows on the page, so a picture is asked for.</param>
    public SigningWindow(ViewerSettings settings, bool visible)
    {
        _settings = settings;
        _visible = visible;
        Title = visible ? "Sign — the signature will show where you drew it" : "Sign the field";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = System.Windows.Application.Current?.MainWindow;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(Heading("Sign with"));
        panel.Children.Add(_useStore);
        panel.Children.Add(_certificates);
        panel.Children.Add(_usePfx);
        var pfxRow = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(20, 0, 0, 0) };
        pfxRow.Children.Add(_pfxPath);
        var browse = new WpfButton { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
        browse.Click += (_, _) => Browse(_pfxPath, "Certificate files|*.pfx;*.p12|All files|*.*");
        pfxRow.Children.Add(browse);
        panel.Children.Add(pfxRow);
        panel.Children.Add(Small("Password of the file:", 20));
        panel.Children.Add(_pfxPassword);

        panel.Children.Add(Heading("Written into the signature (optional)"));
        panel.Children.Add(Small("Reason"));
        panel.Children.Add(_reason);
        panel.Children.Add(Small("Location"));
        panel.Children.Add(_location);
        panel.Children.Add(Small("Timestamp authority — proves when you signed, so the signature still verifies after the certificate expires (e.g. http://timestamp.digicert.com)"));
        panel.Children.Add(_tsa);

        if (visible)
        {
            panel.Children.Add(Heading("Picture (optional)"));
            panel.Children.Add(Small("A PNG or JPEG of your signature. Without one, the signature shows your name and the date."));
            var pictureRow = new StackPanel { Orientation = WpfOrientation.Horizontal };
            pictureRow.Children.Add(_picture);
            var browsePicture = new WpfButton { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
            browsePicture.Click += (_, _) => Browse(_picture, "Pictures|*.png;*.jpg;*.jpeg|All files|*.*");
            pictureRow.Children.Add(browsePicture);
            panel.Children.Add(pictureRow);
        }

        var ok = new WpfButton { Content = "Sign", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
        var cancel = new WpfButton { Content = "Cancel", IsCancel = true, MinWidth = 90, Padding = new Thickness(8, 3, 8, 3) };
        ok.Click += (_, _) => Accept();
        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        _reason.Text = settings.SignReason;
        _location.Text = settings.SignLocation;
        _tsa.Text = settings.TimestampUrl;
        _pfxPath.Text = settings.PfxPath;
        LoadCertificates(settings.CertificateThumbprint);
        if (_certificates.Items.Count == 0 || settings.CertificateThumbprint.Length == 0 && settings.PfxPath.Length > 0)
            _usePfx.IsChecked = true;
    }

    private sealed record CertificateItem(string Thumbprint, string Text)
    {
        public override string ToString() => Text;
    }

    private void LoadCertificates(string select)
    {
        try
        {
            X509Certificate2Collection found = CertStoreLookup.FindUsableSigningCertificates(StoreLocation.CurrentUser, DateTime.UtcNow);
            foreach (X509Certificate2 c in found)
            {
                using (c)
                {
                    string text = $"{c.GetNameInfo(X509NameType.SimpleName, false)} — issued by {c.GetNameInfo(X509NameType.SimpleName, true)}, until {c.NotAfter:yyyy-MM-dd}";
                    var item = new CertificateItem(c.Thumbprint, text);
                    _certificates.Items.Add(item);
                    if (string.Equals(c.Thumbprint, select, StringComparison.OrdinalIgnoreCase)) _certificates.SelectedItem = item;
                }
            }
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // An unreadable store lists nothing; the file option remains.
        }
        if (_certificates.SelectedItem == null && _certificates.Items.Count > 0) _certificates.SelectedIndex = 0;
        if (_certificates.Items.Count == 0) _certificates.Items.Add("(no certificate with a private key is in your Windows store — insert the smart card, or use a file)");
    }

    private void Accept()
    {
        string tsa = _tsa.Text.Trim();
        if (tsa.Length > 0 && !(Uri.TryCreate(tsa, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            MessageBox.Show(this, "The timestamp authority must be a web address starting with http:// or https://, or empty.", Title);
            return;
        }
        var parameters = new SignatureParameters
        {
            Reason = Blank(_reason.Text),
            Location = Blank(_location.Text),
            TimestampAuthorityUrl = Blank(tsa),
        };

        IDisposable? signer = null;
        string signerName;
        if (_useStore.IsChecked == true)
        {
            if (_certificates.SelectedItem is not CertificateItem item)
            {
                MessageBox.Show(this, "Choose a certificate, or a certificate file.", Title);
                return;
            }
            X509Certificate2? certificate = CertStoreLookup.FindByThumbprint(item.Thumbprint, StoreLocation.CurrentUser);
            if (certificate == null || !certificate.HasPrivateKey)
            {
                certificate?.Dispose();
                MessageBox.Show(this, "The certificate's key cannot be reached. Insert the smart card it belongs to and try again.", Title);
                return;
            }
            signerName = certificate.GetNameInfo(X509NameType.SimpleName, false);
            var external = new CertStoreExternalSigner(certificate) { Silent = false };
            parameters.ExternalSigner = external;
            signer = new Owned(external, certificate);
            _settings.CertificateThumbprint = item.Thumbprint;
        }
        else
        {
            string path = _pfxPath.Text.Trim();
            if (!File.Exists(path))
            {
                MessageBox.Show(this, "Choose the certificate file.", Title);
                return;
            }
            try
            {
                using var probe = X509CertificateLoader.LoadPkcs12FromFile(path, _pfxPassword.Password);
                signerName = probe.GetNameInfo(X509NameType.SimpleName, false);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                MessageBox.Show(this, "That password does not open the certificate file.", Title);
                return;
            }
            parameters.CertificatePath = path;
            parameters.CertificatePassword = _pfxPassword.Password;
            _settings.PfxPath = path;
            _settings.CertificateThumbprint = "";
        }

        byte[] picture;
        try
        {
            picture = !_visible ? Array.Empty<byte>()
                : _picture.Text.Trim().Length > 0 ? File.ReadAllBytes(_picture.Text.Trim())
                : NamePicture(signerName, parameters.Reason);
        }
        catch (IOException ex)
        {
            signer?.Dispose();
            MessageBox.Show(this, "The picture could not be read: " + ex.Message, Title);
            return;
        }

        _settings.SignReason = _reason.Text.Trim();
        _settings.SignLocation = _location.Text.Trim();
        _settings.TimestampUrl = tsa;
        _settings.Save();
        Choice = new SigningChoice(parameters, picture, signer);
        DialogResult = true;
    }

    /// <summary>A transparent PNG reading "Digitally signed by", the name, the date and the reason.</summary>
    internal static byte[] NamePicture(string name, string? reason)
    {
        using var bitmap = new Bitmap(800, 300, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var big = new Font("Segoe UI", 54f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var small = new Font("Segoe UI", 34f, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(System.Drawing.Color.FromArgb(20, 40, 120));
            g.DrawString("Digitally signed by", small, ink, 10, 10);
            g.DrawString(name, big, ink, new RectangleF(10, 60, 780, 80));
            g.DrawString(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture), small, ink, 10, 160);
            if (!string.IsNullOrWhiteSpace(reason)) g.DrawString(reason, small, ink, new RectangleF(10, 210, 780, 80));
        }
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private sealed class Owned(IDisposable signer, IDisposable certificate) : IDisposable
    {
        public void Dispose()
        {
            signer.Dispose();
            certificate.Dispose();
        }
    }

    private void Browse(TextBox target, string filter)
    {
        var d = new OpenFileDialog { Filter = filter };
        if (d.ShowDialog(this) == true) target.Text = d.FileName;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };

    private static TextBlock Small(string text, double indent = 0) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Foreground = System.Windows.Media.Brushes.DimGray,
        Margin = new Thickness(indent, 4, 0, 2),
    };
}
