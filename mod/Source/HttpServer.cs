using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BannerlordInspector
{
    /// <summary>
    /// A deliberately tiny HTTP/1.1 server on loopback.
    ///
    /// Why not HttpListener: on Windows it goes through HTTP.SYS, which needs either administrator
    /// rights or a netsh URL reservation. That is friction the user would have to deal with before
    /// anything worked. A raw TcpListener needs neither.
    ///
    /// Two properties are enforced here rather than trusted higher up:
    ///   - it binds to 127.0.0.1 only, so nothing outside this machine can reach it;
    ///   - it answers GET and nothing else. Read-only is a protocol guarantee, not a convention -
    ///     there is no verb through which this server could be asked to change the game.
    /// </summary>
    public sealed class HttpServer : IDisposable
    {
        private readonly int _port;
        private readonly Func<string, IDictionary<string, string>, object> _handler;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        public HttpServer(int port, Func<string, IDictionary<string, string>, object> handler)
        {
            _port = port;
            _handler = handler;
        }

        public bool IsRunning => _running;

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _running = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,   // must never keep the game process alive
                Name = "BannerlordInspector.Accept"
            };
            _acceptThread.Start();

            InspectorLog.Info($"Listening on http://127.0.0.1:{_port}/ (GET only, loopback only).");
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    // Listener stopped, or a transient socket error - either way, stop quietly.
                    if (!_running) return;
                    continue;
                }

                // One short-lived thread per request. Volume here is a handful of calls a minute,
                // so a thread pool would be more machinery than the problem deserves.
                var worker = new Thread(() => Serve(client)) { IsBackground = true };
                worker.Start();
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;

                    string requestLine = ReadLine(stream);
                    if (string.IsNullOrEmpty(requestLine)) return;

                    // Drain headers; we need none of them, but the client expects them consumed.
                    while (!string.IsNullOrEmpty(ReadLine(stream))) { }

                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2)
                    {
                        Respond(stream, 400, new { error = "malformed request line" });
                        return;
                    }

                    string method = parts[0];
                    string target = parts[1];

                    if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        // The read-only guarantee, enforced at the door.
                        Respond(stream, 405, new
                        {
                            error = "This inspector is read-only. Only GET is accepted.",
                            method
                        });
                        return;
                    }

                    string path = target;
                    var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    int q = target.IndexOf('?');
                    if (q >= 0)
                    {
                        path = target.Substring(0, q);
                        ParseQuery(target.Substring(q + 1), query);
                    }

                    object result;
                    int status = 200;
                    try
                    {
                        result = _handler(path, query);
                    }
                    catch (TimeoutException ex)
                    {
                        status = 504;
                        result = new { error = ex.Message };
                    }
                    catch (Exception ex)
                    {
                        status = 500;
                        result = new { error = ex.Message, type = ex.GetType().Name };
                    }

                    Respond(stream, status, result);
                }
            }
            catch (Exception ex)
            {
                InspectorLog.Error("Request failed.", ex);
            }
        }

        private static void ParseQuery(string raw, IDictionary<string, string> into)
        {
            foreach (string pair in raw.Split('&'))
            {
                if (pair.Length == 0) continue;

                int eq = pair.IndexOf('=');
                if (eq < 0) into[Uri.UnescapeDataString(pair)] = string.Empty;
                else
                {
                    into[Uri.UnescapeDataString(pair.Substring(0, eq))] =
                        Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                }
            }
        }

        /// <summary>Reads a CRLF-terminated line without buffering past it.</summary>
        private static string ReadLine(Stream stream)
        {
            var sb = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static void Respond(Stream stream, int status, object payload)
        {
            string json = Json.Serialize(payload);
            byte[] body = Encoding.UTF8.GetBytes(json);

            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
            head.Append("Content-Type: application/json; charset=utf-8\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("Cache-Control: no-store\r\n");
            head.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 500: return "Internal Server Error";
                case 504: return "Gateway Timeout";
                default: return "Unknown";
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            MainThreadDispatcher.DrainAndFail("The inspector is shutting down.");
            InspectorLog.Info("Stopped.");
        }
    }
}
