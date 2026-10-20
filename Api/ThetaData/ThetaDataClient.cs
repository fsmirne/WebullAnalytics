using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using WebullAnalytics.Api.ThetaData.Proto;
using WebullAnalytics.Api.ThetaData.Proto.V3;

namespace WebullAnalytics.Api.ThetaData;

/// <summary>Thrown when ThetaData's single session is unavailable: another process holds <see cref="ThetaSessionLock"/>, or the
/// server ended our session (UNAUTHENTICATED — a sign-in elsewhere with the same login).</summary>
internal sealed class ThetaSessionException(string message) : Exception(message);

/// <summary>Thrown when a call goes <see cref="ThetaDataClient"/>'s stall timeout without receiving a chunk — the silent hang the
/// Python library's calls fall into, which its backfill could only escape by killing the worker process.</summary>
internal sealed class ThetaStallException(string message) : TimeoutException(message);

/// <summary>Direct gRPC client for ThetaData's market-data service — a port of the Python <c>thetadata</c> library (Apache 2.0)
/// pinned to <see cref="LibraryVersion"/>. ThetaData confirmed in writing (2026-10-20) that calling these endpoints from our own
/// client with our own credentials is permitted for our own use, UNSUPPORTED: there's no published spec and the protocol can
/// change with the library, so the protos are regenerated from the library by scripts/thetadata_proto_extract.py and every
/// request mirrors what that library version sends.
///
/// One instance = one ThetaData session. <see cref="ConnectAsync"/> takes <see cref="ThetaSessionLock"/> first and signs in only
/// if it's free; dispose to release both. Concurrent calls on one instance share the session (the library's
/// existing_authorized_client pattern) and are capped at the tier's in-flight limit. Responses stream as chunks; a call that
/// receives no chunk for the stall timeout is cancelled with <see cref="ThetaStallException"/>. A stall bound rather than a
/// whole-call deadline because a large minute window legitimately streams for minutes.</summary>
internal sealed class ThetaDataClient : IDisposable
{
	/// <summary>The thetadata library version the protos and request shapes were taken from.</summary>
	internal const string LibraryVersion = "1.0.15";

	private const string AuthUrl = "https://nexus-api.thetadata.us/identity/terminal/auth_user";
	// The library sends this fixed key on every sign-in; it identifies the client software, not the account.
	private const string TerminalKey = "cf58ada4-4175-11f0-860f-1e2e95c79e64";
	private const string ServiceAddress = "https://mdds-01.thetadata.us:443";
	private const string ClientName = "csharp";
	// The library's default start_time/end_time arguments, sent as given (strings pass through its _fmt_time unchanged).
	internal const string SessionStart = "09:30:00";
	internal const string SessionEnd = "16:00:00";

	private static readonly HttpClient AuthHttp = new();

	private readonly ThetaSessionLock _lock;
	private readonly GrpcChannel _channel;
	private readonly BetaThetaTerminal.BetaThetaTerminalClient _stub;
	private readonly AuthToken _authToken;
	private readonly string _email;
	private readonly SemaphoreSlim _inFlight;
	private readonly TimeSpan _stallTimeout;

	private ThetaDataClient(ThetaSessionLock sessionLock, string sessionId, string email, int maxConcurrent, TimeSpan stallTimeout)
	{
		_lock = sessionLock;
		_authToken = new AuthToken { SessionUuid = sessionId };
		_email = email;
		_inFlight = new SemaphoreSlim(maxConcurrent, maxConcurrent);
		_stallTimeout = stallTimeout;
		// Unbounded receive size: the library's chunks arrive under gRPC's 4 MB default today, but a chunk over it would fail the
		// whole call for a size cap the data doesn't need.
		_channel = GrpcChannel.ForAddress(ServiceAddress, new GrpcChannelOptions { MaxReceiveMessageSize = null });
		_stub = new BetaThetaTerminal.BetaThetaTerminalClient(_channel);
	}

	/// <summary>The creds file the Python backfill uses: THETADATA_CREDENTIALS_FILE, else data/creds.txt (email line 1, password line 2).</summary>
	internal static string DefaultCredsPath() => Environment.GetEnvironmentVariable("THETADATA_CREDENTIALS_FILE") is { Length: > 0 } path ? path : Program.ResolvePath("data/creds.txt");

