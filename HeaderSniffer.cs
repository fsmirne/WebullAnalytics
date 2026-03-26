using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WebullAnalytics;

public static class HeaderSniffer
{
	private static readonly string[] HeaderKeys = ["access_token", "did", "lzone", "osv", "ph", "t_time", "t_token", "tz", "ver", "x-s", "x-sv"];
	private const int CdpPort = 9222;

	public static async Task<Dictionary<string, string>> CaptureAsync(string pin, bool autoCloseEdge, CancellationToken cancellation = default)
	{
		if (Process.GetProcessesByName("msedge").Length > 0 && !KillEdge(autoCloseEdge))
			throw new InvalidOperationException("Cannot proceed while Edge is running.");

		var userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "User Data");
		if (!Directory.Exists(userDataDir))
			throw new InvalidOperationException($"Edge user data directory not found: {userDataDir}");

		var edgePath = FindEdgePath();

		Console.WriteLine("Launching Edge...");
		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = edgePath,
			Arguments = $"--remote-debugging-port={CdpPort} --user-data-dir=\"{userDataDir}\" --profile-directory=Default https://app.webull.com/account",
			UseShellExecute = false,
		}) ?? throw new InvalidOperationException("Failed to start Edge.");

		try
		{
			Console.WriteLine("Connecting to Edge DevTools...");
			var wsUrl = await WaitForDebuggerUrl(cancellation);

			using var ws = new ClientWebSocket();
			await ws.ConnectAsync(new Uri(wsUrl), cancellation);

			int cmdId = 0;

			// Enable Network domain so we receive requestWillBeSent events.
			// Response (and any events that fire before we start reading) will buffer in the WebSocket.
			await CdpSend(ws, ++cmdId, "Network.enable", null, cancellation);

			// Wait for the page to load
			Console.WriteLine("Waiting for page to load...");
			await Task.Delay(2000, cancellation);

			// Click the "unlock" link to open the PIN dialog
			Console.WriteLine("Clicking unlock link...");
			await CdpSend(ws, ++cmdId, "Runtime.evaluate", new { expression = "Array.from(document.querySelectorAll('a')).find(a => a.textContent.trim().toLowerCase() === 'unlock')?.click()" }, cancellation);
			await Task.Delay(1500, cancellation);

			// Focus the first input in the PIN dialog and type the code
			Console.WriteLine("Entering unlock code...");
			await CdpSend(ws, ++cmdId, "Runtime.evaluate", new { expression = "document.querySelector('.modal input, [class*=dialog] input, [class*=unlock] input, input[type=password], input[type=tel]')?.focus()" }, cancellation);
			await Task.Delay(300, cancellation);

			foreach (var c in pin)
			{
				var text = c.ToString();
				var keyCode = (int)c;
				await CdpSend(ws, ++cmdId, "Input.dispatchKeyEvent", new { type = "keyDown", text, key = text, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode }, cancellation);
				await CdpSend(ws, ++cmdId, "Input.dispatchKeyEvent", new { type = "keyUp", key = text, windowsVirtualKeyCode = keyCode, nativeVirtualKeyCode = keyCode }, cancellation);
				await Task.Delay(80, cancellation);
			}

			Console.WriteLine("Waiting for API headers...");
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
			cts.CancelAfter(TimeSpan.FromSeconds(30));

			Dictionary<string, string> headers;
			try
			{
				headers = await ReadUntilHeaders(ws, cts.Token);
			}
			catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
			{
				throw new TimeoutException("Timed out waiting for a Webull API request. The unlock code may be incorrect, or the page did not trigger an API call.");
			}

			// Clear the page before closing the browser
			try
			{
				await CdpSend(ws, ++cmdId, "Page.navigate", new { url = "about:blank" }, CancellationToken.None);
				await Task.Delay(500);
			}
			catch { }

			return headers;
		}
		finally
		{
			try { if (!process.HasExited) process.Kill(); } catch { }
		}
	}

	private static bool KillEdge(bool autoClose)
	{
		if (!autoClose)
		{
			Console.Write("Microsoft Edge is running and must be closed to sniff headers. Close it now? [Y/n] ");
			var key = Console.ReadLine()?.Trim();
			if (!string.IsNullOrEmpty(key) && !key.Equals("y", StringComparison.OrdinalIgnoreCase))
				return false;
		}

		Console.WriteLine("Closing Edge...");
		foreach (var p in Process.GetProcessesByName("msedge"))
		{
			try { p.Kill(); } catch { }
		}

		// Wait for processes to exit
		for (int i = 0; i < 20; i++)
		{
			if (Process.GetProcessesByName("msedge").Length == 0) return true;
			Thread.Sleep(250);
		}

		Console.WriteLine("Warning: Edge processes did not exit in time.");
		return false;
	}

	private static string FindEdgePath()
	{
		string[] candidates =
		[
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
		];
		return candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Microsoft Edge not found.");
	}

	private static async Task<string> WaitForDebuggerUrl(CancellationToken cancellation)
	{
		using var http = new HttpClient();
		for (int attempt = 0; attempt < 30; attempt++)
		{
			await Task.Delay(500, cancellation);
			try
			{
				var json = await http.GetStringAsync($"http://localhost:{CdpPort}/json", cancellation);
				foreach (var page in JsonDocument.Parse(json).RootElement.EnumerateArray())
				{
					if (page.TryGetProperty("webSocketDebuggerUrl", out var wsUrl))
						return wsUrl.GetString()!;
				}
			}
			catch (HttpRequestException) { }
		}
		throw new TimeoutException("Timed out waiting for Edge DevTools to become available.");
	}

	private static async Task CdpSend(ClientWebSocket ws, int id, string method, object? parameters, CancellationToken cancellation)
	{
		var msg = parameters != null
			? JsonSerializer.Serialize(new { id, method, @params = parameters })
			: JsonSerializer.Serialize(new { id, method });
		await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, cancellation);
	}

	private static async Task<Dictionary<string, string>> ReadUntilHeaders(ClientWebSocket ws, CancellationToken cancellation)
	{
		var captured = new Dictionary<string, string>();
		var buffer = new byte[65536];
		var sb = new StringBuilder();

		while (ws.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
		{
			sb.Clear();
			WebSocketReceiveResult result;
			do
			{
				result = await ws.ReceiveAsync(buffer, cancellation);
				sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
			} while (!result.EndOfMessage);

			if (result.MessageType == WebSocketMessageType.Close) break;

			try
			{
				var msg = JsonDocument.Parse(sb.ToString()).RootElement;
				if (!msg.TryGetProperty("method", out var method) || method.GetString() != "Network.requestWillBeSent") continue;
				if (!msg.TryGetProperty("params", out var prms) || !prms.TryGetProperty("request", out var req)) continue;
				if (!req.TryGetProperty("url", out var urlEl) || urlEl.GetString()?.Contains("ustrade.webullfinance.com") != true) continue;
				if (!req.TryGetProperty("headers", out var headers)) continue;

				foreach (var key in HeaderKeys)
				{
					if (headers.TryGetProperty(key, out var val))
					{
						var v = val.GetString();
						if (!string.IsNullOrEmpty(v)) captured[key] = v;
					}
				}

				if (captured.ContainsKey("access_token") && captured.ContainsKey("x-s"))
					return new Dictionary<string, string>(captured);
			}
			catch (JsonException) { }
		}

		throw new TimeoutException("WebSocket closed before headers were captured.");
	}
}
