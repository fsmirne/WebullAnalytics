using Google.Protobuf;
using WebullAnalytics.Api.ThetaData;
using WebullAnalytics.Api.ThetaData.Proto;
using Xunit;

namespace WebullAnalytics.Tests.Api;

public class ThetaDataClientTests
{
	private static DataValue Text(string v) => new() { Text = v };
	private static DataValue Price(int value, int type) => new() { Price = new Price { Value = value, Type = type } };

	private static ResponseData Chunk(DataTable table, bool zstd)
	{
		var raw = table.ToByteArray();
		var payload = zstd ? new ZstdSharp.Compressor().Wrap(raw).ToArray() : raw;
		return new ResponseData { CompressedData = ByteString.CopyFrom(payload), CompressionDescription = new CompressionDescription { Algo = zstd ? CompressionAlgo.Zstd : CompressionAlgo.None }, OriginalSize = raw.Length };
	}

	private static DataTable QuoteTable(params DataValueList[] rows)
	{
		var table = new DataTable();
		table.Headers.AddRange(["symbol", "timestamp", "bid_size", "bid", "ask_size", "ask", "flag"]);
		table.DataTable_.AddRange(rows);
		return table;
	}

	private static DataValueList Row(DataValue bid, DataValue ask, ZonedDateTime? ts = null)
	{
		var row = new DataValueList();
		row.Values.AddRange([Text("SPY"), new DataValue { Timestamp = ts ?? new ZonedDateTime { EpochMs = 1_791_983_220_000, Zone = WebullAnalytics.Api.ThetaData.Proto.TimeZone.NewYork } }, new DataValue { Number = 50 }, bid, new DataValue { Number = 9 }, ask, new DataValue { Boolean = true }]);
		return row;
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void DecodeChunkScalesPricesLikeTheLibrary(bool zstd)
	{
		// 9541 at type 8 (x0.01) and 9874 at type 8: the library multiplies in double, so 98.74 decodes as 98.74000000000001.
		var table = ThetaDataClient.DecodeChunk(Chunk(QuoteTable(Row(Price(9541, 8), Price(9874, 8))), zstd));

		Assert.Equal(["symbol", "timestamp", "bid_size", "bid", "ask_size", "ask", "flag"], table.Headers);
		Assert.Equal(9541 * 0.01, table.Price(0, table.Column("bid")));
		Assert.Equal(9874 * 0.01, table.Price(0, table.Column("ask")));
		Assert.Equal(50L, table.Number(0, table.Column("bid_size")));
		Assert.Equal("SPY", table.Text(0, table.Column("symbol")));
		Assert.Equal(true, table.Rows[0][table.Column("flag")]);
	}

	[Fact]
	public void DecodeChunkPriceTypeZeroIsNaN()
	{
		var table = ThetaDataClient.DecodeChunk(Chunk(QuoteTable(Row(Price(0, 0), Price(5, 8))), zstd: false));

		Assert.True(double.IsNaN(table.Price(0, table.Column("bid"))));
	}

	[Fact]
	public void DecodeChunkTimestampsCarryTheirZone()
	{
		// 1_791_983_220_000 ms = 2026-10-14 13:07:00 UTC -> 09:07:00 EDT.
		var ny = ThetaDataClient.DecodeChunk(Chunk(QuoteTable(Row(Price(1, 8), Price(2, 8))), zstd: false)).Rows[0][1];
		var utc = ThetaDataClient.DecodeChunk(Chunk(QuoteTable(Row(Price(1, 8), Price(2, 8), new ZonedDateTime { EpochMs = 1_791_983_220_000, Zone = WebullAnalytics.Api.ThetaData.Proto.TimeZone.Utc })), zstd: false)).Rows[0][1];

		Assert.Equal(new DateTimeOffset(2026, 10, 14, 9, 7, 0, TimeSpan.FromHours(-4)), ny);
		Assert.Equal(TimeSpan.FromHours(-4), ((DateTimeOffset)ny!).Offset);
		Assert.Equal(TimeSpan.Zero, ((DateTimeOffset)utc!).Offset);
	}

	[Fact]
	public void FirstBookAppliesTheStoreEncoding()
	{
		ThetaTable Table(DataValue bid, DataValue ask) => ThetaDataClient.DecodeChunk(Chunk(QuoteTable(Row(bid, ask)), zstd: false));

		var twoSided = ThetaQuotes.FirstBook(Table(Price(9541, 8), Price(9874, 8)));
		Assert.Equal(95.41m, twoSided!.Value.Bid);
		Assert.Equal(98.74m, twoSided.Value.Ask);
		Assert.Equal(50, twoSided.Value.BidSize);
		Assert.Equal(9, twoSided.Value.AskSize);

		var oneSided = ThetaQuotes.FirstBook(Table(Price(0, 0), Price(5, 8)));
		Assert.Equal(0m, oneSided!.Value.Bid);
		Assert.Equal(0.05m, oneSided.Value.Ask);

		Assert.Null(ThetaQuotes.FirstBook(Table(Price(0, 0), Price(0, 0))));
		Assert.Null(ThetaQuotes.FirstBook(Table(Price(-1, 8), Price(5, 8))));
		Assert.Null(ThetaQuotes.FirstBook(ThetaTable.Empty));
	}

	[Fact]
	public void TickRoundsHalfToEvenLikePython()
	{
		Assert.Equal(2L, ThetaQuotes.Tick(0.00025));   // round(2.5) == 2 in Python
		Assert.Equal(4L, ThetaQuotes.Tick(0.00035));   // round(3.5) == 4
		Assert.Null(ThetaQuotes.Tick(double.NaN));
	}

	[Fact]
	public void OptionContractFromOccBuildsTheLibrarySpec()
	{
		var spec = OptionContract.FromOcc("SPY261021C00670500")!.Spec();

		Assert.Equal("SPY", spec.Symbol);
		Assert.Equal("2026-10-21", spec.Expiration);
		Assert.Equal("670.5", spec.Strike);
		Assert.Equal("call", spec.Right);
		Assert.Null(OptionContract.FromOcc("SPY"));
		Assert.Equal(("*", "both"), (new OptionContract("SPXW", new DateOnly(2026, 10, 21)).Spec().Strike, new OptionContract("SPXW", new DateOnly(2026, 10, 21)).Spec().Right));
	}
}
