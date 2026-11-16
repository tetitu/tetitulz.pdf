// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Net;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Regression tests for the redirect-bypassable SSRF guard in <see cref="DssBuilder"/>.
///
/// <para>
/// <b>The bug.</b> OCSP/CRL URLs come from the untrusted certificate's AIA / CRL-DP
/// extensions. <see cref="PrivateAddressGuard.IsSafeHost"/> was applied once, to the URL
/// about to be requested — but the shared <see cref="HttpClient"/> was constructed with no
/// handler, so <c>AllowAutoRedirect</c> defaulted to <c>true</c>. A hostile responder could
/// answer <c>302 Location: http://169.254.169.254/latest/meta-data/</c> and the handler would
/// follow it, with the guard never seeing the new host. <c>PrivateAddressGuard</c>'s own
/// remarks tell callers to "re-check on every request and redirect"; nothing did.
/// </para>
/// <para>
/// The fix disables auto-redirect and follows redirects manually, re-running the guard on
/// every hop with a bounded hop count.
/// </para>
/// </summary>
public class DssBuilderRedirectGuardTests
{
    private const string CloudMetadata = "http://169.254.169.254/latest/meta-data/";
    private const string Loopback = "http://127.0.0.1:8080/crl";
    private const string PrivateLan = "http://192.168.1.10/crl.pem";

    private static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        r.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://example.com/crl");
        return r;
    }

    [Theory]
    [InlineData(CloudMetadata)]   // cloud instance-metadata service — the classic SSRF target
    [InlineData(Loopback)]
    [InlineData(PrivateLan)]
    public void Redirect_ToPrivateOrReservedAddress_IsRefused(string target)
    {
        DssBuilder.ResetLastBlockedRedirect();

        bool followed = false;
        var result = DssBuilder.FollowGuardedRedirects(
            Redirect(target),
            _ => { followed = true; return new HttpResponseMessage(HttpStatusCode.OK); });

        Assert.Null(result);
        Assert.False(followed, "The redirect target must never be requested.");
        Assert.Contains("Refused redirect", DssBuilder.LastBlockedRedirect);
    }

    [Theory]
    [InlineData(CloudMetadata)]
    [InlineData(Loopback)]
    public async Task RedirectAsync_ToPrivateOrReservedAddress_IsRefused(string target)
    {
        DssBuilder.ResetLastBlockedRedirect();

        bool followed = false;
        var result = await DssBuilder.FollowGuardedRedirectsAsync(
            Redirect(target),
            _ => { followed = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });

        Assert.Null(result);
        Assert.False(followed);
        Assert.Contains("Refused redirect", DssBuilder.LastBlockedRedirect);
    }

    /// <summary>Non-http(s) redirect targets (file://, ftp://, gopher://) are refused too.</summary>
    [Theory]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("ftp://example.com/crl")]
    [InlineData("gopher://example.com:70/1")]
    public void Redirect_ToNonHttpScheme_IsRefused(string target)
    {
        DssBuilder.ResetLastBlockedRedirect();

        var result = DssBuilder.FollowGuardedRedirects(
            Redirect(target), _ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Null(result);
        Assert.Contains("Refused redirect", DssBuilder.LastBlockedRedirect);
    }

    /// <summary>A relative Location must resolve against the request URI and still be guarded.</summary>
    [Fact]
    public void Redirect_WithNoLocationHeader_IsRefused()
    {
        DssBuilder.ResetLastBlockedRedirect();

        var noLocation = new HttpResponseMessage(HttpStatusCode.Found);
        var result = DssBuilder.FollowGuardedRedirects(
            noLocation, _ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Null(result);
        Assert.Contains("no Location header", DssBuilder.LastBlockedRedirect);
    }

    /// <summary>A redirect loop must terminate at the hop cap rather than spinning.</summary>
    [Fact]
    public void Redirect_Loop_StopsAtHopLimit()
    {
        DssBuilder.ResetLastBlockedRedirect();

        int sends = 0;
        var result = DssBuilder.FollowGuardedRedirects(
            Redirect("http://example.com/a"),
            _ => { sends++; return Redirect("http://example.com/b"); });

        Assert.Null(result);
        Assert.Contains("Exceeded", DssBuilder.LastBlockedRedirect);
        Assert.True(sends <= DssBuilder.MaxRedirects,
            $"Followed {sends} hops, cap is {DssBuilder.MaxRedirects}.");
    }

    /// <summary>A non-redirect response passes straight through, untouched.</summary>
    [Fact]
    public void NonRedirectResponse_IsReturnedUnchanged()
    {
        var ok = new HttpResponseMessage(HttpStatusCode.OK);
        var result = DssBuilder.FollowGuardedRedirects(ok, _ => throw new InvalidOperationException());

        Assert.Same(ok, result);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]   // 301
    [InlineData(HttpStatusCode.Found)]              // 302
    [InlineData(HttpStatusCode.SeeOther)]           // 303
    [InlineData(HttpStatusCode.TemporaryRedirect)]  // 307
    [InlineData((HttpStatusCode)308)]               // 308 Permanent Redirect
    public void AllRedirectStatusCodes_AreGuarded(HttpStatusCode code)
    {
        DssBuilder.ResetLastBlockedRedirect();

        var result = DssBuilder.FollowGuardedRedirects(
            Redirect(CloudMetadata, code), _ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Null(result);
        Assert.Contains("Refused redirect", DssBuilder.LastBlockedRedirect);
    }

    /// <summary>
    /// The blocked-redirect diagnostic must not leak the query string — AIA URLs can carry
    /// the certificate serial number.
    /// </summary>
    [Fact]
    public void BlockedRedirectDiagnostic_OmitsQueryString()
    {
        DssBuilder.ResetLastBlockedRedirect();

        DssBuilder.FollowGuardedRedirects(
            Redirect("http://127.0.0.1/crl?serial=SECRET-SERIAL-1234"),
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.DoesNotContain("SECRET-SERIAL-1234", DssBuilder.LastBlockedRedirect);
    }
}
