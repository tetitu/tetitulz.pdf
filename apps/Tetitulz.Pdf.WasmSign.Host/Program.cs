// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// Serves the WasmSign client and relays /tsa to ONE configured TSA.
//
// The upstream is configuration, never request data: a caller cannot choose where the relay
// connects, so it cannot be used as an open proxy or to reach internal addresses. Only a
// timestamp query of bounded size is forwarded; it carries a hash, never the document.

const int MaxRequestBytes = 8 * 1024;   // a TimeStampReq is about a hundred bytes
const int MaxReplyBytes = 64 * 1024;    // a token with its certificates is a few kilobytes
const string TsaLimit = "tsa";

var builder = WebApplication.CreateBuilder(args);
string upstream = builder.Configuration["Tsa:Url"] ?? "http://timestamp.digicert.com";
int perMinute = builder.Configuration.GetValue("Tsa:RequestsPerMinute", 30);
builder.Services.AddHttpClient("tsa", c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.MaxResponseContentBufferSize = MaxReplyBytes;
});

// The relay is open to whoever reaches this host, and every request it forwards is one the
// upstream TSA counts against this host. Each client address gets Tsa:RequestsPerMinute; past
// that the answer is 429. Behind a reverse proxy the address is the proxy's unless the host
// applies forwarded headers, and then the limit is one for everyone.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(TsaLimit, context => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, perMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

// The page holds a signing key in memory, so what may run in it and frame it is stated on every
// response: scripts and styles from this origin only (WebAssembly compiled, nothing inline, no
// eval), requests to this origin only (the /tsa relay), no plugin content, no framing, and the
// declared content types taken as declared.
const string ContentSecurityPolicy =
    "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data:; " +
    "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'none'; frame-ancestors 'none'";
app.Use((context, next) =>
{
    IHeaderDictionary headers = context.Response.Headers;
    headers["Content-Security-Policy"] = ContentSecurityPolicy;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    return next();
});

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapPost("/tsa", async (HttpRequest request, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    if (request.ContentType != "application/timestamp-query")
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

    var query = new MemoryStream();
    var buffer = new byte[1024];
    int read;
    while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
    {
        if (query.Length + read > MaxRequestBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        query.Write(buffer, 0, read);
    }

    var content = new ByteArrayContent(query.ToArray());
    content.Headers.ContentType = new("application/timestamp-query");
    using HttpResponseMessage reply = await clients.CreateClient("tsa").PostAsync(upstream, content, cancellationToken);
    if (!reply.IsSuccessStatusCode)
        return Results.StatusCode(StatusCodes.Status502BadGateway);

    return Results.Bytes(await reply.Content.ReadAsByteArrayAsync(cancellationToken), "application/timestamp-reply");
}).RequireRateLimiting(TsaLimit);

app.MapFallbackToFile("index.html");

app.Run();