	/// <param name="maxConcurrent">Requests in flight at once — the subscription tier's limit (Value 2, Standard 4, Pro 8).</param>
	/// <param name="stallTimeout">Longest wait for the next response chunk before the call is cancelled as hung.</param>
	internal static async Task<ThetaDataClient> ConnectAsync(string credsPath, int maxConcurrent, TimeSpan stallTimeout, CancellationToken cancellation)
	{
		var lines = File.Exists(credsPath) ? await File.ReadAllLinesAsync(credsPath, cancellation) : [];
		if (lines.Length < 2 || string.IsNullOrWhiteSpace(lines[0]) || string.IsNullOrWhiteSpace(lines[1]))
			throw new ThetaSessionException($"ThetaData credentials not found at {credsPath} (expected email on line 1, password on line 2)");

		var sessionLock = ThetaSessionLock.TryAcquire() ?? throw new ThetaSessionException($"ThetaData session is in use by another process (the backfill?) — {Program.ResolvePath(ThetaSessionLock.RelativePath)} is held; signing in now would end its session");
		try
		{
			var request = new { email = lines[0].Trim(), password = lines[1].Trim(), authEnv = new { envType = "PROD" } };
			using var message = new HttpRequestMessage(HttpMethod.Post, AuthUrl) { Content = JsonContent.Create(request) };
			message.Headers.Add("TD-TERMINAL-KEY", TerminalKey);
			using var response = await AuthHttp.SendAsync(message, cancellation);
			var body = await response.Content.ReadAsStringAsync(cancellation);
			if (!response.IsSuccessStatusCode)
				throw new ThetaSessionException($"ThetaData sign-in failed ({(int)response.StatusCode}): {body}");
			using var json = JsonDocument.Parse(body);
			var sessionId = json.RootElement.GetProperty("sessionId").GetString()!;
			var email = json.RootElement.GetProperty("user").GetProperty("email").GetString()!;
			return new ThetaDataClient(sessionLock, sessionId, email, maxConcurrent, stallTimeout);
		}
		catch
		{
			sessionLock.Dispose();
			throw;
		}
	}

	/// <summary>option_list_expirations: every listed expiration of a root (columns symbol, expiration).</summary>
	internal IAsyncEnumerable<ThetaTable> OptionListExpirations(string symbol, CancellationToken cancellation)
	{
		var query = new OptionListExpirationsRequestQuery();
		query.Symbol.Add(symbol);
		var request = new OptionListExpirationsRequest { QueryInfo = Info(("symbol", symbol)), Params = query };
		return Stream(options => _stub.GetOptionListExpirations(request, options), cancellation);
	}

	/// <summary>option_history_quote: NBBO per <paramref name="interval"/> (e.g. "1m") for one expiration over [start, end],
	/// between <paramref name="startTime"/> and <paramref name="endTime"/> ET each day.</summary>
	internal IAsyncEnumerable<ThetaTable> OptionHistoryQuote(OptionContract contract, string interval, DateOnly startDate, DateOnly endDate, CancellationToken cancellation, string startTime = SessionStart, string endTime = SessionEnd)
	{
		var query = new OptionHistoryQuoteRequestQuery { ContractSpec = contract.Spec(), Interval = interval, StartTime = startTime, EndTime = endTime, StartDate = Date(startDate), EndDate = Date(endDate) };
		var request = new OptionHistoryQuoteRequest { QueryInfo = Info([.. contract.Parameters(), ("interval", interval), ("start_time", startTime), ("end_time", endTime), ("start_date", Date(startDate)), ("end_date", Date(endDate))]), Params = query };
		return Stream(options => _stub.GetOptionHistoryQuote(request, options), cancellation);
	}

	/// <summary>option_history_ohlc: trade bars per <paramref name="interval"/> for one expiration over [start, end]. A
	/// <paramref name="strikeRange"/> trims the chain server-side to that many listed strikes either side of ATM.</summary>
	internal IAsyncEnumerable<ThetaTable> OptionHistoryOhlc(OptionContract contract, string interval, DateOnly startDate, DateOnly endDate, int? strikeRange, CancellationToken cancellation)
	{
		var query = new OptionHistoryOhlcRequestQuery { ContractSpec = contract.Spec(), Interval = interval, StartTime = SessionStart, EndTime = SessionEnd, StartDate = Date(startDate), EndDate = Date(endDate) };
		(string, string)[] parameters = [.. contract.Parameters(), ("interval", interval), ("start_time", SessionStart), ("end_time", SessionEnd), ("start_date", Date(startDate)), ("end_date", Date(endDate))];
		if (strikeRange is int range)
		{
			query.StrikeRange = range;
			parameters = [.. parameters, ("strike_range", range.ToString(CultureInfo.InvariantCulture))];
		}
		var request = new OptionHistoryOhlcRequest { QueryInfo = Info(parameters), Params = query };
		return Stream(options => _stub.GetOptionHistoryOhlc(request, options), cancellation);
	}

