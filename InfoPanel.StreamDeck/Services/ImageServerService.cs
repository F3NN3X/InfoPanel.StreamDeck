using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.StreamDeck.Services
{
    public class ImageServerService : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly ConcurrentDictionary<string, string> _imagePaths = new();
        private readonly ConcurrentDictionary<string, string> _pathToId = new();
        private Task? _listenTask;
        private CancellationTokenSource? _cts;
        private int _port;

        public string BaseUrl => $"http://localhost:{_port}/images/";

        public ImageServerService()
        {
            _listener = new HttpListener();
            _port = GetFreePort();
            _listener.Prefixes.Add(BaseUrl);
        }

        public void Start()
        {
            try
            {
                _listener.Start();
                _cts = new CancellationTokenSource();
                _listenTask = Task.Run(() => ListenLoop(_cts.Token));
                Console.WriteLine($"[ImageServer] Started on {BaseUrl}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImageServer] Failed to start: {ex.Message}");
            }
        }

        public string GetUrl(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return "-";

            if (_pathToId.TryGetValue(filePath, out var id))
            {
                return $"{BaseUrl}{id}";
            }

            id = Guid.NewGuid().ToString("N");
            _imagePaths[id] = filePath;
            _pathToId[filePath] = id;
            return $"{BaseUrl}{id}";
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch (HttpListenerException)
                {
                    // Listener stopped
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ImageServer] Error accepting request: {ex.Message}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            try
            {
                string? id = context.Request.Url?.Segments.LastOrDefault();
                if (id != null && _imagePaths.TryGetValue(id, out string? filePath) && File.Exists(filePath))
                {
                    byte[] buffer = File.ReadAllBytes(filePath);
                    context.Response.ContentType = GetContentType(filePath);
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
            }
            catch
            {
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }

        private string GetContentType(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                ".gif" => "image/gif",
                _ => "application/octet-stream"
            };
        }

        private int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
