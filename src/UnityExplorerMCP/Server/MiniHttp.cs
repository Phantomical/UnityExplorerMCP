using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityExplorerMCP.Server
{
    /// <summary>
    /// A parsed HTTP/1.1 request.
    /// </summary>
    internal sealed class HttpRequest
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Query = new(StringComparer.Ordinal);
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();
        public bool KeepAlive;

        public string GetHeader(string name) =>
            Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Minimal blocking HTTP/1.1 implementation used instead of HttpListener.
    /// Mono's HttpListener completes all socket I/O on the ThreadPool, so the
    /// server stops responding entirely when the pool is starved. Everything
    /// here uses synchronous socket I/O on the calling thread.
    /// </summary>
    internal static class MiniHttp
    {
        const int MaxHeaderBytes = 64 * 1024;
        const int MaxBodyBytes = 64 * 1024 * 1024;

        /// <summary>
        /// Read a request from the stream. Returns null if the connection was
        /// closed before a request line arrived.
        /// </summary>
        public static HttpRequest ReadRequest(Stream stream)
        {
            int headerBytes = 0;
            string requestLine = ReadLine(stream, ref headerBytes);
            while (requestLine != null && requestLine.Length == 0)
                requestLine = ReadLine(stream, ref headerBytes);
            if (requestLine == null)
                return null;

            var parts = requestLine.Split(new[] { ' ' });
            if (parts.Length != 3)
                throw new InvalidDataException($"Malformed request line: {requestLine}");

            var request = new HttpRequest { Method = parts[0].ToUpperInvariant() };
            ParseTarget(parts[1], request);

            while (true)
            {
                string line =
                    ReadLine(stream, ref headerBytes)
                    ?? throw new EndOfStreamException("Connection closed in headers");
                if (line.Length == 0)
                    break;

                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                request.Headers[name] = request.Headers.TryGetValue(name, out var existing)
                    ? existing + ", " + value
                    : value;
            }

            string connection = request.GetHeader("Connection") ?? "";
            request.KeepAlive =
                parts[2] == "HTTP/1.1"
                    ? connection.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0
                    : connection.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0;

            string transferEncoding = request.GetHeader("Transfer-Encoding");
            if (
                transferEncoding != null
                && transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0
            )
            {
                request.Body = ReadChunkedBody(stream);
            }
            else if (long.TryParse(request.GetHeader("Content-Length"), out long length))
            {
                if (length < 0 || length > MaxBodyBytes)
                    throw new InvalidDataException($"Invalid Content-Length: {length}");
                request.Body = ReadExactly(stream, (int)length);
            }

            return request;
        }

        public static void WriteResponse(
            Stream stream,
            int status,
            bool keepAlive,
            string contentType = null,
            string body = null
        )
        {
            byte[] bytes = body != null ? Encoding.UTF8.GetBytes(body) : Array.Empty<byte>();

            var head = new StringBuilder();
            AppendStatusAndCors(head, status);
            if (contentType != null)
                head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
            head.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
            head.Append("\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (bytes.Length > 0)
                stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>
        /// Write the headers of a streaming text/event-stream response and
        /// return a stream that writes the body using chunked encoding.
        /// </summary>
        public static Stream BeginEventStream(Stream stream)
        {
            var head = new StringBuilder();
            AppendStatusAndCors(head, 200);
            head.Append("Content-Type: text/event-stream\r\n");
            head.Append("Cache-Control: no-cache\r\n");
            head.Append("Connection: keep-alive\r\n");
            head.Append("Transfer-Encoding: chunked\r\n");
            head.Append("\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Flush();
            return new ChunkedWriteStream(stream);
        }

        static void AppendStatusAndCors(StringBuilder head, int status)
        {
            head.Append("HTTP/1.1 ")
                .Append(status)
                .Append(' ')
                .Append(ReasonPhrase(status))
                .Append("\r\n");
            head.Append("Access-Control-Allow-Origin: *\r\n");
            head.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            head.Append("Access-Control-Allow-Headers: Content-Type\r\n");
        }

        static string ReasonPhrase(int status) =>
            status switch
            {
                200 => "OK",
                202 => "Accepted",
                204 => "No Content",
                400 => "Bad Request",
                404 => "Not Found",
                405 => "Method Not Allowed",
                410 => "Gone",
                413 => "Payload Too Large",
                500 => "Internal Server Error",
                _ => "Unknown",
            };

        static void ParseTarget(string target, HttpRequest request)
        {
            // Absolute-form targets (http://host/path) are allowed by HTTP/1.1.
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                int slash = target.IndexOf('/', "http://".Length);
                target = slash >= 0 ? target.Substring(slash) : "/";
            }

            int q = target.IndexOf('?');
            string path = q >= 0 ? target.Substring(0, q) : target;
            request.Path = Uri.UnescapeDataString(path);

            if (q < 0)
                return;

            foreach (var pair in target.Substring(q + 1).Split(new[] { '&' }))
            {
                if (pair.Length == 0)
                    continue;
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                string value = eq >= 0 ? pair.Substring(eq + 1) : "";
                request.Query[Unescape(key)] = Unescape(value);
            }
        }

        static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        static string ReadLine(Stream stream, ref int totalBytes)
        {
            var bytes = new List<byte>(128);
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
                if (++totalBytes > MaxHeaderBytes)
                    throw new InvalidDataException("Request headers too large");
                if (b == '\n')
                    break;
                bytes.Add((byte)b);
            }

            if (bytes.Count > 0 && bytes[bytes.Count - 1] == '\r')
                bytes.RemoveAt(bytes.Count - 1);
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        static byte[] ReadExactly(Stream stream, int length)
        {
            var buffer = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(buffer, read, length - read);
                if (n <= 0)
                    throw new EndOfStreamException("Connection closed in body");
                read += n;
            }
            return buffer;
        }

        static byte[] ReadChunkedBody(Stream stream)
        {
            var body = new MemoryStream();
            int lineBytes = 0;
            while (true)
            {
                string sizeLine =
                    ReadLine(stream, ref lineBytes)
                    ?? throw new EndOfStreamException("Connection closed in chunked body");
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0)
                    sizeLine = sizeLine.Substring(0, semi);
                int size = Convert.ToInt32(sizeLine.Trim(), 16);
                if (size == 0)
                    break;
                if (body.Length + size > MaxBodyBytes)
                    throw new InvalidDataException("Request body too large");

                var chunk = ReadExactly(stream, size);
                body.Write(chunk, 0, chunk.Length);
                ReadLine(stream, ref lineBytes);
            }

            // Skip trailers
            while (!string.IsNullOrEmpty(ReadLine(stream, ref lineBytes))) { }

            return body.ToArray();
        }

        /// <summary>
        /// Write-only stream that frames each write as an HTTP chunk. Closing
        /// it writes the terminating chunk but leaves the inner stream open.
        /// </summary>
        sealed class ChunkedWriteStream : Stream
        {
            readonly Stream _inner;
            bool _closed;

            public ChunkedWriteStream(Stream inner)
            {
                _inner = inner;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => !_closed;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_closed)
                    throw new ObjectDisposedException(nameof(ChunkedWriteStream));
                if (count == 0)
                    return;

                byte[] header = Encoding.ASCII.GetBytes(count.ToString("X") + "\r\n");
                _inner.Write(header, 0, header.Length);
                _inner.Write(buffer, offset, count);
                _inner.Write(Crlf, 0, Crlf.Length);
                _inner.Flush();
            }

            public override void Flush() => _inner.Flush();

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_closed)
                {
                    _closed = true;
                    try
                    {
                        _inner.Write(Terminator, 0, Terminator.Length);
                        _inner.Flush();
                    }
                    catch { }
                }
                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };
            static readonly byte[] Terminator = Encoding.ASCII.GetBytes("0\r\n\r\n");
        }
    }
}
