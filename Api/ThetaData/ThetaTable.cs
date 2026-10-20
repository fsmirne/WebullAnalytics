namespace WebullAnalytics.Api.ThetaData;

/// <summary>One ThetaData response, decoded the way the Python library's <c>_convert_response_stream</c> builds its DataFrame:
/// a header list plus rows whose cells are string (text), long (number), double (price, NaN when the server sends price type 0),
/// bool, DateTimeOffset (timestamp, New York offset unless the server tags it UTC) or null. Column order is the server's.</summary>
internal sealed class ThetaTable
{
	internal static readonly ThetaTable Empty = new([], []);

	internal IReadOnlyList<string> Headers { get; }
	internal IReadOnlyList<object?[]> Rows { get; }

	internal ThetaTable(IReadOnlyList<string> headers, IReadOnlyList<object?[]> rows)
	{
		Headers = headers;
		Rows = rows;
	}

	internal int Column(string name)
	{
		for (var i = 0; i < Headers.Count; i++)
			if (Headers[i] == name) return i;
		throw new KeyNotFoundException($"ThetaData response has no '{name}' column (columns: {string.Join(", ", Headers)})");
	}

	internal string Text(int row, int column) => (string)Rows[row][column]!;
	internal long Number(int row, int column) => (long)Rows[row][column]!;
	internal double Price(int row, int column) => (double)Rows[row][column]!;
	internal DateTimeOffset Timestamp(int row, int column) => (DateTimeOffset)Rows[row][column]!;
}
