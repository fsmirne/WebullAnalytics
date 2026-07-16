using Spectre.Console;
using Spectre.Console.Cli;
using System.Net;
using System.Text;
using System.Text.Json;

namespace WebullAnalytics.Sniff;

class SniffSettings : CommandSettings
{
	public override ValidationResult Validate()
	{
		if (!File.Exists(Program.ResolvePath(Program.ApiConfigPath))) return ValidationResult.Error($"Config file '{Program.ApiConfigPath}' does not exist.");
		return ValidationResult.Success();
	}
}

/// <summary>Captures the Webull consumer-web session headers that <c>wa fetch</c> (and the other scrape paths —
/// cash record, charts, GEX) reuse, then writes them into <c>webull.headers</c> in api-config.json.
///
/// Rather than launching a browser under CDP — which forces a second Edge profile (Chromium 136+ refuses remote
/// debugging on the default profile) and so a second Webull web session that trips "multiple sign-in" and logs the
/// user out — this listens on loopback for a companion Tampermonkey userscript (scripts/webull-header-dump.user.js).
/// The script runs in the user's REAL, already-logged-in Webull tab, reads the durable session headers off any
/// authenticated request, and POSTs them here. No second session, no PIN dialog, no DOM driving: the read endpoints
/// drop x-s/x-sv and regenerate t_time themselves, and nothing consumes t_token, so only the identity headers matter.</summary>
class SniffCommand : AsyncCommand<SniffSettings>
{
	// Must match WA_PORT in scripts/webull-header-dump.user.js. Loopback-only; explicit 127.0.0.1 needs no URL ACL.
	private const int ListenPort = 9223;

	protected override async Task<int> ExecuteAsync(CommandContext context, SniffSettings settings, CancellationToken cancellation)
	{
		var configPath = Program.ResolvePath(Program.ApiConfigPath);

		var prefix = $"http://127.0.0.1:{ListenPort}/";
		using var listener = new HttpListener();
		listener.Prefixes.Add(prefix);
		try { listener.Start(); }
		catch (HttpListenerException ex) { Console.WriteLine($"Error: cannot listen on {prefix} ({ex.Message}). Is another 'wa sniff' already running?"); return 1; }

		Console.WriteLine($"Listening on {prefix} for Webull session headers.");
		Console.WriteLine("In your logged-in Webull tab, run the Tampermonkey command \"Dump Webull headers to wa\".");
		Console.WriteLine("(One-time setup: install scripts/webull-header-dump.user.js in Tampermonkey.)");
		Console.WriteLine("Press Ctrl+C to cancel.");

		Dictionary<string, string>? headers = null;
		while (headers == null && !cancellation.IsCancellationRequested)
		{
			HttpListenerContext ctx;
			try { ctx = await listener.GetContextAsync().WaitAsync(cancellation); }
			catch (OperationCanceledException) { break; }

			// GM_xmlhttpRequest bypasses CORS, but reply permissively so a plain fetch from the page works too.
			ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
			ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
			ctx.Response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");

			if (ctx.Request.HttpMethod == "OPTIONS") { WriteResponse(ctx, 204, ""); continue; }
			if (ctx.Request.HttpMethod != "POST") { WriteResponse(ctx, 405, "POST only"); continue; }

			string body;
			using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding)) body = await reader.ReadToEndAsync(cancellation);

			Dictionary<string, string>? parsed;
			try { parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(body); }
			catch (JsonException) { WriteResponse(ctx, 400, "invalid JSON"); continue; }

			// access_token is the one header that proves a logged-in session; without it the script fired before the
			// page made an authenticated request. Reject and keep listening so the user can interact and retry.
			if (parsed == null || !parsed.TryGetValue("access_token", out var token) || string.IsNullOrEmpty(token))
			{
				WriteResponse(ctx, 400, "no access_token in payload — interact with Webull (e.g. open Orders) and retry");
				Console.WriteLine("Received a payload without access_token — still listening.");
				continue;
			}

			headers = parsed;
			WriteResponse(ctx, 200, $"captured {parsed.Count} header(s)");
		}

		listener.Stop();
		if (headers == null) { Console.WriteLine("Cancelled; no headers captured."); return 1; }

		Console.WriteLine($"Captured {headers.Count} header(s).");

		var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
		var webull = root["webull"]?.AsObject();
		if (webull == null)
		{
			webull = new System.Text.Json.Nodes.JsonObject();
			root["webull"] = webull;
		}
		webull["headers"] = JsonSerializer.SerializeToNode(headers);
		File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 }));

		Console.WriteLine($"Updated webull.headers in {configPath}");
		return 0;
	}

	private static void WriteResponse(HttpListenerContext ctx, int status, string message)
	{
		ctx.Response.StatusCode = status;
		if (!string.IsNullOrEmpty(message))
		{
			var bytes = Encoding.UTF8.GetBytes(message);
			ctx.Response.ContentType = "text/plain";
			ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
		}
		ctx.Response.Close();
	}
}