	/// <summary>option_history_eod: one OHLC + closing-NBBO row per contract per day over [start, end], expirations within
	/// <paramref name="maxDte"/> days of each day.</summary>
	internal IAsyncEnumerable<ThetaTable> OptionHistoryEod(OptionContract contract, DateOnly startDate, DateOnly endDate, int maxDte, CancellationToken cancellation)
	{
		var query = new OptionHistoryEodRequestQuery { ContractSpec = contract.Spec(), StartDate = Date(startDate), EndDate = Date(endDate), MaxDte = maxDte };
		var request = new OptionHistoryEodRequest { QueryInfo = Info([.. contract.Parameters(), ("max_dte", maxDte.ToString(CultureInfo.InvariantCulture)), ("start_date", Date(startDate)), ("end_date", Date(endDate))]), Params = query };
		return Stream(options => _stub.GetOptionHistoryEod(request, options), cancellation);
	}

	/// <summary>option_history_open_interest: each contract's reported open interest per day over [start, end].</summary>
	internal IAsyncEnumerable<ThetaTable> OptionHistoryOpenInterest(OptionContract contract, DateOnly startDate, DateOnly endDate, int maxDte, CancellationToken cancellation)
	{
		var query = new OptionHistoryOpenInterestRequestQuery { ContractSpec = contract.Spec(), StartDate = Date(startDate), EndDate = Date(endDate), MaxDte = maxDte };
		var request = new OptionHistoryOpenInterestRequest { QueryInfo = Info([.. contract.Parameters(), ("max_dte", maxDte.ToString(CultureInfo.InvariantCulture)), ("start_date", Date(startDate)), ("end_date", Date(endDate))]), Params = query };
		return Stream(options => _stub.GetOptionHistoryOpenInterest(request, options), cancellation);
	}

	/// <summary>option_at_time_quote: the last NBBO at <paramref name="timeOfDay"/> ET on each date in [start, end]. ThetaData warns
	/// that sub-minute times can make the request hang; pass minute boundaries.</summary>
	internal IAsyncEnumerable<ThetaTable> OptionAtTimeQuote(OptionContract contract, DateOnly startDate, DateOnly endDate, TimeOnly timeOfDay, CancellationToken cancellation)
	{
		var query = new OptionAtTimeQuoteRequestQuery { ContractSpec = contract.Spec(), StartDate = Date(startDate), EndDate = Date(endDate), TimeOfDay = Time(timeOfDay) };
		var request = new OptionAtTimeQuoteRequest { QueryInfo = Info([.. contract.Parameters(), ("start_date", Date(startDate)), ("end_date", Date(endDate)), ("time_of_day", Time(timeOfDay))]), Params = query };
		return Stream(options => _stub.GetOptionAtTimeQuote(request, options), cancellation);
	}

	/// <summary>option_snapshot_quote: the latest NBBO.</summary>
	internal IAsyncEnumerable<ThetaTable> OptionSnapshotQuote(OptionContract contract, CancellationToken cancellation)
	{
		var request = new OptionSnapshotQuoteRequest { QueryInfo = Info(contract.Parameters()), Params = new OptionSnapshotQuoteRequestQuery { ContractSpec = contract.Spec() } };
		return Stream(options => _stub.GetOptionSnapshotQuote(request, options), cancellation);
	}

	/// <summary>stock_history_eod: daily bars for an equity over [start, end].</summary>
	internal IAsyncEnumerable<ThetaTable> StockHistoryEod(string symbol, DateOnly startDate, DateOnly endDate, CancellationToken cancellation)
	{
		var query = new StockHistoryEodRequestQuery { Symbol = symbol, StartDate = Date(startDate), EndDate = Date(endDate) };
		var request = new StockHistoryEodRequest { QueryInfo = Info(("symbol", symbol), ("start_date", Date(startDate)), ("end_date", Date(endDate))), Params = query };
		return Stream(options => _stub.GetStockHistoryEod(request, options), cancellation);
	}

	/// <summary>index_history_eod: daily bars for an index over [start, end].</summary>
	internal IAsyncEnumerable<ThetaTable> IndexHistoryEod(string symbol, DateOnly startDate, DateOnly endDate, CancellationToken cancellation)
	{
		var query = new IndexHistoryEodRequestQuery { Symbol = symbol, StartDate = Date(startDate), EndDate = Date(endDate) };
		var request = new IndexHistoryEodRequest { QueryInfo = Info(("symbol", symbol), ("start_date", Date(startDate)), ("end_date", Date(endDate))), Params = query };
		return Stream(options => _stub.GetIndexHistoryEod(request, options), cancellation);
	}

	// The library's _fmt_if_date / _fmt_time formats.
	internal static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
	private static string Time(TimeOnly time) => time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

