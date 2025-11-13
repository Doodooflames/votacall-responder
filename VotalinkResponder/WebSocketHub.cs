using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VotalinkResponder
{
    public class WebSocketClientInfo
    {
        public string Id { get; set; } = "";
        public string BrowserName { get; set; } = "Unknown";
        public string BrowserVersion { get; set; } = "";
        public string UserAgent { get; set; } = "";
        public DateTime ConnectedAt { get; set; } = DateTime.Now;
        public bool IsExtension { get; set; } = false;
        public bool HasVotacallTab { get; set; } = false;
        public int VotacallTabCount { get; set; } = 0;
        public string? ExtensionName { get; set; } // Extension name for path detection
        public string? ExtensionVersion { get; set; } // Extension version for path detection
    }

    public class ExtensionReplyEventArgs : EventArgs
    {
        public string ClientId { get; set; } = "";
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }

    public sealed class WebSocketHub
    {
        private readonly HttpListener _listener;
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
        private readonly ConcurrentDictionary<string, WebSocketClientInfo> _clientInfo = new();
        private readonly int _port;

        public event EventHandler<int>? ClientCountChanged;
        public event EventHandler<WebSocketClientInfo>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;
        public event EventHandler<ExtensionReplyEventArgs>? ExtensionReplyReceived;
        public event EventHandler<string>? LogMessage; // For debugging messages

        public WebSocketHub(int port)
        {
            _port = port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public void Start()
        {
            _listener.Start();
            _ = AcceptLoop();
        }

        public void Stop()
        {
            _listener.Stop();
        }

        public void Broadcast(string json)
        {
            BroadcastWithCount(json);
        }

        public int BroadcastWithCount(string json)
        {
            return BroadcastWithCount(json, null);
        }

        public int BroadcastWithCount(string json, Func<WebSocketClientInfo, bool>? filter)
        {
            var buffer = Encoding.UTF8.GetBytes(json);
            
            // Get clients to send to
            var clientsToSend = _clients
                .Where(kv => kv.Value.State == WebSocketState.Open)
                .Select(kv => new { Id = kv.Key, WebSocket = kv.Value })
                .ToList();
            
            // Apply filter if provided (e.g., only extensions with Votacall tabs)
            if (filter != null)
            {
                clientsToSend = clientsToSend
                    .Where(c => _clientInfo.TryGetValue(c.Id, out var info) && filter(info))
                    .ToList();
            }
            
            if (clientsToSend.Count == 0)
                return 0;
            
            var tasks = clientsToSend
                .Select(c => c.WebSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None))
                .ToArray();
            
            Task.WaitAll(tasks, 100);
            
            // Count successful sends (tasks that completed)
            int successCount = tasks.Count(t => t.IsCompletedSuccessfully);
            return successCount;
        }

        public int ClientCount => _clients.Count(kv => kv.Value.State == WebSocketState.Open);

        public int GetPort() => _port;

        public List<WebSocketClientInfo> GetConnectedClients()
        {
            return _clientInfo.Values
                .Where(info => _clients.ContainsKey(info.Id) && _clients[info.Id].State == WebSocketState.Open)
                .ToList();
        }

        private async Task AcceptLoop()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    if (ctx.Request.IsWebSocketRequest)
                    {
                        var wsCtx = await ctx.AcceptWebSocketAsync(null);
                        var id = Guid.NewGuid().ToString();
                        _clients[id] = wsCtx.WebSocket;
                        
                        // Create client info from User-Agent header
                        var userAgent = ctx.Request.Headers["User-Agent"] ?? "";
                        var clientInfo = new WebSocketClientInfo
                        {
                            Id = id,
                            UserAgent = userAgent,
                            BrowserName = DetectBrowserName(userAgent),
                            BrowserVersion = DetectBrowserVersion(userAgent),
                            ConnectedAt = DateTime.Now
                        };
                        _clientInfo[id] = clientInfo;
                        
                        // Count active extensions with Votacall tabs for initial count
                        var initialActiveCount = _clientInfo.Values
                            .Count(c => c.IsExtension && c.HasVotacallTab && 
                                       _clients.ContainsKey(c.Id) && 
                                       _clients[c.Id].State == WebSocketState.Open);
                        ClientCountChanged?.Invoke(this, initialActiveCount);
                        ClientConnected?.Invoke(this, clientInfo);
                        _ = ReceiveLoop(id, wsCtx.WebSocket);
                    }
                    else
                    {
                        var msg = Encoding.UTF8.GetBytes("VotalinkResponder running");
                        ctx.Response.ContentType = "text/plain";
                        ctx.Response.ContentLength64 = msg.Length;
                        await ctx.Response.OutputStream.WriteAsync(msg, 0, msg.Length);
                        ctx.Response.Close();
                    }
                }
                catch { }
            }
        }

        private async Task ReceiveLoop(string id, WebSocket ws)
        {
            var buffer = new byte[1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    
                    // Handle identification messages from extension and keepalive pings
                    if (res.MessageType == WebSocketMessageType.Text && res.Count > 0)
                    {
                        try
                        {
                            var message = Encoding.UTF8.GetString(buffer, 0, res.Count);
                            
                            // Log ALL incoming messages for debugging
                            if (message.Contains("call-answer-reply"))
                            {
                                var debugMsg = $"[WebSocketHub] 🔵 INCOMING MESSAGE (contains call-answer-reply) - {res.Count} bytes";
                                LogMessage?.Invoke(this, debugMsg);
                                DebugConsoleForm.Instance.WriteLine(debugMsg);
                                DebugConsoleForm.Instance.WriteLine($"[WebSocketHub] Raw: {message}");
                            }
                            
                            // Log ALL WebSocket messages to debug console
                            DebugConsoleForm.Instance.WriteLine($"[WebSocketHub] Received message type: {message.Substring(0, Math.Min(50, message.Length))}...");
                            
                            var json = JsonDocument.Parse(message);
                            
                            if (json.RootElement.TryGetProperty("type", out var typeElement))
                            {
                                var type = typeElement.GetString();
                                
                                // Log all message types for debugging (especially call-answer-reply)
                                if (type == "call-answer-reply")
                                {
                                    var rawMsg = Encoding.UTF8.GetString(buffer, 0, res.Count);
                                    System.Console.WriteLine($"[WebSocketHub] ✓ Received message with type='call-answer-reply' from client {id.Substring(0, Math.Min(8, id.Length))}...");
                                    System.Console.WriteLine($"[WebSocketHub] Raw message: {rawMsg}");
                                }
                                
                                if (type == "extension-identify" && _clientInfo.TryGetValue(id, out var info))
                                {
                                    bool hadVotacallTab = info.HasVotacallTab;
                                    
                                    // Update client info with extension details
                                    if (json.RootElement.TryGetProperty("browser", out var browser))
                                        info.BrowserName = browser.GetString() ?? info.BrowserName;
                                    if (json.RootElement.TryGetProperty("version", out var version))
                                        info.BrowserVersion = version.GetString() ?? info.BrowserVersion;
                                    if (json.RootElement.TryGetProperty("hasVotacallTab", out var hasTab))
                                        info.HasVotacallTab = hasTab.GetBoolean();
                                    if (json.RootElement.TryGetProperty("votacallTabCount", out var tabCount))
                                        info.VotacallTabCount = tabCount.GetInt32();
                                    // Extract extension fingerprint for path detection
                                    if (json.RootElement.TryGetProperty("extensionFingerprint", out var fingerprint))
                                    {
                                        if (fingerprint.TryGetProperty("name", out var extName))
                                            info.ExtensionName = extName.GetString();
                                        if (fingerprint.TryGetProperty("version", out var extVersion))
                                            info.ExtensionVersion = extVersion.GetString();
                                    }
                                    info.IsExtension = true;
                                    
                                    // If tab status changed, trigger count change event
                                    if (hadVotacallTab != info.HasVotacallTab)
                                    {
                                        var activeCount = _clientInfo.Values
                                            .Count(c => c.IsExtension && c.HasVotacallTab && 
                                                       _clients.ContainsKey(c.Id) && 
                                                       _clients[c.Id].State == WebSocketState.Open);
                                        ClientCountChanged?.Invoke(this, activeCount);
                                    }
                                    
                                    ClientConnected?.Invoke(this, info); // Re-notify with updated info
                                }
                                else if (type == "ping")
                                {
                                    // Respond to keepalive ping with pong to keep connection alive
                                    var pong = Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");
                                    await ws.SendAsync(new ArraySegment<byte>(pong), WebSocketMessageType.Text, true, CancellationToken.None);
                                }
                                else if (type == "call-answer-reply" || type == "call-hangup-reply")
                                {
                                    // Handle reply from extension about button click result
                                    var actionType = type == "call-answer-reply" ? "answer" : "hangup";
                                    var processMsg = $"[WebSocketHub] Processing call-{actionType}-reply message...";
                                    LogMessage?.Invoke(this, processMsg);
                                    DebugConsoleForm.Instance.WriteLine(processMsg);
                                    
                                    try
                                    {
                                        if (json.RootElement.TryGetProperty("success", out var success) &&
                                            json.RootElement.TryGetProperty("message", out var replyMessage))
                                        {
                                            var successValue = success.GetBoolean();
                                            var messageText = replyMessage.GetString() ?? "";
                                            
                                            var parsedMsg = $"[WebSocketHub] ✓ Parsed: success={successValue}, message=\"{messageText}\"";
                                            LogMessage?.Invoke(this, parsedMsg);
                                            DebugConsoleForm.Instance.WriteLine(parsedMsg);
                                            
                                            var invokeMsg = "[WebSocketHub] Invoking ExtensionReplyReceived event...";
                                            LogMessage?.Invoke(this, invokeMsg);
                                            DebugConsoleForm.Instance.WriteLine(invokeMsg);
                                            
                                            // Trigger event for reply handling
                                            ExtensionReplyReceived?.Invoke(this, new ExtensionReplyEventArgs
                                            {
                                                ClientId = id,
                                                Success = successValue,
                                                Message = messageText
                                            });
                                            
                                            var invokedMsg = "[WebSocketHub] ✓ ExtensionReplyReceived event invoked";
                                            LogMessage?.Invoke(this, invokedMsg);
                                            DebugConsoleForm.Instance.WriteLine(invokedMsg);
                                        }
                                        else
                                        {
                                            // Log malformed message - this will help debug
                                            var rawMessage = Encoding.UTF8.GetString(buffer, 0, res.Count);
                                            var errorMsg = $"[WebSocketHub] ⚠ Missing success/message properties. Raw: {rawMessage}";
                                            LogMessage?.Invoke(this, errorMsg);
                                            DebugConsoleForm.Instance.WriteLine(errorMsg);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        var exMsg = $"[WebSocketHub] ⚠ Error: {ex.Message}";
                                        LogMessage?.Invoke(this, exMsg);
                                        DebugConsoleForm.Instance.WriteLine(exMsg);
                                        DebugConsoleForm.Instance.WriteLine($"[WebSocketHub] Stack: {ex.StackTrace}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log parse errors for debugging - especially for call-answer-reply messages
                            try
                            {
                                var rawMessage = Encoding.UTF8.GetString(buffer, 0, res.Count);
                                if (rawMessage.Contains("call-answer-reply"))
                                {
                                    LogMessage?.Invoke(this, $"[WebSocketHub] ⚠ Parse error for call-answer-reply: {ex.Message}");
                                    LogMessage?.Invoke(this, $"[WebSocketHub] Raw: {rawMessage}");
                                }
                            }
                            catch { } // Ignore errors in error logging
                        }
                    }
                }
            }
            catch { }
            finally
            {
                _clients.TryRemove(id, out _);
                _clientInfo.TryRemove(id, out _);
                
                // Count active extensions with Votacall tabs
                var activeCount = _clientInfo.Values
                    .Count(c => c.IsExtension && c.HasVotacallTab && 
                               _clients.ContainsKey(c.Id) && 
                               _clients[c.Id].State == WebSocketState.Open);
                ClientCountChanged?.Invoke(this, activeCount);
                ClientDisconnected?.Invoke(this, id);
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            }
        }

        private string DetectBrowserName(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent)) return "Unknown";
            
            var ua = userAgent.ToLower();
            if (ua.Contains("chrome") && !ua.Contains("edg")) return "Chrome";
            if (ua.Contains("edg")) return "Edge";
            if (ua.Contains("firefox")) return "Firefox";
            if (ua.Contains("safari") && !ua.Contains("chrome")) return "Safari";
            return "Unknown";
        }

        private string DetectBrowserVersion(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent)) return "";
            
            // Simple version extraction - look for common patterns
            var ua = userAgent.ToLower();
            if (ua.Contains("chrome/"))
            {
                var start = userAgent.IndexOf("Chrome/", StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    start += 7;
                    var end = userAgent.IndexOf(' ', start);
                    if (end < 0) end = userAgent.Length;
                    return userAgent.Substring(start, end - start);
                }
            }
            return "";
        }
    }
}

