using System.Runtime.CompilerServices;
using WebullAnalytics.AI;
using WebullAnalytics.Pricing;

namespace WebullAnalytics.Tests;

/// <summary>Pins every test to the calendar clock: the app resolves its data folder to the machine's production store, whose clock fits
/// would otherwise leak into tests and make expected prices depend on the machine. Tests of fitted clocks pass weights explicitly.
/// The intraday variance profile is pinned off for the same reason (it is fitted from the machine's minute tape).</summary>
internal static class TestClock
{
	[ModuleInitializer]
	internal static void UseCalendarClock() => MarketClock.FitLookup = static (_, _) => null;

	[ModuleInitializer]
	internal static void UseEvenSessionSpread() => IntradayVarianceProfile.Lookup = static (_, _) => null;
}