	/// <summary>QueryInfo as the library builds it: the session token, the account email as a hint, and the call's parameters echoed
	/// as strings (server-side request logging) under a "client" tag naming the caller.</summary>
	private QueryInfo Info(params (string Key, string Value)[] parameters)
	{
		var info = new QueryInfo { AuthToken = _authToken, EmailHint = _email };
		info.QueryParameters.Add("client", ClientName);
		foreach (var (key, value) in parameters)
			info.QueryParameters[key] = value;
		return info;
	}

	/// <summary>Runs one server-streaming call under the in-flight cap and yields each response chunk decoded. NOT_FOUND is
	/// ThetaData's "no data for this request" (the library's NoDataFoundError) and yields nothing. Every wait for the next chunk is
	/// bounded by the stall timeout.</summary>
	private async IAsyncEnumerable<ThetaTable> Stream(Func<CallOptions, AsyncServerStreamingCall<ResponseData>> start, [EnumeratorCancellation] CancellationToken cancellation)
	{
		await _inFlight.WaitAsync(cancellation);
		try
		{
			using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
			stall.CancelAfter(_stallTimeout);
			using var call = start(new CallOptions(cancellationToken: stall.Token));
			while (await NextChunkAsync(call.ResponseStream, stall, cancellation) is { } chunk)
			{
				yield return DecodeChunk(chunk);
				stall.CancelAfter(_stallTimeout);
			}
		}
		finally
		{
			_inFlight.Release();
		}
	}

	/// <summary>The next chunk, or null at the end of the stream or on NOT_FOUND. Maps a stall-timer cancellation to
	/// <see cref="ThetaStallException"/> and UNAUTHENTICATED to <see cref="ThetaSessionException"/>.</summary>
	private async Task<ResponseData?> NextChunkAsync(IAsyncStreamReader<ResponseData> stream, CancellationTokenSource stall, CancellationToken cancellation)
	{
		try
		{
			return await stream.MoveNext(stall.Token) ? stream.Current : null;
		}
		catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
		{
			return null;
		}
		catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
		{
			throw new ThetaSessionException($"ThetaData ended this session ({ex.Status.Detail}) — another sign-in with the same login replaced it");
		}
		catch (Exception ex) when (ex is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled } && stall.IsCancellationRequested && !cancellation.IsCancellationRequested)
		{
			throw new ThetaStallException($"ThetaData sent nothing for {_stallTimeout.TotalSeconds:0}s — call cancelled as hung");
		}
	}

	/// <summary>One streamed chunk -> its table (the library's per-chunk step of _convert_response_stream).</summary>
	internal static ThetaTable DecodeChunk(ResponseData chunk)
	{
		var table = DataTable.Parser.ParseFrom(Decompress(chunk));
		return new ThetaTable(table.Headers, [.. table.DataTable_.Select(row => row.Values)]);
	}

	private static byte[] Decompress(ResponseData chunk) => chunk.CompressionDescription?.Algo switch
	{
		null or CompressionAlgo.None => chunk.CompressedData.ToByteArray(),
		CompressionAlgo.Zstd => new ZstdSharp.Decompressor().Unwrap(chunk.CompressedData.Span).ToArray(),
		var algo => throw new NotSupportedException($"ThetaData response compression {algo}")
	};

	public void Dispose()
	{
		_channel.Dispose();
		_inFlight.Dispose();
		_lock.Dispose();
	}
}

/// <summary>A ThetaData contract selector: root + expiration, and optionally one strike and one side. Null expiration = every
/// expiration ("*"), null strike = every strike ("*"), null right = both sides — the library's wildcards and defaults.</summary>
internal sealed record OptionContract(string Symbol, DateOnly? Expiration, decimal? Strike = null, bool? IsCall = null)
{
	/// <summary>One contract from an OCC symbol (SPY261021C00670000), or null when it isn't one.</summary>
	internal static OptionContract? FromOcc(string occ) => ParsingHelpers.ParseOptionSymbol(occ) is { } p ? new(p.Root, DateOnly.FromDateTime(p.ExpiryDate), p.Strike, p.CallPut == "C") : null;

	private string ExpirationText => Expiration is { } e ? ThetaDataClient.Date(e) : "*";
	private string StrikeText => Strike?.ToString(CultureInfo.InvariantCulture) ?? "*";
	private string RightText => IsCall switch { true => "call", false => "put", null => "both" };

	internal ContractSpec Spec() => new() { Symbol = Symbol, Expiration = ExpirationText, Strike = StrikeText, Right = RightText };

	internal (string, string)[] Parameters() => [("symbol", Symbol), ("expiration", ExpirationText), ("strike", StrikeText), ("right", RightText)];
}
