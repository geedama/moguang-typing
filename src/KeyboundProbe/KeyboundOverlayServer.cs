using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ActEnhanced.KeyboundProbe
{
    internal sealed class KeyboundOverlayServer : IDisposable
    {
        private readonly object stateLock = new object();
        private readonly TcpListener listener;
        private readonly Thread worker;
        private readonly string token;
        private string stateJson = "{\"Version\":1,\"Ready\":false}";
        private bool disposed;

        public event Action<bool> AutomationRequested;

        public int Port { get; private set; }
        public string Error { get; private set; }

        public KeyboundOverlayServer(string overlayDirectory)
        {
            token = Guid.NewGuid().ToString("N");
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start(8);
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Directory.CreateDirectory(overlayDirectory);
                string runtime = "window.KeyboundOverlayRuntime={endpoint:\"http://127.0.0.1:" +
                    Port + "\",token:\"" + token + "\"};";
                File.WriteAllText(Path.Combine(overlayDirectory, "runtime.js"), runtime,
                    new UTF8Encoding(false));
                worker = new Thread(Run) { IsBackground = true, Name = "KeyboundOverlayServer" };
                worker.Start();
            }
            catch (Exception ex)
            {
                Error = ex.GetType().Name + ": " + ex.Message;
                if (listener != null)
                    try { listener.Stop(); } catch { }
            }
        }

        public void SetState(string json)
        {
            lock (stateLock)
                stateJson = String.IsNullOrEmpty(json) ? "{\"Version\":1,\"Ready\":false}" : json;
        }

        private void Run()
        {
            while (!disposed)
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                    Handle(client);
                }
                catch (SocketException)
                {
                    if (!disposed) Error = "本地控制端口已中断";
                }
                catch (Exception ex)
                {
                    if (!disposed) Error = ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    if (client != null) client.Close();
                }
            }
        }

        private void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 1500;
            client.SendTimeout = 1500;
            NetworkStream stream = client.GetStream();
            byte[] requestBuffer = new byte[16384];
            int count = stream.Read(requestBuffer, 0, requestBuffer.Length);
            if (count <= 0) return;
            string request = Encoding.ASCII.GetString(requestBuffer, 0, count);
            int lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
            string requestLine = lineEnd < 0 ? request : request.Substring(0, lineEnd);
            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2)
            {
                Respond(stream, 400, "application/json", "{\"ok\":false}");
                return;
            }
            string method = parts[0];
            Uri uri;
            if (!Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out uri))
            {
                Respond(stream, 400, "application/json", "{\"ok\":false}");
                return;
            }
            string suppliedToken = QueryValue(uri.Query, "token");
            if (!String.Equals(token, suppliedToken, StringComparison.Ordinal))
            {
                Respond(stream, 403, "application/json", "{\"ok\":false,\"error\":\"forbidden\"}");
                return;
            }
            if (String.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                Respond(stream, 204, "text/plain", String.Empty);
                return;
            }
            if (String.Equals(uri.AbsolutePath, "/state", StringComparison.Ordinal) &&
                String.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                string state;
                lock (stateLock) state = stateJson;
                Respond(stream, 200, "application/json; charset=utf-8", state);
                return;
            }
            if (String.Equals(uri.AbsolutePath, "/automation", StringComparison.Ordinal) &&
                String.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                bool enabled = String.Equals(QueryValue(uri.Query, "enabled"), "true",
                    StringComparison.OrdinalIgnoreCase);
                Action<bool> handler = AutomationRequested;
                if (handler != null) handler(enabled);
                Respond(stream, 200, "application/json; charset=utf-8",
                    "{\"ok\":true,\"enabled\":" + (enabled ? "true" : "false") + "}");
                return;
            }
            Respond(stream, 404, "application/json", "{\"ok\":false,\"error\":\"not_found\"}");
        }

        private static string QueryValue(string query, string key)
        {
            string source = String.IsNullOrEmpty(query) ? String.Empty : query.TrimStart('?');
            foreach (string pair in source.Split('&'))
            {
                int separator = pair.IndexOf('=');
                string name = separator < 0 ? pair : pair.Substring(0, separator);
                if (!String.Equals(Uri.UnescapeDataString(name), key, StringComparison.Ordinal))
                    continue;
                return separator < 0 ? String.Empty : Uri.UnescapeDataString(pair.Substring(separator + 1));
            }
            return String.Empty;
        }

        private static void Respond(NetworkStream stream, int status, string contentType, string body)
        {
            byte[] content = Encoding.UTF8.GetBytes(body ?? String.Empty);
            string reason = status == 200 ? "OK" : status == 204 ? "No Content" :
                status == 400 ? "Bad Request" : status == 403 ? "Forbidden" : "Not Found";
            string headers = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + content.Length + "\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type\r\n" +
                "Cache-Control: no-store\r\nConnection: close\r\n\r\n";
            byte[] prefix = Encoding.ASCII.GetBytes(headers);
            stream.Write(prefix, 0, prefix.Length);
            if (content.Length != 0) stream.Write(content, 0, content.Length);
        }

        internal static string JsonString(string value)
        {
            if (value == null) return "null";
            var result = new StringBuilder(value.Length + 2);
            result.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                            result.Append("\\u").Append(((int)character).ToString("X4"));
                        else
                            result.Append(character);
                        break;
                }
            }
            return result.Append('"').ToString();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (listener != null)
                try { listener.Stop(); } catch { }
            if (worker != null && worker.IsAlive)
                worker.Join(1000);
        }
    }
}
