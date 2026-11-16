// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Net
{
    /// <summary>
    /// The library's own HTTP/1.1 transport for a runtime with no connect hook (.NET Framework): the
    /// socket opened to the address <see cref="PrivateAddressGuard.TryResolveSafeAddresses"/> vetted,
    /// TLS authenticated with the URL's host name, one request with <c>Connection: close</c>, and a
    /// response read under ceilings a hostile server cannot move.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it exists.</b> <c>HttpClientHandler</c> on .NET Framework resolves the host itself
    /// and offers no remote-address hook, so the pre-check's answer and the connection's address came
    /// from two lookups — the rebinding window GAPS.md §17a names. Here the connection is
    /// <see cref="GuardedHttpConnect.ConnectToVettedTaskAsync"/>'s, the same one the
    /// <c>SocketsHttpHandler</c> callback makes: one resolution, refused when any address is private,
    /// the socket dialled to an address that passed.</para>
    /// <para><b>What it does not cover, and hands to <c>HttpClientHandler</c> instead</b> (the pre-check
    /// alone): a request with credentials or an explicit proxy, which
    /// <see cref="GuardedHttpFetch.CreateHandler"/> decides; and a URL the system proxy applies to
    /// (<see cref="SystemProxyApplies"/>), where the proxy resolves the host and no connection of ours
    /// reaches it.</para>
    /// <para><b>Hostile input.</b> The status line and the header section are read under
    /// <see cref="MaxHeaderBytes"/>, at most <see cref="MaxInformationalResponses"/> interim (1xx)
    /// responses are skipped, a chunk-size line is read under <see cref="MaxChunkLineBytes"/> and its
    /// size refused past <see cref="long.MaxValue"/>; a folded header line, a response carrying both
    /// <c>Transfer-Encoding</c> and <c>Content-Length</c>, a coding other than <c>chunked</c>, two
    /// different lengths and a <c>101</c> are refused (RFC 9112 §5.2, §6.1, §6.3). The body's size
    /// is the caller's ceiling (<see cref="GuardedHttpFetch.ReadCappedAsync(HttpContent, long, CancellationToken)"/>),
    /// and every read of it is cancellable, closing the socket, since a .NET Framework socket read
    /// ignores its token.</para>
    /// </remarks>
    internal sealed class GuardedHttpTransport : HttpMessageHandler
    {
        /// <summary>The status line and header section of one response, together.</summary>
        internal const int MaxHeaderBytes = 64 * 1024;

        /// <summary>Interim (1xx) responses skipped before the final one.</summary>
        internal const int MaxInformationalResponses = 8;

        /// <summary>A chunk-size line, its extensions included.</summary>
        internal const int MaxChunkLineBytes = 1024;

        private const int ReadBufferBytes = 8 * 1024;

        private readonly Func<string, int, CancellationToken, Task<Stream>> _connect;
        private readonly RemoteCertificateValidationCallback _validate;
        private readonly Func<Uri, bool> _usesProxy;
        private readonly HttpMessageHandler _proxied;
        private HttpMessageInvoker _proxiedInvoker;

        /// <summary>The transport <see cref="GuardedHttpFetch.CreateHandler"/> builds: the vetted connection, the platform's certificate validation, the system proxy honoured.</summary>
        internal GuardedHttpTransport()
            : this(GuardedHttpConnect.ConnectToVettedTaskAsync, null, SystemProxyApplies, null)
        {
        }

        /// <summary>The test seam: the connection, the certificate check, the proxy decision and the proxied handler supplied.</summary>
        /// <param name="connect">Opens the stream to a host and port, vetting it first.</param>
        /// <param name="validate">Validates the server's certificate; <c>null</c> for the platform's rule.</param>
        /// <param name="usesProxy">Whether a URL goes through a proxy, so the proxied handler takes it.</param>
        /// <param name="proxied">The handler a proxied URL goes to; <c>null</c> for an <c>HttpClientHandler</c> made when first needed.</param>
        internal GuardedHttpTransport(
            Func<string, int, CancellationToken, Task<Stream>> connect, RemoteCertificateValidationCallback validate,
            Func<Uri, bool> usesProxy, HttpMessageHandler proxied)
        {
            _connect = connect ?? throw new ArgumentNullException(nameof(connect));
            _validate = validate ?? PlatformValidation;
            _usesProxy = usesProxy ?? (_ => false);
            _proxied = proxied;
        }

        /// <summary>
        /// Whether the system proxy applies to <paramref name="uri"/>: then the proxy, not this
        /// transport, resolves the host, and the request goes to the proxied handler.
        /// </summary>
        internal static bool SystemProxyApplies(Uri uri)
        {
            try
            {
#if NET
                IWebProxy proxy = HttpClient.DefaultProxy;
#else
                IWebProxy proxy = WebRequest.DefaultWebProxy;
#endif
                if (proxy == null || proxy.IsBypassed(uri)) return false;
                Uri via = proxy.GetProxy(uri);
                return via != null && via != uri;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is PlatformNotSupportedException
                                       || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                // No proxy configuration to read: the request goes direct, through the vetted connection.
                return false;
            }
        }

        /// <summary>
        /// The TLS protocols a host's <c>ServicePointManager.SecurityProtocol</c> names, as
        /// <see cref="SslStream"/> takes them — the two enumerations share their values, and
        /// <c>SystemDefault</c> (0) is <see cref="SslProtocols.None"/>, the operating system's choice.
        /// What the .NET Framework leg authenticates under, as <c>HttpClientHandler</c> there would.
        /// </summary>
        internal static SslProtocols ProtocolsOf(SecurityProtocolType configured) => (SslProtocols)(int)configured;

        /// <summary>
        /// The platform's rule for a server certificate: no errors — or, on .NET Framework, what a
        /// process-wide <c>ServicePointManager.ServerCertificateValidationCallback</c> answers, as
        /// <c>HttpClientHandler</c> there would ask it.
        /// </summary>
        private static bool PlatformValidation(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
#if !NET
            RemoteCertificateValidationCallback hostCallback = ServicePointManager.ServerCertificateValidationCallback;
            if (hostCallback != null) return hostCallback(sender, certificate, chain, errors);
#endif
            return errors == SslPolicyErrors.None;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _proxiedInvoker?.Dispose();
                _proxied?.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            Uri uri = request.RequestUri;
            if (uri == null || !uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new HttpRequestException("Only an absolute http(s) URL can be fetched.");
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Post && request.Method != HttpMethod.Head)
                throw new HttpRequestException($"The {request.Method} method is not sent by this transport.");

            if (_usesProxy(uri))
            {
                _proxiedInvoker ??= new HttpMessageInvoker(_proxied ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: _proxied == null);
                return await _proxiedInvoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            byte[] body = request.Content == null ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            byte[] head = RequestHead(request, uri, body);
            Stream stream = null;
            try
            {
                stream = await _connect(uri.IdnHost, uri.Port, cancellationToken).ConfigureAwait(false);
                if (uri.Scheme == Uri.UriSchemeHttps)
                {
                    var tls = new SslStream(stream, false, _validate);
                    stream = tls;
                    using (cancellationToken.Register(() => tls.Dispose()))
                    {
#if NET
                        await tls.AuthenticateAsClientAsync(uri.IdnHost).ConfigureAwait(false);
#else
                        // What HttpClientHandler there offers: the host's SecurityProtocol and its revocation
                        // switch, where the parameterless overload took the runtime's own defaults.
                        await tls.AuthenticateAsClientAsync(uri.IdnHost, null,
                            ProtocolsOf(ServicePointManager.SecurityProtocol),
                            ServicePointManager.CheckCertificateRevocationList).ConfigureAwait(false);
#endif
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var wire = new WireReader(stream);
                using (cancellationToken.Register(() => wire.Dispose()))
                {
                    await stream.WriteAsync(head, 0, head.Length, cancellationToken).ConfigureAwait(false);
                    if (body.Length > 0) await stream.WriteAsync(body, 0, body.Length, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    HttpResponseMessage response = await ReadResponseAsync(wire, request, cancellationToken).ConfigureAwait(false);
                    stream = null;   // the response's content owns it now
                    return response;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is AuthenticationException || ex is ObjectDisposedException
                                       || ex is System.Net.Sockets.SocketException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new HttpRequestException($"The exchange with '{uri.IdnHost}' failed: {ex.Message}", ex);
            }
            finally
            {
                stream?.Dispose();
            }
        }

        // ---------------------------------------------------------------- the request

        /// <summary>The request line and headers, the transport's own framing in place of any the caller set; and the body.</summary>
        private static byte[] RequestHead(HttpRequestMessage request, Uri uri, byte[] body)
        {
            var sb = new StringBuilder();
            sb.Append(request.Method.Method).Append(' ').Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n");
            string host = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost + "]" : uri.IdnHost;
            sb.Append("Host: ").Append(host);
            if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
            sb.Append("\r\nConnection: close\r\nAccept-Encoding: identity\r\n");
            foreach (var header in request.Headers) AppendHeader(sb, header.Key, header.Value);
            if (request.Content != null)
                foreach (var header in request.Content.Headers) AppendHeader(sb, header.Key, header.Value);
            // A POST states its length, an empty one 0 (RFC 9110 §8.6); a GET or HEAD only with a body.
            if (request.Method == HttpMethod.Post || body.Length > 0)
                sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("\r\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static void AppendHeader(StringBuilder sb, string name, System.Collections.Generic.IEnumerable<string> values)
        {
            // The framing is the transport's: the caller's would contradict it.
            if (string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                return;
            string value = string.Join(", ", values);
            // A value added without validation may hold a line break, which would start a header of its own.
            if (!IsToken(name) || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
                throw new HttpRequestException($"The request header '{name}' cannot be written.");
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        // ---------------------------------------------------------------- the response

        private static async Task<HttpResponseMessage> ReadResponseAsync(WireReader wire, HttpRequestMessage request, CancellationToken ct)
        {
            for (int interim = 0; ; interim++)
            {
                string[] lines = await wire.ReadHeadAsync(MaxHeaderBytes, ct).ConfigureAwait(false);
                int status = ParseStatusLine(lines[0], out string reason);
                if (status == 101) throw new HttpRequestException("The server switched protocols, which this transport does not speak.");
                if (status < 200)
                {
                    if (interim >= MaxInformationalResponses)
                        throw new HttpRequestException($"The server sent more than {MaxInformationalResponses} interim responses.");
                    continue;
                }

                var response = new HttpResponseMessage((HttpStatusCode)status)
                {
                    ReasonPhrase = reason,
                    RequestMessage = request,
                    Version = new Version(1, 1),
                };
                var contentHeaders = new System.Collections.Generic.List<(string Name, string Value)>();
                string transferEncoding = null, contentLength = null;
                for (int i = 1; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                        throw new HttpRequestException("The response folds a header line (RFC 9112 §5.2).");
                    int colon = line.IndexOf(':');
                    if (colon <= 0 || !IsToken(line.Substring(0, colon)))
                        throw new HttpRequestException("The response holds a malformed header line.");
                    string name = line.Substring(0, colon), value = line.Substring(colon + 1).Trim(' ', '\t');
                    if (string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                        transferEncoding = transferEncoding == null ? value : transferEncoding + ", " + value;
                    else if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        if (contentLength != null && contentLength != value)
                            throw new HttpRequestException("The response declares two different lengths.");
                        contentLength = value;
                    }
                    if (!response.Headers.TryAddWithoutValidation(name, value)) contentHeaders.Add((name, value));
                }

                Stream body = BodyOf(wire, request, status, transferEncoding, contentLength);
                var content = new StreamContent(body);
                foreach (var (name, value) in contentHeaders) content.Headers.TryAddWithoutValidation(name, value);
                response.Content = content;
                return response;
            }
        }

        /// <summary>The status code of <c>HTTP/1.x SSS reason</c>, refused for anything else.</summary>
        private static int ParseStatusLine(string line, out string reason)
        {
            reason = "";
            if (line.Length < 12 || !(line.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) || line.StartsWith("HTTP/1.0 ", StringComparison.Ordinal)))
                throw new HttpRequestException("The response's status line is not HTTP/1.x.");
            int status = 0;
            for (int i = 9; i < 12; i++)
            {
                char c = line[i];
                if (c < '0' || c > '9') throw new HttpRequestException("The response's status code is not three digits.");
                status = status * 10 + (c - '0');
            }
            if (status < 100 || status > 599 || (line.Length > 12 && line[12] != ' '))
                throw new HttpRequestException("The response's status code is out of range.");
            reason = line.Length > 13 ? line.Substring(13) : "";
            return status;
        }

        /// <summary>RFC 9112 §6.3: no body, a chunked one, one of a declared length, or one ending at the close.</summary>
        private static Stream BodyOf(WireReader wire, HttpRequestMessage request, int status, string transferEncoding, string contentLength)
        {
            if (request.Method == HttpMethod.Head || status == 204 || status == 304)
            {
                wire.Dispose();
                return new MemoryStream(Array.Empty<byte>(), writable: false);
            }
            if (transferEncoding != null)
            {
                if (contentLength != null)
                    throw new HttpRequestException("The response declares both Transfer-Encoding and Content-Length (RFC 9112 §6.1).");
                if (!string.Equals(transferEncoding.Trim(), "chunked", StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException($"The response's transfer coding '{transferEncoding}' is not chunked alone.");
                return new ChunkedBody(wire);
            }
            if (contentLength != null)
            {
                if (contentLength.Length == 0 || contentLength.Length > 18 || !AllDigits(contentLength))
                    throw new HttpRequestException("The response's Content-Length is not a length.");
                return new FixedLengthBody(wire, long.Parse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture));
            }
            return new CloseDelimitedBody(wire);
        }

        private static bool AllDigits(string s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }

        /// <summary>An RFC 9110 §5.6.2 token: a header name.</summary>
        private static bool IsToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c > 126 || c <= 32) return false;
                if ("\"(),/:;<=>?@[\\]{}".IndexOf(c) >= 0) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- the wire

        /// <summary>
        /// The connection's bytes: a read buffer the head is parsed from, whose rest the body continues
        /// from. Every read is cancellable — a cancellation closes the stream, which ends a read a
        /// .NET Framework socket would not end for its token.
        /// </summary>
        private sealed class WireReader : IDisposable
        {
            private readonly Stream _stream;
            private readonly byte[] _buffer = new byte[ReadBufferBytes];
            private int _start, _end;
            private bool _disposed;

            internal WireReader(Stream stream) => _stream = stream;

            /// <summary>Reads more bytes into the buffer; 0 at the end of the stream.</summary>
            private async Task<int> FillAsync(CancellationToken ct)
            {
                if (_start > 0 && _start == _end) { _start = 0; _end = 0; }
                if (_end == _buffer.Length)
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                    _end -= _start; _start = 0;
                }
                int read;
                try
                {
                    using (ct.Register(Dispose))
                        read = await _stream.ReadAsync(_buffer, _end, _buffer.Length - _end, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ct.IsCancellationRequested && !(ex is OperationCanceledException))
                {
                    // The close the cancellation made ends the read with the stream's own failure.
                    throw new OperationCanceledException("The read was cancelled.", ex, ct);
                }
                ct.ThrowIfCancellationRequested();
                _end += read;
                return read;
            }

            /// <summary>One line up to its CRLF (a bare LF accepted), under <paramref name="maxBytes"/>; <c>null</c> at a clean end of stream.</summary>
            internal async Task<string> ReadLineAsync(int maxBytes, CancellationToken ct)
            {
                var line = new MemoryStream();
                while (true)
                {
                    for (int i = _start; i < _end; i++)
                    {
                        if (_buffer[i] != (byte)'\n') continue;
                        line.Write(_buffer, _start, i - _start);
                        _start = i + 1;
                        if (line.Length > maxBytes) throw new HttpRequestException($"A response line is longer than {maxBytes} bytes.");
                        byte[] bytes = line.ToArray();
                        int length = bytes.Length > 0 && bytes[bytes.Length - 1] == (byte)'\r' ? bytes.Length - 1 : bytes.Length;
                        return Latin1Text.Decode(bytes, 0, length);
                    }
                    line.Write(_buffer, _start, _end - _start);
                    _start = _end;
                    if (line.Length > maxBytes) throw new HttpRequestException($"A response line is longer than {maxBytes} bytes.");
                    if (await FillAsync(ct).ConfigureAwait(false) == 0)
                    {
                        if (line.Length == 0) return null;
                        throw new IOException("The connection closed inside a response line.");
                    }
                }
            }

            /// <summary>
            /// A status line and its header lines, under <paramref name="maxBytes"/> together: each line
            /// is read under what is left of the section's ceiling, so the line that would pass it is the
            /// one refused.
            /// </summary>
            internal async Task<string[]> ReadHeadAsync(int maxBytes, CancellationToken ct)
            {
                var lines = new System.Collections.Generic.List<string>();
                int used = 0;
                while (true)
                {
                    string line = await ReadLineAsync(maxBytes - used, ct).ConfigureAwait(false);
                    if (line == null) throw new IOException("The connection closed before a response.");
                    used += line.Length + 2;
                    if (line.Length == 0)
                    {
                        if (lines.Count == 0) continue;   // RFC 9112 §2.2: an empty line before the status line is passed over
                        return lines.ToArray();
                    }
                    lines.Add(line);
                }
            }

            /// <summary>Up to <paramref name="count"/> bytes, from the buffer first; 0 at the end of the stream.</summary>
            internal async Task<int> ReadAsync(byte[] destination, int offset, int count, CancellationToken ct)
            {
                if (count == 0) return 0;
                if (_start == _end && await FillAsync(ct).ConfigureAwait(false) == 0) return 0;
                int n = Math.Min(count, _end - _start);
                Buffer.BlockCopy(_buffer, _start, destination, offset, n);
                _start += n;
                return n;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _stream.Dispose();
            }
        }

        /// <summary>A read-only body over the wire; disposing it closes the connection.</summary>
        private abstract class BodyStream : Stream
        {
            protected readonly WireReader Wire;
            protected BodyStream(WireReader wire) => Wire = wire;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
                => ReadAsync(buffer, offset, count, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

            protected override void Dispose(bool disposing)
            {
                if (disposing) Wire.Dispose();
                base.Dispose(disposing);
            }
        }

        /// <summary>A body of a declared length; the connection closing short of it is an error.</summary>
        private sealed class FixedLengthBody : BodyStream
        {
            private long _remaining;
            internal FixedLengthBody(WireReader wire, long length) : base(wire) => _remaining = length;

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (_remaining == 0) return 0;
                int n = await Wire.ReadAsync(buffer, offset, (int)Math.Min(count, _remaining), ct).ConfigureAwait(false);
                if (n == 0) throw new IOException($"The connection closed {_remaining} bytes short of the declared length.");
                _remaining -= n;
                return n;
            }
        }

        /// <summary>A body that ends where the server closes the connection.</summary>
        private sealed class CloseDelimitedBody : BodyStream
        {
            internal CloseDelimitedBody(WireReader wire) : base(wire) { }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
                => Wire.ReadAsync(buffer, offset, count, ct);
        }

        /// <summary>RFC 9112 §7.1: chunks, each a hexadecimal size line and its bytes, ended by a zero size and the trailer section.</summary>
        private sealed class ChunkedBody : BodyStream
        {
            private long _inChunk;
            private bool _done;
            internal ChunkedBody(WireReader wire) : base(wire) { }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (_done || count == 0) return 0;
                if (_inChunk == 0)
                {
                    _inChunk = await NextChunkSizeAsync(ct).ConfigureAwait(false);
                    if (_inChunk == 0)
                    {
                        // The trailer section, read under the head's ceiling, and passed over.
                        int used = 0;
                        while (true)
                        {
                            string trailer = await Wire.ReadLineAsync(MaxHeaderBytes - used, ct).ConfigureAwait(false);
                            if (trailer == null) throw new IOException("The connection closed inside the trailer section.");
                            if (trailer.Length == 0) break;
                            used += trailer.Length + 2;
                        }
                        _done = true;
                        return 0;
                    }
                }
                int n = await Wire.ReadAsync(buffer, offset, (int)Math.Min(count, _inChunk), ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("The connection closed inside a chunk.");
                _inChunk -= n;
                if (_inChunk == 0)
                {
                    string end = await Wire.ReadLineAsync(MaxChunkLineBytes, ct).ConfigureAwait(false);
                    if (end == null || end.Length != 0) throw new HttpRequestException("A chunk does not end with CRLF.");
                }
                return n;
            }

            private async Task<long> NextChunkSizeAsync(CancellationToken ct)
            {
                string line = await Wire.ReadLineAsync(MaxChunkLineBytes, ct).ConfigureAwait(false);
                if (line == null) throw new IOException("The connection closed before a chunk.");
                int semicolon = line.IndexOf(';');
                string hex = (semicolon >= 0 ? line.Substring(0, semicolon) : line).Trim(' ', '\t');
                if (hex.Length == 0) throw new HttpRequestException("A chunk-size line holds no size.");
                long size = 0;
                foreach (char c in hex)
                {
                    int digit = c >= '0' && c <= '9' ? c - '0'
                              : c >= 'a' && c <= 'f' ? c - 'a' + 10
                              : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                    if (digit < 0) throw new HttpRequestException("A chunk size is not hexadecimal.");
                    if (size > (long.MaxValue - digit) / 16) throw new HttpRequestException("A chunk size is larger than any body.");
                    size = size * 16 + digit;
                }
                return size;
            }
        }
    }
}
