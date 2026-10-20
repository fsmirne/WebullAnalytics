using Google.Protobuf.Collections;
using WebullAnalytics.Api.ThetaData.Proto;

namespace WebullAnalytics.Api.ThetaData;

/// <summary>One ThetaData response chunk (or several concatenated): a header list plus the server's rows, decoded on access the way
/// the Python library's <c>_convert_response_stream</c> builds its DataFrame — text, number (long), price (double; NaN when the server
/// sends price type 0), bool, timestamp (New York offset unless the server tags it UTC) or null. Rows stay as the parsed proto
/// values, so a multi-million-row minute window isn't boxed cell by cell.</summary>
internal sealed class ThetaTable
{
	// Price.value is a scaled int; Price.type indexes the scale. Same table and same double multiply as the library, so decoded
	// prices are bit-identical to what the Python backfill stored.
	private static readonly double[] PriceTypeFactors = [0.0, 0.000000001, 0.00000001, 0.0000001, 0.000001, 0.00001, 0.0001, 0.001, 0.01, 0.1, 1.0, 10.0, 100.0, 1000.0, 10000.0, 100000.0, 1000000.0, 10000000.0, 100000000.0, 1000000000.0];
	private static readonly TimeZoneInfo NyTz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

	internal static readonly ThetaTable Empty = new([], []);

	private readonly List<RepeatedField<DataValue>> _rows;

	internal IReadOnlyList<string> Headers { get; }
	internal int RowCount => _rows.Count;

	internal ThetaTable(IReadOnlyList<string> headers, List<RepeatedField<DataValue>> rows)
	{
		Headers = headers;
		_rows = rows;
	}

	/// <summary>Chunks of one response, concatenated (they share the header).</summary>
	internal static async Task<ThetaTable> CollectAsync(IAsyncEnumerable<ThetaTable> chunks)
	{
		IReadOnlyList<string> headers = [];
		var rows = new List<RepeatedField<DataValue>>();
		await foreach (var chunk in chunks)
		{
			headers = chunk.Headers;
			rows.AddRange(chunk._rows);
		}
		return new ThetaTable(headers, rows);
	}

	internal int Column(string name) => ColumnOrNull(name) ?? throw new KeyNotFoundException($"ThetaData response has no '{name}' column (columns: {string.Join(", ", Headers)})");

	internal int? ColumnOrNull(string name)
	{
		for (var i = 0; i < Headers.Count; i++)
			if (Headers[i] == name) return i;
		return null;
	}

	internal string Text(int row, int column) => _rows[row][column].Text;
	internal long Number(int row, int column) => _rows[row][column].Number;
	internal double Price(int row, int column) => DecodePrice(_rows[row][column].Price);
	internal DateTimeOffset Timestamp(int row, int column) => DecodeTimestamp(_rows[row][column].Timestamp);

	/// <summary>A numeric cell as a double, the way pandas.to_numeric(errors="coerce") reads the library's column: price or number,
	/// NaN for null or anything else.</summary>
	internal double Double(int row, int column)
	{
		var cell = _rows[row][column];
		return cell.DataTypeCase switch
		{
			DataValue.DataTypeOneofCase.Price => DecodePrice(cell.Price),
			DataValue.DataTypeOneofCase.Number => cell.Number,
			_ => double.NaN
		};
	}

	/// <summary>Any cell, decoded to its CLR value (string, long, double, bool, DateTimeOffset or null).</summary>
	internal object? Value(int row, int column)
	{
		var cell = _rows[row][column];
		return cell.DataTypeCase switch
		{
			DataValue.DataTypeOneofCase.Text => cell.Text,
			DataValue.DataTypeOneofCase.Number => cell.Number,
			DataValue.DataTypeOneofCase.Price => DecodePrice(cell.Price),
			DataValue.DataTypeOneofCase.Boolean => cell.Boolean,
			DataValue.DataTypeOneofCase.Timestamp => DecodeTimestamp(cell.Timestamp),
			_ => null
		};
	}

	private static double DecodePrice(Proto.Price price) => price.Type != 0 ? price.Value * PriceTypeFactors[price.Type] : double.NaN;

	private static DateTimeOffset DecodeTimestamp(ZonedDateTime timestamp)
	{
		var utc = DateTimeOffset.FromUnixTimeMilliseconds((long)timestamp.EpochMs);
		return timestamp.Zone == Proto.TimeZone.NewYork ? TimeZoneInfo.ConvertTime(utc, NyTz) : utc;
	}
}
