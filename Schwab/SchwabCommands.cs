using Spectre.Console;
using Spectre.Console.Cli;
using System.Text.Json;
using WebullAnalytics.Api;

namespace WebullAnalytics.Schwab;

/// <summary>Loads api-config.json and returns its <see cref="SchwabConfig"/>, erroring out with actionable
/// messages when the file or the Schwab credentials are missing.</summary>
internal static class SchwabConfigGate
{
	public static bool TryLoad(out ApiConfig config, out SchwabConfig schwab, out string configPath)
	{
		config = null!;
		schwab = null!;
		configPath = Program.ResolvePath(Program.ApiConfigPath);
		if (!File.Exists(configPath))
		{
			AnsiConsole.MarkupLine("[red]api-config.json not found[/] — run `wa sniff` to bootstrap it.");
			return false;
		}
		var loaded = JsonSerializer.Deserialize<ApiConfig>(File.ReadAllText(configPath));
		if (loaded == null)
		{
			AnsiConsole.MarkupLine("[red]failed to parse api-config.json[/].");
			return false;
		}
		config = loaded;
		if (config.Schwab == null || string.IsNullOrWhiteSpace(config.Schwab.ClientId) || string.IsNullOrWhiteSpace(config.Schwab.ClientSecret) || string.IsNullOrWhiteSpace(config.Schwab.RedirectUri))
		{
			AnsiConsole.MarkupLine("[red]Schwab credentials missing[/] — add a \"schwab\" block with clientId, clientSecret, and redirectUri to api-config.json (from your app at developer.schwab.com).");
			return false;
		}
		schwab = config.Schwab;
		return true;
	}
}

internal sealed class SchwabLoginSettings : CommandSettings { }

/// <summary>Three-legged OAuth login: prints the authorize URL, takes the pasted post-login redirect URL,
/// exchanges the code for tokens, and stores the refresh token (good for 7 days).</summary>
internal sealed class SchwabLoginCommand : AsyncCommand<SchwabLoginSettings>
{
	public override async Task<int> ExecuteAsync(CommandContext context, SchwabLoginSettings settings, CancellationToken cancellation)
	{
		if (!SchwabConfigGate.TryLoad(out _, out var schwab, out var configPath)) return 1;

		AnsiConsole.MarkupLine("[bold]Schwab login[/] — open this URL, log in, and authorize:");
		AnsiConsole.WriteLine();
		AnsiConsole.WriteLine(SchwabAuthClient.BuildAuthorizeUrl(schwab));
		AnsiConsole.WriteLine();
		AnsiConsole.MarkupLine($"Your browser will redirect to [dim]{Markup.Escape(schwab.RedirectUri)}...[/] (the page may fail to load — that's fine).");
		AnsiConsole.MarkupLine("Copy the [bold]entire URL[/] from the address bar and paste it here:");
		Console.Write("> ");

		var pasted = Console.ReadLine();
		if (string.IsNullOrWhiteSpace(pasted))
		{
			AnsiConsole.MarkupLine("[red]No URL entered.[/]");
			return 1;
		}

		var code = SchwabAuthClient.ExtractCode(pasted);
		if (code == null)
		{
			AnsiConsole.MarkupLine("[red]Could not find a 'code' parameter in that URL.[/] Paste the full redirect URL including '?code=...'.");
			return 1;
		}

		try
		{
			await SchwabAuthClient.ExchangeCodeAsync(schwab, code, configPath, cancellation);
		}
		catch (SchwabAuthException ex)
		{
			AnsiConsole.MarkupLine($"[red]Token exchange failed:[/] {Markup.Escape(ex.Message)}");
			return 1;
		}

		AnsiConsole.MarkupLine("[green]Logged in.[/] Refresh token stored — valid for [bold]7 days[/]. Re-run `wa schwab login` weekly.");
		return 0;
	}
}

internal sealed class SchwabStatusSettings : CommandSettings { }

/// <summary>Reports whether tokens are present and how long until the access/refresh tokens expire.</summary>
internal sealed class SchwabStatusCommand : AsyncCommand<SchwabStatusSettings>
{
	public override Task<int> ExecuteAsync(CommandContext context, SchwabStatusSettings settings, CancellationToken cancellation)
	{
		if (!SchwabConfigGate.TryLoad(out _, out var schwab, out _)) return Task.FromResult(1);

		if (string.IsNullOrEmpty(schwab.RefreshToken))
		{
			AnsiConsole.MarkupLine("[yellow]Not logged in.[/] Run `wa schwab login`.");
			return Task.FromResult(0);
		}

		var days = SchwabAuthClient.RefreshTokenDaysRemaining(schwab);
		var refreshMsg = days is { } d
			? (d <= 0 ? "[red]EXPIRED — run `wa schwab login`[/]" : d < 1 ? $"[red]{d * 24:F1}h left — re-login soon[/]" : $"[green]{d:F1} days left[/]")
			: "[dim]unknown (no issued timestamp)[/]";
		var accessMsg = schwab.AccessTokenExpiresUtc is { } exp
			? (exp <= DateTime.UtcNow ? "[dim]expired (auto-refreshes on next use)[/]" : $"{(exp - DateTime.UtcNow).TotalMinutes:F0} min left")
			: "[dim]none cached[/]";

		AnsiConsole.MarkupLine($"Refresh token: {refreshMsg}");
		AnsiConsole.MarkupLine($"Access token:  {accessMsg}");
		return Task.FromResult(0);
	}
}
