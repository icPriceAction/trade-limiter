// =========================================================================================
// Tool Name:    icPA - Trade Limiter
// Class:        icPATradeLimiter
// Category:     Indicator
// Version:      2.3.2
// Platform:     NinjaTrader 8.0+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/trade-limiter
// YouTube:      https://youtube.com/@icPriceAction
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  Session discipline dashboard that counts your trades, win/loss streaks and
//               realized P&L from the selected account's fills, warns before limits, and can
//               optionally block new entries after a limit, during a loss cooldown or outside
//               a trade window. Positions you already hold are never closed.
//
// Changelog:
//   v2.3.2 (2023-09-23) - Initial public release. Cleaned and optimized by icPriceAction.
//   v2.3.1              - Loss cooldown with countdown and optional entry blocking; a blocked
//                           fill is undone only once across several charts; blocked limit-order
//                           cancels retry, plus a once-a-second sweep.
//   v2.3.0              - Limits use realized P&L only (open P&L is display-only and turns amber
//                           when closing would breach the max loss); chart dimming; blocking-only
//                           enforcement that never closes held positions; optional win-streak
//                           warning; reason-coloured limit message; window follows Playback clock.
//   v2.2.0              - Redesigned dashboard (Dark/Light/Custom themes, compact mode, progress
//                           bars); trade window with its own time zone; early warnings with sound.
//   v2.1.0              - Per-instrument session P&L from own fills (or account-wide P&L);
//                           SessionIterator session start; thread-safe state; latched limits;
//                           optional entry blocking after the limit and outside the trade window.
// =========================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
#endregion

// ---------------------------------------------------------
// Custom drop-downs are declared OUTSIDE the namespace.
// Prefixed "icPATL_" so they never clash with other scripts
// ---------------------------------------------------------
public enum icPATL_LimitMode
{
	Quantity,
	Dollar,
	Either
}

public enum icPATL_BoxPosition
{
	TopLeft,
	TopCenter,
	TopRight,
	BottomLeft,
	BottomCenter,
	BottomRight
}

public enum icPATL_Theme
{
	Dark,
	Light,
	Custom
}

public enum icPATL_WindowTimeZone
{
	USEastern,
	USCentral,
	USPacific,
	London,
	CentralEurope,
	Tokyo,
	Sydney,
	NinjaTraderSetting
}

namespace NinjaTrader.NinjaScript.Indicators.icPA
{
	public class icPATradeLimiter : Indicator
	{
		private const string LogPrefix = "icPA Trade Limiter: ";

		private Account myAccount;
		private SessionIterator sessionIterator;
		private readonly object stateLock = new object();

		// --- Session counters (guarded by stateLock) ---
		private int totalTrades = 0;
		private int consecWins = 0;
		private int consecLosses = 0;
		private double sessionNetPnl = 0.0;
		private bool limitReached = false;
		private LimitKind limitKind = LimitKind.None;

		// Why the session limit latched - decides the message tone (negative / positive / neutral)
		private enum LimitKind { None, MaxLoss, LossStreak, ProfitTarget, WinStreak, MaxTrades }
		private double displayRealized = 0.0;
		private double displayUnrealized = 0.0;
		private double displayLossUsed = 0.0;
		private string displayWarning = "";

		// --- Position math (guarded by stateLock) ---
		private int currentQty = 0;
		private double avgPrice = 0.0;
		private double posGrossPnl = 0.0;
		private double posCommission = 0.0;

		// --- System state ---
		private DateTime currentSessionStartTime = DateTime.MinValue;
		private double lastPrice = 0.0;
		private volatile bool isReady = false;
		private volatile bool isTerminated = false;
		private bool rebuildDone = false;
		private bool isRebuilding = false;
		private bool isSubscribed = false;
		private TimeZoneInfo windowTz = null;
		private readonly BoundedIdSet processedExecutionIds = new BoundedIdSet(20000);	// guarded by stateLock
		private readonly List<Execution> pendingLiveFills = new List<Execution>();		// guarded by stateLock
		private readonly BoundedIdSet cancelledOrderIds = new BoundedIdSet(2000);		// guarded by stateLock

		private DateTime cooldownUntil = DateTime.MinValue;	// guarded by stateLock

		// Shared by every copy of the indicator (several charts on the same account)
		private static readonly object undoLock = new object();
		private static readonly BoundedIdSet UndoneFillKeys = new BoundedIdSet(2000);	// guarded by undoLock

		// --- UI-thread only ---
		private Dispatcher uiDispatcher;
		private bool isCompact = false;
		private string lastWarningSounded = "";
		private string lastBodyKey = null;
		private readonly List<Order> sweepBuffer = new List<Order>();
		private TlPalette pal;
		private FontFamily uiFont;
		private Grid chartGrid;
		private Border dashboardBorder;
		private Border headerBorder;
		private TextBlock headerTitle;
		private TextBlock statusText;
		private System.Windows.Shapes.Ellipse statusDot;
		private StackPanel bodyPanel;
		private TextBlock centerText;
		private Border dimOverlay;
		private DispatcherTimer uiTimer;
		private NinjaTrader.Gui.Chart.Chart chartWindow;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Session discipline dashboard: counts your trades, win/loss streaks and realized P&L from the selected account's own fills, warns before a limit is hit, adds an optional cooldown after a loss and an optional trade-time window, and can block new entries (never closes a position you hold). Works on any bar type, no Tick Replay needed, designed for Calculate = On Each Tick. Tracking starts when the chart goes live and includes today's earlier fills. Developed by Istvan Cebrian | icPriceAction.com";
				Name										= "icPA - Trade Limiter";
				Calculate									= Calculate.OnEachTick;
				IsOverlay									= true;
				DisplayInDataBox							= false;
				PaintPriceMarkers							= false;
				IsSuspendedWhileInactive					= false;
				BarsRequiredToPlot							= 0;	// no lookback; session detection must see the first bars

				// 1. General
				AccountName									= "Sim101";
				ActiveLimitMode								= icPATL_LimitMode.Either;
				UseAccountWidePnl							= false;
				EnableTradeWindow							= false;
				StartTime									= DateTime.Parse("09:30", System.Globalization.CultureInfo.InvariantCulture);
				EndTime										= DateTime.Parse("12:00", System.Globalization.CultureInfo.InvariantCulture);
				WindowTimeZone								= icPATL_WindowTimeZone.USEastern;
				BlockEntriesAfterLimit						= false;	// off by default; never closes a held position
				BlockEntriesOutsideWindow					= false;
				BlockEntriesDuringCooldown					= false;

				// 2. Visuals
				ShowDashboardBox							= true;
				DashboardTheme								= icPATL_Theme.Dark;
				BoxLocation									= icPATL_BoxPosition.BottomLeft;
				StartCompact								= false;
				BoxColor									= FrozenHex("#D8DADC");
				FontColor									= FrozenHex("#111213");
				DashboardFontSize							= 12;
				BoxOpacity									= 92;
				DimChartOnLimit								= true;
				DimOpacity									= 60;

				// 3. Offsets & Thresholds
				MaxTotalTrades								= 5;
				MaxConsecutiveWins							= 3;
				MaxConsecutiveLosses						= 2;
				MaxDailyLoss								= 200;
				DailyProfitTarget							= 300;
				CooldownMinutes								= 0;

				// 4. Alerts
				WarnOneFromLimit							= true;
				WarnBeforeWinLimit							= false;
				WarnAtLossPercent							= 75;
				PlayWarningSound							= true;
			}
			else if (State == State.Configure)
			{
				// Brushes are used on the UI thread; freeze them so they can cross threads safely.
				BoxColor = FreezeBrush(BoxColor, FrozenHex("#D8DADC"));
				FontColor = FreezeBrush(FontColor, FrozenHex("#111213"));
				pal = TlPalette.Build(DashboardTheme, BoxColor, FontColor, BoxOpacity);
				windowTz = ResolveTimeZone(WindowTimeZone);
				isCompact = StartCompact;
			}
			else if (State == State.DataLoaded)
			{
				myAccount = null;
				lock (Account.All)
				{
					foreach (Account a in Account.All)
					{
						if (a.Name == AccountName)
						{
							myAccount = a;
							break;
						}
					}
				}

				sessionIterator = new SessionIterator(Bars);

				// One-time prerequisite warnings (printed once per load, never per bar)
				if (myAccount == null)
					Print(LogPrefix + "account '" + AccountName + "' was not found. Nothing will be tracked or blocked until a valid Account Name is selected.");

				if (Calculate == Calculate.OnBarClose)
					Print(LogPrefix + "Calculate is set to 'On bar close'. Limits still work from fills, but Open P&L and the session reset only update when a bar closes. 'On each tick' is recommended.");
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
				{
					uiDispatcher = ChartControl.Dispatcher;
					uiDispatcher.InvokeAsync(new Action(CreateWpfDashboard));
				}
			}
			else if (State == State.Realtime)
			{
				StartLiveTracking();
			}
			else if (State == State.Terminated)
			{
				// Stops account callbacks, the UI timer and the order sweep from acting after removal
				isTerminated = true;

				if (myAccount != null && isSubscribed)
				{
					myAccount.ExecutionUpdate -= OnAccountExecutionUpdate;
					myAccount.OrderUpdate -= OnAccountOrderUpdate;
					isSubscribed = false;
				}

				Dispatcher d = ChartControl != null ? ChartControl.Dispatcher : uiDispatcher;
				if (d != null)
					d.InvokeAsync(new Action(DisposeWpfDashboard));
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < BarsRequiredToPlot) return;

			// Track the true session open (works for any timezone / session template).
			// IsFirstTickOfBar: run once per bar, not on every tick of the session's first bar.
			if (Bars.IsFirstBarOfSession && IsFirstTickOfBar && sessionIterator != null)
			{
				sessionIterator.GetNextSession(Time[0], true);
				DateTime begin = sessionIterator.ActualSessionBegin;

				if (begin != currentSessionStartTime)
				{
					bool live = State == State.Realtime;
					lock (stateLock)
					{
						currentSessionStartTime = begin;
						if (live) ResetSessionCountersLocked();
					}
					if (live) RequestUiRefresh();
				}
			}

			if (State == State.Realtime)
				lastPrice = Close[0];
		}

		// -------------------------------------------------------------
		// LIVE TRACKING STARTUP
		// -------------------------------------------------------------
		private void StartLiveTracking()
		{
			if (myAccount == null)
			{
				isReady = true;
				return;
			}

			// Subscribe first; any fill arriving during the rebuild is queued, not processed early.
			myAccount.ExecutionUpdate += OnAccountExecutionUpdate;
			myAccount.OrderUpdate += OnAccountOrderUpdate;
			isSubscribed = true;

			string master = Instrument.MasterInstrument.Name;
			List<Execution> snapshot;
			lock (myAccount.Executions)
			{
				// All of today's known fills for this instrument. Fills before the session open
				// only seed the position math (so a carried position closes correctly);
				// they are not counted as session trades. Runs once (stable time ordering).
				snapshot = myAccount.Executions
					.Where(e => e.Instrument != null && e.Instrument.MasterInstrument.Name == master)
					.OrderBy(e => e.Time)
					.ToList();
			}

			double accR, accU;
			ReadAccountPnl(out accR, out accU);

			lock (stateLock)
			{
				isRebuilding = true;
				int unused;

				foreach (Execution e in snapshot)
					ProcessExecutionLocked(e, accR, accU, out unused);

				foreach (Execution e in pendingLiveFills.OrderBy(x => x.Time))
					ProcessExecutionLocked(e, accR, accU, out unused);

				pendingLiveFills.Clear();
				isRebuilding = false;
				rebuildDone = true;

				EvaluateLimitsLocked(accR, accU);
			}

			isReady = true;
			RequestUiRefresh();
		}

		// -------------------------------------------------------------
		// ACCOUNT EVENTS (fire on a non-UI, non-NinjaScript thread)
		// -------------------------------------------------------------
		private void OnAccountExecutionUpdate(object sender, ExecutionEventArgs e)
		{
			if (isTerminated) return;

			Execution ex = e.Execution;
			if (ex == null || ex.Instrument == null) return;
			if (ex.Instrument.MasterInstrument.Name != Instrument.MasterInstrument.Name) return;

			double accR, accU;
			ReadAccountPnl(out accR, out accU);

			bool triggered;
			int addedQty;
			bool longExposure;
			bool limitNow;

			lock (stateLock)
			{
				if (!rebuildDone)
				{
					pendingLiveFills.Add(ex);
					return;
				}

				triggered = ProcessExecutionLocked(ex, accR, accU, out addedQty);
				longExposure = currentQty > 0;
				limitNow = limitReached;
			}

			if (triggered)
				OnLimitTriggered();

			// Market orders can fill before they can be cancelled. If a blocked entry fills,
			// undo ONLY that fill with an offsetting order - any position you already held
			// is never touched. Our own undo orders are never treated as entries.
			if (addedQty > 0 && (ex.Order == null || ex.Order.Name != BlockOrderName))
			{
				if (BlockEntriesAfterLimit && limitNow)
					UndoBlockedFill(ex, addedQty, longExposure, "entry filled after daily limit");
				else if (BlockEntriesDuringCooldown && IsInCooldown())
					UndoBlockedFill(ex, addedQty, longExposure, "entry filled during cooldown");
				else if (BlockEntriesOutsideWindow && IsWindowClosedNow())
					UndoBlockedFill(ex, addedQty, longExposure, "entry filled outside trade window");
			}

			RequestUiRefresh();
		}

		private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
		{
			if (isTerminated || !isReady || !AnyEntryBlockingEnabled()) return;

			Order o = e.Order;
			if (o == null || !IsCancellableState(e.OrderState)) return;

			string reason = BlockReasonFor(o);
			if (reason != null)
				CancelBlockedOrder(o, reason);
		}

		private bool AnyEntryBlockingEnabled()
		{
			return BlockEntriesAfterLimit
				|| (BlockEntriesOutsideWindow && EnableTradeWindow)
				|| (BlockEntriesDuringCooldown && CooldownMinutes > 0);
		}

		private static bool IsCancellableState(OrderState state)
		{
			return state == OrderState.Accepted || state == OrderState.Working || state == OrderState.TriggerPending;
		}

		// Why this order must not be allowed right now, or null if it is fine.
		private string BlockReasonFor(Order o)
		{
			if (o.Instrument == null || o.Instrument.MasterInstrument.Name != Instrument.MasterInstrument.Name) return null;

			// Never touch our own undo orders, or exits / protective orders for an existing position
			if (o.Name == BlockOrderName) return null;

			string reason = null;
			bool limitNow = false;
			if (BlockEntriesAfterLimit) { lock (stateLock) { limitNow = limitReached; } }

			if (limitNow)																		reason = "daily limit reached";
			else if (BlockEntriesDuringCooldown && IsInCooldown())								reason = "cooldown after a loss";
			else if (BlockEntriesOutsideWindow && EnableTradeWindow && IsWindowClosedNow())	reason = "outside trade window";
			if (reason == null) return null;

			if (IsPositionReducing(o)) return null;
			return reason;
		}

		// Sends a cancel every time it is called (a cancel sent while the order is still being
		// accepted can be ignored, so later calls retry). Logs only the first attempt per order.
		private void CancelBlockedOrder(Order o, string reason)
		{
			if (myAccount == null) return;

			bool firstTime;
			lock (stateLock) { firstTime = cancelledOrderIds.Add(o.OrderId); }

			try
			{
				myAccount.Cancel(new[] { o });
				if (firstTime)
					Print(string.Format("{0}cancelled new entry order {1} ({2} {3} {4}) - {5}.",
						LogPrefix, o.OrderId, o.OrderAction, o.Quantity, o.Instrument.FullName, reason));
			}
			catch (Exception ex)
			{
				Print(LogPrefix + "failed to cancel order - " + ex.Message);
			}
		}

		// Safety net, run once a second on the UI thread: cancel any blocked entry order still
		// resting on the account (e.g. a Buy Bid / Sell Ask placed while trading is blocked).
		private void SweepBlockedOrders()
		{
			if (isTerminated || !isReady || myAccount == null || !AnyEntryBlockingEnabled()) return;

			sweepBuffer.Clear();
			try
			{
				string master = Instrument.MasterInstrument.Name;
				lock (myAccount.Orders)
				{
					foreach (Order o in myAccount.Orders)
					{
						if (o.Instrument != null && o.Instrument.MasterInstrument.Name == master && IsCancellableState(o.OrderState))
							sweepBuffer.Add(o);
					}
				}
			}
			catch
			{
				sweepBuffer.Clear();
				return;
			}

			for (int i = 0; i < sweepBuffer.Count; i++)
			{
				Order o = sweepBuffer[i];
				string reason = BlockReasonFor(o);
				if (reason != null)
					CancelBlockedOrder(o, reason);
			}

			sweepBuffer.Clear();
		}

		// -------------------------------------------------------------
		// POSITION MATH (call with stateLock held)
		// Returns true if this fill newly triggered the limit (live only).
		// -------------------------------------------------------------
		// addedQty = contracts of NEW exposure this fill created (adding to or reversing a position).
		private bool ProcessExecutionLocked(Execution ex, double accR, double accU, out int addedQty)
		{
			addedQty = 0;

			if (string.IsNullOrEmpty(ex.ExecutionId)) return false;
			if (!processedExecutionIds.Add(ex.ExecutionId)) return false;

			int qty = ex.Quantity;
			if (qty <= 0) return false;

			bool isBuy = ex.MarketPosition == MarketPosition.Long;
			int signedQty = isBuy ? qty : -qty;
			double price = ex.Price;
			double commPerContract = ex.Commission / qty;
			double pointValue = ex.Instrument.MasterInstrument.PointValue;

			// Opening or adding to a position
			if (currentQty == 0 || Math.Sign(currentQty) == Math.Sign(signedQty))
			{
				int absCur = Math.Abs(currentQty);
				avgPrice = absCur == 0 ? price : ((avgPrice * absCur) + (price * qty)) / (absCur + qty);
				currentQty += signedQty;
				posCommission += ex.Commission;
				addedQty = qty;
				return false;
			}

			// Reducing, closing or reversing
			int prevQty = currentQty;
			int closeQty = Math.Min(Math.Abs(prevQty), qty);
			double points = prevQty > 0 ? (price - avgPrice) : (avgPrice - price);

			posGrossPnl += points * closeQty * pointValue;
			posCommission += closeQty * commPerContract;
			currentQty += signedQty;

			// Partial close - position still open in the same direction
			if (currentQty != 0 && Math.Sign(currentQty) == Math.Sign(prevQty))
				return false;

			// Position flat or reversed: the round trip is complete
			double netTradePnl = posGrossPnl - posCommission;
			posGrossPnl = 0.0;

			int openQty = Math.Abs(currentQty);
			posCommission = openQty * commPerContract;
			avgPrice = openQty > 0 ? price : 0.0;
			addedQty = openQty; // a reversal opens a new position

			// Only count trades that closed inside the current session
			if (ex.Time < currentSessionStartTime) return false;

			// A losing trade starts the cooldown (not when the "loss" is just undoing a blocked entry)
			bool isUndo = ex.Order != null && ex.Order.Name == BlockOrderName;
			if (netTradePnl < 0 && CooldownMinutes > 0 && !isUndo)
			{
				DateTime until = ex.Time.AddMinutes(CooldownMinutes);
				if (until > cooldownUntil) cooldownUntil = until;
			}

			return RegisterClosedTradeLocked(netTradePnl, accR, accU);
		}

		private bool RegisterClosedTradeLocked(double netTradePnl, double accR, double accU)
		{
			totalTrades++;
			sessionNetPnl += netTradePnl;

			if (netTradePnl > 0)
			{
				consecWins++;
				consecLosses = 0;
			}
			else if (netTradePnl < 0)
			{
				consecLosses++;
				consecWins = 0;
			}
			else
			{
				consecWins = 0;
				consecLosses = 0;
			}

			return EvaluateLimitsLocked(accR, accU);
		}

		// Returns true only on the live transition into "limit reached".
		// The limit latches until the next session. Also computes the early-warning text.
		private bool EvaluateLimitsLocked(double accR, double accU)
		{
			double realized = UseAccountWidePnl ? accR : sessionNetPnl;
			double unrealized;

			if (UseAccountWidePnl)
			{
				unrealized = accU;
			}
			else
			{
				unrealized = posGrossPnl - (currentQty != 0 ? posCommission : 0.0);
				if (currentQty != 0 && lastPrice > 0)
				{
					double pts = currentQty > 0 ? (lastPrice - avgPrice) : (avgPrice - lastPrice);
					unrealized += pts * Math.Abs(currentQty) * Instrument.MasterInstrument.PointValue;
				}
			}

			// Limits use realized (closed-trade) P&L only. Open P&L is display-only.
			double lossBasis = realized;

			displayRealized = realized;
			displayUnrealized = unrealized;
			displayLossUsed = Math.Max(0.0, -lossBasis);

			bool useQty = ActiveLimitMode != icPATL_LimitMode.Dollar;
			bool useDollar = ActiveLimitMode != icPATL_LimitMode.Quantity;

			bool hitQuantityLimit = (totalTrades >= MaxTotalTrades) ||
									(consecWins >= MaxConsecutiveWins) ||
									(consecLosses >= MaxConsecutiveLosses);

			bool hitDollarLimit = (lossBasis <= -MaxDailyLoss) ||
								  (realized >= DailyProfitTarget);

			bool hit = (useQty && hitQuantityLimit) || (useDollar && hitDollarLimit);

			// --- Early warning (most urgent first) ---
			string warning = "";
			if (!limitReached && !hit)
			{
				if (useDollar && WarnAtLossPercent > 0 && displayLossUsed >= MaxDailyLoss * WarnAtLossPercent / 100.0)
					warning = string.Format("{0:0}% of loss used", displayLossUsed / MaxDailyLoss * 100.0);
				else if (useQty && WarnOneFromLimit && MaxConsecutiveLosses > 1 && consecLosses == MaxConsecutiveLosses - 1)
					warning = "1 loss from limit";
				else if (useQty && WarnOneFromLimit && MaxTotalTrades > 1 && totalTrades == MaxTotalTrades - 1)
					warning = "Last trade";
				else if (useQty && WarnBeforeWinLimit && MaxConsecutiveWins > 1 && consecWins == MaxConsecutiveWins - 1)
					warning = "1 win from limit";
			}
			displayWarning = warning;

			if (hit && !limitReached)
			{
				limitReached = true;

				// Most serious reason first
				if (useDollar && lossBasis <= -MaxDailyLoss)						limitKind = LimitKind.MaxLoss;
				else if (useQty && consecLosses >= MaxConsecutiveLosses)			limitKind = LimitKind.LossStreak;
				else if (useDollar && realized >= DailyProfitTarget)				limitKind = LimitKind.ProfitTarget;
				else if (useQty && consecWins >= MaxConsecutiveWins)				limitKind = LimitKind.WinStreak;
				else																limitKind = LimitKind.MaxTrades;
				return !isRebuilding;
			}

			return false;
		}

		private void ResetSessionCountersLocked()
		{
			// Open position (currentQty / avgPrice / commissions) deliberately carries over.
			totalTrades = 0;
			consecWins = 0;
			consecLosses = 0;
			sessionNetPnl = 0.0;
			limitReached = false;
			limitKind = LimitKind.None;
			cooldownUntil = DateTime.MinValue;
			displayWarning = "";
			cancelledOrderIds.Clear();
		}

		// -------------------------------------------------------------
		// ENFORCEMENT
		// -------------------------------------------------------------
		private const string BlockOrderName = "icPATL_Undo";

		private void OnLimitTriggered()
		{
			// Alert only. Hitting the limit never closes a position you are already in.
			try { PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav"); }
			catch { }
		}

		// Offsets ONLY the quantity a blocked entry just added. Existing positions are left alone.
		private void UndoBlockedFill(Execution ex, int qty, bool longExposure, string reason)
		{
			if (myAccount == null || qty <= 0) return;

			// Several charts can track the same account and instrument: undo each fill once
			lock (undoLock)
			{
				if (!UndoneFillKeys.Add(myAccount.Name + "|" + ex.ExecutionId)) return;
			}

			try
			{
				// Stop any unfilled remainder of the blocked order first
				if (ex.Order != null && !Order.IsTerminalState(ex.Order.OrderState))
					myAccount.Cancel(new[] { ex.Order });

				OrderAction action = longExposure ? OrderAction.Sell : OrderAction.BuyToCover;
				Order undo = myAccount.CreateOrder(ex.Instrument, action, OrderType.Market, OrderEntry.Automated,
					TimeInForce.Day, qty, 0, 0, string.Empty, BlockOrderName, NinjaTrader.Core.Globals.MaxDate, null);
				myAccount.Submit(new[] { undo });

				Print(string.Format("{0}{1} - submitted {2} {3} {4} to undo it. Any position you already held is unchanged.",
					LogPrefix, reason, action, qty, ex.Instrument.FullName));
			}
			catch (Exception e)
			{
				Print(LogPrefix + "failed to undo blocked entry - " + e.Message);
			}
		}

		private bool IsPositionReducing(Order o)
		{
			if (myAccount == null) return false;

			Position pos = null;
			lock (myAccount.Positions)
			{
				foreach (Position p in myAccount.Positions)
				{
					if (p.Instrument == o.Instrument)
					{
						pos = p;
						break;
					}
				}
			}

			if (pos == null || pos.MarketPosition == MarketPosition.Flat) return false;

			bool isSell = o.OrderAction == OrderAction.Sell || o.OrderAction == OrderAction.SellShort;

			if (pos.MarketPosition == MarketPosition.Long && isSell) return o.Quantity <= pos.Quantity;
			if (pos.MarketPosition == MarketPosition.Short && !isSell) return o.Quantity <= pos.Quantity;

			return false;
		}

		// -------------------------------------------------------------
		// TRADE WINDOW & TIME ZONE
		// -------------------------------------------------------------
		private static TimeZoneInfo ResolveTimeZone(icPATL_WindowTimeZone zone)
		{
			string id = null;
			switch (zone)
			{
				case icPATL_WindowTimeZone.USEastern:		id = "Eastern Standard Time"; break;
				case icPATL_WindowTimeZone.USCentral:		id = "Central Standard Time"; break;
				case icPATL_WindowTimeZone.USPacific:		id = "Pacific Standard Time"; break;
				case icPATL_WindowTimeZone.London:			id = "GMT Standard Time"; break;
				case icPATL_WindowTimeZone.CentralEurope:	id = "W. Europe Standard Time"; break;
				case icPATL_WindowTimeZone.Tokyo:			id = "Tokyo Standard Time"; break;
				case icPATL_WindowTimeZone.Sydney:			id = "AUS Eastern Standard Time"; break;
				default:									return null; // NinjaTrader's own setting
			}

			try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
			catch { return null; }
		}

		private string TimeZoneLabel()
		{
			switch (WindowTimeZone)
			{
				case icPATL_WindowTimeZone.USEastern:		return "ET";
				case icPATL_WindowTimeZone.USCentral:		return "CT";
				case icPATL_WindowTimeZone.USPacific:		return "PT";
				case icPATL_WindowTimeZone.London:			return "UK";
				case icPATL_WindowTimeZone.CentralEurope:	return "CET";
				case icPATL_WindowTimeZone.Tokyo:			return "JST";
				case icPATL_WindowTimeZone.Sydney:			return "AET";
				default:									return "";
			}
		}

		// Platform "now" in NinjaTrader's time zone. Uses the Playback clock when
		// connected to Playback, so the trade window follows the replayed market time.
		private static DateTime PlatformNow()
		{
			try
			{
				Connection playback = Connection.PlaybackConnection;
				if (playback != null)
					return playback.Now;
			}
			catch { }

			return NinjaTrader.Core.Globals.Now;
		}

		// Current time expressed in the trade window's time zone
		private DateTime GetWindowNow()
		{
			DateTime now = PlatformNow();
			if (windowTz == null) return now;

			try
			{
				TimeZoneInfo source = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
				return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(now, DateTimeKind.Unspecified), source, windowTz);
			}
			catch
			{
				return now;
			}
		}

		private bool IsInCooldown()
		{
			if (CooldownMinutes <= 0) return false;
			DateTime until;
			lock (stateLock) { until = cooldownUntil; }
			return until > PlatformNow();
		}

		private static string FormatClock(TimeSpan t)
		{
			int secs = Math.Max(0, (int)Math.Ceiling(t.TotalSeconds));
			return string.Format("{0}:{1:00}", secs / 60, secs % 60);
		}

		private bool IsWindowClosedNow()
		{
			if (!EnableTradeWindow) return false;
			TimeSpan unused;
			return IsOutsideWindow(GetWindowNow(), out unused);
		}

		private bool IsOutsideWindow(DateTime currentTime, out TimeSpan timeUntilStart)
		{
			TimeSpan now = currentTime.TimeOfDay;
			TimeSpan start = StartTime.TimeOfDay;
			TimeSpan end = EndTime.TimeOfDay;
			bool outside;

			if (start < end)
				outside = (now < start || now >= end);
			else
				outside = (now >= end && now < start);

			if (now <= start)
				timeUntilStart = start - now;
			else
				timeUntilStart = (TimeSpan.FromHours(24) - now) + start;

			return outside;
		}

		// -------------------------------------------------------------
		// HELPERS
		// -------------------------------------------------------------
		private void ReadAccountPnl(out double realized, out double unrealized)
		{
			realized = 0.0;
			unrealized = 0.0;

			// Read outside stateLock to avoid lock-ordering issues with NinjaTrader's internals
			if (UseAccountWidePnl && myAccount != null)
			{
				realized = myAccount.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
				unrealized = myAccount.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
			}
		}

		private static string FormatMoney(double amount)
		{
			return amount < 0 ? string.Format("\u2212${0:N2}", Math.Abs(amount)) : string.Format("${0:N2}", amount);
		}

		private static string FormatMoneyShort(double amount)
		{
			return string.Format("${0:N0}", Math.Abs(amount));
		}

		private static string FormatSpan(TimeSpan t)
		{
			if (t.TotalHours >= 1) return string.Format("{0}h {1}m", (int)t.TotalHours, t.Minutes);
			return string.Format("{0}m", Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)));
		}

		private static Brush FrozenHex(string hex)
		{
			SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
			b.Freeze();
			return b;
		}

		private static Brush FreezeBrush(Brush brush, Brush fallback)
		{
			Brush b = brush ?? fallback;
			if (b.IsFrozen) return b;
			Brush clone = b.Clone();
			clone.Freeze();
			return clone;
		}

		// Set of IDs with a fixed capacity: the oldest IDs are dropped first, so long-running
		// charts never grow these collections without limit.
		private sealed class BoundedIdSet
		{
			private readonly HashSet<string> set = new HashSet<string>();
			private readonly Queue<string> order = new Queue<string>();
			private readonly int capacity;

			public BoundedIdSet(int capacity)
			{
				this.capacity = Math.Max(1, capacity);
			}

			public bool Add(string id)
			{
				if (id == null || !set.Add(id)) return false;
				order.Enqueue(id);
				while (order.Count > capacity)
					set.Remove(order.Dequeue());
				return true;
			}

			public void Clear()
			{
				set.Clear();
				order.Clear();
			}
		}

		// -------------------------------------------------------------
		// THEME PALETTE (all brushes frozen; safe on any thread)
		// -------------------------------------------------------------
		private class TlPalette
		{
			public Brush Background, Border, Divider, Track;
			public Brush Text, Strong, Muted;
			public Brush Accent, Positive, Negative, Warning, Danger, HeaderDanger, HeaderPositive, HeaderNeutral;

			private static Brush Hex(string hex, double opacity)
			{
				SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
				b.Opacity = opacity;
				b.Freeze();
				return b;
			}

			private static Brush Hex(string hex) { return Hex(hex, 1.0); }

			private static TlPalette Dark(double bgOpacity)
			{
				TlPalette p = new TlPalette();
				p.Background	= Hex("#1C202A", bgOpacity);
				p.Border		= Hex("#2E3445");
				p.Divider		= Hex("#2E3445");
				p.Track			= Hex("#2E3445");
				p.Text			= Hex("#C9CED8");
				p.Strong		= Hex("#E6E9EF");
				p.Muted			= Hex("#7D8597");
				p.Accent		= Hex("#5B8DEF");
				p.Positive		= Hex("#4CC38A");
				p.Negative		= Hex("#F0807A");
				p.Warning		= Hex("#EF9F27");
				p.Danger		= Hex("#E24B4A");
				p.HeaderDanger	= Hex("#5A1E1E");
				p.HeaderPositive= Hex("#173D2C");
				p.HeaderNeutral	= Hex("#1E2A4A");
				return p;
			}

			private static TlPalette Light(double bgOpacity)
			{
				TlPalette p = new TlPalette();
				p.Background	= Hex("#FFFFFF", bgOpacity);
				p.Border		= Hex("#D0D5DD");
				p.Divider		= Hex("#E4E7EC");
				p.Track			= Hex("#E4E7EC");
				p.Text			= Hex("#344054");
				p.Strong		= Hex("#101828");
				p.Muted			= Hex("#667085");
				p.Accent		= Hex("#2F6FDB");
				p.Positive		= Hex("#1A7F37");
				p.Negative		= Hex("#C4320A");
				p.Warning		= Hex("#B54708");
				p.Danger		= Hex("#D92D20");
				p.HeaderDanger	= Hex("#FEE4E2");
				p.HeaderPositive= Hex("#DCFAE6");
				p.HeaderNeutral	= Hex("#E0EAFF");
				return p;
			}

			public static TlPalette Build(icPATL_Theme theme, Brush customBg, Brush customText, int opacityPercent)
			{
				double op = Math.Max(0, Math.Min(100, opacityPercent)) / 100.0;

				if (theme == icPATL_Theme.Light) return Light(op);
				if (theme == icPATL_Theme.Dark) return Dark(op);

				// Custom: pick dark/light accents based on the background's brightness
				bool darkBg = true;
				SolidColorBrush sb = customBg as SolidColorBrush;
				if (sb != null)
					darkBg = (0.299 * sb.Color.R + 0.587 * sb.Color.G + 0.114 * sb.Color.B) / 255.0 < 0.5;

				TlPalette p = darkBg ? Dark(op) : Light(op);

				if (customBg != null)
				{
					Brush bg = customBg.Clone();
					bg.Opacity = op;
					bg.Freeze();
					p.Background = bg;
				}

				if (customText != null)
				{
					p.Text = customText;
					p.Strong = customText;

					SolidColorBrush st = customText as SolidColorBrush;
					if (st != null)
					{
						SolidColorBrush muted = new SolidColorBrush(st.Color);
						muted.Opacity = 0.65;
						muted.Freeze();
						p.Muted = muted;
					}
				}

				return p;
			}
		}

		// -------------------------------------------------------------
		// NATIVE WPF DASHBOARD (all methods below run on the UI thread)
		// -------------------------------------------------------------
		private void RequestUiRefresh()
		{
			if (isTerminated) return;
			ChartControl cc = ChartControl;
			if (cc != null)
				cc.Dispatcher.InvokeAsync(new Action(RefreshUi));
		}

		private void CreateWpfDashboard()
		{
			if (isTerminated || ChartControl == null) return;

			DisposeWpfDashboard();

			chartGrid = ChartControl.Parent as Grid;
			if (chartGrid == null) return;

			if (pal == null) pal = TlPalette.Build(DashboardTheme, BoxColor, FontColor, BoxOpacity);
			uiFont = new FontFamily("Segoe UI");
			lastBodyKey = null;

			// Header: title on the left, status dot + text on the right. Click to collapse.
			headerTitle = MakeText("", pal.Strong, FontWeights.Medium, DashboardFontSize + 0.5);

			statusDot = new System.Windows.Shapes.Ellipse
			{
				Width = 7,
				Height = 7,
				Margin = new Thickness(0, 0, 5, 0),
				VerticalAlignment = VerticalAlignment.Center
			};
			statusText = MakeText("", pal.Muted, FontWeights.Normal, DashboardFontSize - 1);
			statusText.VerticalAlignment = VerticalAlignment.Center;

			StackPanel statusPanel = new StackPanel
			{
				Orientation = System.Windows.Controls.Orientation.Horizontal,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(10, 0, 0, 0)
			};
			statusPanel.Children.Add(statusDot);
			statusPanel.Children.Add(statusText);

			Grid headerGrid = new Grid();
			headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			Grid.SetColumn(statusPanel, 1);
			headerGrid.Children.Add(headerTitle);
			headerGrid.Children.Add(statusPanel);

			headerBorder = new Border
			{
				Background = Brushes.Transparent,
				Padding = new Thickness(10, 7, 10, 7),
				Cursor = System.Windows.Input.Cursors.Hand,
				ToolTip = "Click to collapse or expand",
				Child = headerGrid
			};
			headerBorder.MouseLeftButtonDown += OnHeaderMouseDown;

			bodyPanel = new StackPanel { IsHitTestVisible = false };

			StackPanel outer = new StackPanel();
			outer.Children.Add(headerBorder);
			outer.Children.Add(bodyPanel);

			dashboardBorder = new Border
			{
				Background = pal.Background,
				BorderBrush = pal.Border,
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(6),
				Width = DashboardWidth(),
				SnapsToDevicePixels = true,
				Child = outer
			};
			TextOptions.SetTextFormattingMode(dashboardBorder, TextFormattingMode.Display);

			centerText = new TextBlock
			{
				FontFamily = uiFont,
				FontWeight = FontWeights.SemiBold,
				TextAlignment = TextAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				IsHitTestVisible = false,
				Visibility = Visibility.Collapsed
			};

			// Dark "filter screen" over the chart area, shown when trading is blocked.
			// Clicks pass through, so the chart stays usable underneath.
			SolidColorBrush dimBrush = new SolidColorBrush(Colors.Black);
			dimBrush.Opacity = Math.Max(10, Math.Min(95, DimOpacity)) / 100.0;
			dimBrush.Freeze();
			dimOverlay = new Border
			{
				Background = dimBrush,
				IsHitTestVisible = false,
				Visibility = Visibility.Collapsed
			};
			Grid.SetRow(dimOverlay, Grid.GetRow(ChartControl));
			Grid.SetColumn(dimOverlay, Grid.GetColumn(ChartControl));
			Grid.SetRowSpan(dimOverlay, Grid.GetRowSpan(ChartControl));
			Grid.SetColumnSpan(dimOverlay, Grid.GetColumnSpan(ChartControl));

			ApplyDashboardAlignment();
			chartGrid.Children.Add(dimOverlay);			// bottom: dims the chart
			chartGrid.Children.Add(centerText);			// message on top of the dim
			chartGrid.Children.Add(dashboardBorder);	// dashboard stays bright on top

			// Hide the overlay when this chart's tab isn't the selected one
			chartWindow = Window.GetWindow(ChartControl.Parent) as NinjaTrader.Gui.Chart.Chart;
			if (chartWindow != null && chartWindow.MainTabControl != null)
				chartWindow.MainTabControl.SelectionChanged += OnTabSelectionChanged;

			// Wall-clock refresh: countdown and window logic work even with no ticks
			uiTimer = new DispatcherTimer(DispatcherPriority.Background, ChartControl.Dispatcher);
			uiTimer.Interval = TimeSpan.FromSeconds(1);
			uiTimer.Tick += OnUiTimerTick;
			uiTimer.Start();

			RefreshUi();
		}

		private void DisposeWpfDashboard()
		{
			if (uiTimer != null)
			{
				uiTimer.Stop();
				uiTimer.Tick -= OnUiTimerTick;
				uiTimer = null;
			}

			if (chartWindow != null && chartWindow.MainTabControl != null)
				chartWindow.MainTabControl.SelectionChanged -= OnTabSelectionChanged;
			chartWindow = null;

			if (headerBorder != null)
				headerBorder.MouseLeftButtonDown -= OnHeaderMouseDown;

			if (chartGrid != null)
			{
				if (dashboardBorder != null) chartGrid.Children.Remove(dashboardBorder);
				if (centerText != null) chartGrid.Children.Remove(centerText);
				if (dimOverlay != null) chartGrid.Children.Remove(dimOverlay);
			}

			chartGrid = null;
			dimOverlay = null;
			dashboardBorder = null;
			headerBorder = null;
			headerTitle = null;
			statusText = null;
			statusDot = null;
			bodyPanel = null;
			centerText = null;
			lastBodyKey = null;
		}

		private void OnUiTimerTick(object sender, EventArgs e)
		{
			// Safety net: if the terminate-time cleanup could not reach this chart, stop here
			if (isTerminated)
			{
				DisposeWpfDashboard();
				return;
			}

			RefreshUi();
		}

		private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			RefreshUi();
		}

		private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
		{
			isCompact = !isCompact;
			e.Handled = true;
			RefreshUi();
		}

		private bool IsChartTabSelected()
		{
			try
			{
				if (chartWindow == null || chartWindow.MainTabControl == null) return true;
				TabItem selected = chartWindow.MainTabControl.SelectedItem as TabItem;
				if (selected == null) return true;
				ChartTab tab = selected.Content as ChartTab;
				return tab == null || tab.ChartControl == ChartControl;
			}
			catch
			{
				return true;
			}
		}

		private double DashboardWidth()
		{
			return Math.Max(210.0, DashboardFontSize * 19.0);
		}

		private void ApplyDashboardAlignment()
		{
			if (dashboardBorder == null) return;

			switch (BoxLocation)
			{
				case icPATL_BoxPosition.TopLeft:
					dashboardBorder.HorizontalAlignment = HorizontalAlignment.Left;
					dashboardBorder.VerticalAlignment = VerticalAlignment.Top;
					dashboardBorder.Margin = new Thickness(10, 10, 0, 0);
					break;
				case icPATL_BoxPosition.TopCenter:
					dashboardBorder.HorizontalAlignment = HorizontalAlignment.Center;
					dashboardBorder.VerticalAlignment = VerticalAlignment.Top;
					dashboardBorder.Margin = new Thickness(0, 10, 0, 0);
					break;
				case icPATL_BoxPosition.TopRight:
					dashboardBorder.HorizontalAlignment = HorizontalAlignment.Right;
					dashboardBorder.VerticalAlignment = VerticalAlignment.Top;
					dashboardBorder.Margin = new Thickness(0, 10, 55, 0);
					break;
				case icPATL_BoxPosition.BottomLeft:
					dashboardBorder.HorizontalAlignment = HorizontalAlignment.Left;
					dashboardBorder.VerticalAlignment = VerticalAlignment.Bottom;
					dashboardBorder.Margin = new Thickness(10, 0, 0, 25);
					break;
				case icPATL_BoxPosition.BottomCenter:
					dashboardBorder.HorizontalAlignment = HorizontalAlignment.Center;
					dashboardBorder.VerticalAlignment = VerticalAlignment.Bottom;
					dashboardBorder.Margin = new Thickness(0, 0, 0, 25);
					break;
				case icPATL_BoxPosition.BottomRight:
					dashboardBorder.HorizontalAlignment = HorizontalAlignment.Right;
					dashboardBorder.VerticalAlignment = VerticalAlignment.Bottom;
					dashboardBorder.Margin = new Thickness(0, 0, 55, 25);
					break;
			}
		}

		// --- Small WPF builders ---
		private TextBlock MakeText(string text, Brush fg, FontWeight weight, double size)
		{
			return new TextBlock
			{
				Text = text,
				Foreground = fg,
				FontWeight = weight,
				FontSize = size,
				FontFamily = uiFont,
				TextTrimming = TextTrimming.CharacterEllipsis
			};
		}

		private StackPanel MakeSection(string title)
		{
			StackPanel s = new StackPanel { Margin = new Thickness(10, 7, 10, 7) };
			TextBlock t = MakeText(title, pal.Muted, FontWeights.Normal, DashboardFontSize - 1);
			t.Margin = new Thickness(0, 0, 0, 3);
			s.Children.Add(t);
			return s;
		}

		private UIElement MakeDivider()
		{
			return new Border { Height = 1, Background = pal.Divider, SnapsToDevicePixels = true };
		}

		private UIElement MakeRow(string label, string value, Brush labelBrush, Brush valueBrush, double size, FontWeight valueWeight)
		{
			Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
			g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

			TextBlock l = MakeText(label, labelBrush, FontWeights.Normal, size);
			TextBlock v = MakeText(value, valueBrush, valueWeight, size);
			v.HorizontalAlignment = HorizontalAlignment.Right;
			v.Margin = new Thickness(12, 0, 0, 0);
			Grid.SetColumn(v, 1);

			g.Children.Add(l);
			g.Children.Add(v);
			return g;
		}

		private UIElement MakeRow(string label, string value, Brush valueBrush)
		{
			return MakeRow(label, value, pal.Text, valueBrush, DashboardFontSize, FontWeights.Medium);
		}

		private UIElement MakeCaption(string label, string value)
		{
			UIElement row = MakeRow(label, value, pal.Muted, pal.Muted, DashboardFontSize - 1, FontWeights.Normal);
			((FrameworkElement)row).Margin = new Thickness(0, 5, 0, 0);
			return row;
		}

		private UIElement MakeBar(double fraction, Brush fill)
		{
			double f = Math.Max(0.0, Math.Min(1.0, fraction));

			Grid g = new Grid { Height = 3, Margin = new Thickness(0, 3, 0, 3) };
			g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
			g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0 - f, GridUnitType.Star) });

			Border track = new Border { Background = pal.Track, CornerRadius = new CornerRadius(1.5) };
			Grid.SetColumnSpan(track, 2);
			Border bar = new Border { Background = fill, CornerRadius = new CornerRadius(1.5) };

			g.Children.Add(track);
			if (f > 0) g.Children.Add(bar);
			return g;
		}

		private Brush CountBrush(int value, int max)
		{
			if (value >= max) return pal.Danger;
			if (max > 1 && value == max - 1) return pal.Warning;
			return pal.Strong;
		}

		private Brush PnlBrush(double value)
		{
			if (value > 0.005) return pal.Positive;
			if (value < -0.005) return pal.Negative;
			return pal.Strong;
		}

		private string HeaderTitle()
		{
			return Instrument.MasterInstrument.Name + " \u00B7 " + AccountName;
		}

		private void SetHeader(string title, string status, Brush dotBrush, Brush statusBrush, Brush headerBg)
		{
			headerTitle.Text = title;
			statusText.Text = status;
			statusText.Foreground = statusBrush;
			statusDot.Fill = dotBrush;
			headerBorder.Background = headerBg;
		}

		// -------------------------------------------------------------
		// MAIN UI REFRESH
		// -------------------------------------------------------------
		private void RefreshUi()
		{
			if (isTerminated) return;
			if (dashboardBorder == null || bodyPanel == null || centerText == null) return;

			bool tabSelected = IsChartTabSelected();
			dashboardBorder.Visibility = (ShowDashboardBox && tabSelected) ? Visibility.Visible : Visibility.Collapsed;
			dashboardBorder.Width = DashboardWidth();
			ApplyDashboardAlignment();
			if (dimOverlay != null) dimOverlay.Visibility = Visibility.Collapsed;

			// --- Account missing ---
			if (myAccount == null)
			{
				SetHeader("Account '" + AccountName + "' not found", "Check settings", pal.Danger, pal.Danger, pal.HeaderDanger);
				bodyPanel.Children.Clear();
				lastBodyKey = null;
				centerText.Visibility = Visibility.Collapsed;
				return;
			}

			// --- Still loading ---
			if (!isReady)
			{
				SetHeader(HeaderTitle(), "Loading\u2026", pal.Muted, pal.Muted, Brushes.Transparent);
				bodyPanel.Children.Clear();
				lastBodyKey = null;
				centerText.Visibility = Visibility.Collapsed;
				return;
			}

			// --- Re-evaluate (picks up unrealized P&L moves and late account P&L updates) ---
			double accR, accU;
			ReadAccountPnl(out accR, out accU);

			bool triggered, limit;
			LimitKind kind;
			DateTime coolUntil;
			int trades, wins, losses;
			double realized, unrealized, lossUsed;
			string warning;

			lock (stateLock)
			{
				triggered = EvaluateLimitsLocked(accR, accU);
				limit = limitReached;
				kind = limitKind;
				coolUntil = cooldownUntil;
				trades = totalTrades;
				wins = consecWins;
				losses = consecLosses;
				realized = displayRealized;
				unrealized = displayUnrealized;
				lossUsed = displayLossUsed;
				warning = displayWarning;
			}

			if (triggered) OnLimitTriggered();

			SweepBlockedOrders();

			// --- Trade window ---
			TimeSpan timeUntilStart = TimeSpan.Zero;
			bool outsideWindow = EnableTradeWindow && IsOutsideWindow(GetWindowNow(), out timeUntilStart);
			string tz = TimeZoneLabel();
			string tzSuffix = tz.Length > 0 ? " " + tz : "";

			// --- Cooldown after a loss ---
			TimeSpan coolLeft = coolUntil - PlatformNow();
			bool inCooldown = CooldownMinutes > 0 && coolLeft > TimeSpan.Zero;
			string coolClock = inCooldown ? FormatClock(coolLeft) : "";

			// --- Early-warning sound (once per new warning) ---
			if (!limit && warning.Length > 0 && warning != lastWarningSounded)
			{
				// Only re-sound when the type of warning changes, not as the % ticks up
				string warningKey = warning.Contains("%") ? "loss%" : warning;
				if (warningKey != lastWarningSounded)
				{
					if (PlayWarningSound)
					{
						try { PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav"); }
						catch { }
					}
					lastWarningSounded = warningKey;
				}
			}
			else if (warning.Length == 0)
			{
				lastWarningSounded = "";
			}

			// --- Header / status ---
			if (limit)
			{
				if (kind == LimitKind.ProfitTarget)
					SetHeader(HeaderTitle(), "Target reached", pal.Positive, pal.Positive, pal.HeaderPositive);
				else if (kind == LimitKind.WinStreak)
					SetHeader(HeaderTitle(), "Win streak limit", pal.Positive, pal.Positive, pal.HeaderPositive);
				else if (kind == LimitKind.MaxTrades)
					SetHeader(HeaderTitle(), "Trade limit reached", pal.Accent, pal.Accent, pal.HeaderNeutral);
				else
					SetHeader(HeaderTitle(), "Stop trading", pal.Danger, pal.Danger, pal.HeaderDanger);
			}
			else if (inCooldown)
				SetHeader(HeaderTitle(), "Cooldown " + coolClock, pal.Accent, pal.Accent, pal.HeaderNeutral);
			else if (outsideWindow)
				SetHeader(HeaderTitle(), "Window closed", pal.Muted, pal.Muted, Brushes.Transparent);
			else if (warning.Length > 0)
				SetHeader(HeaderTitle(), warning, pal.Warning, pal.Warning, Brushes.Transparent);
			else
				SetHeader(HeaderTitle(), "Active", pal.Positive, pal.Text, Brushes.Transparent);

			// --- Footer info (computed first so it is part of the change check) ---
			string windowInfo;
			if (!EnableTradeWindow)
				windowInfo = "Window off";
			else if (outsideWindow)
				windowInfo = "Opens in " + FormatSpan(timeUntilStart);
			else
				windowInfo = "Open until " + EndTime.ToString("HH:mm") + tzSuffix;

			bool anyBlocking = AnyEntryBlockingEnabled();

			// --- Body (rebuilt only when a displayed value changes) ---
			bodyPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;

			if (!isCompact)
			{
				string bodyKey = string.Concat(
					trades.ToString(), "|", wins.ToString(), "|", losses.ToString(), "|",
					FormatMoney(realized), "|", FormatMoney(unrealized), "|", FormatMoney(lossUsed), "|",
					windowInfo, "|", anyBlocking ? "1" : "0");

				if (bodyKey != lastBodyKey)
				{
					lastBodyKey = bodyKey;
					bodyPanel.Children.Clear();

					if (ActiveLimitMode != icPATL_LimitMode.Dollar)
					{
						bodyPanel.Children.Add(MakeDivider());
						StackPanel s = MakeSection("Trades");
						Brush takenBrush = CountBrush(trades, MaxTotalTrades);
						s.Children.Add(MakeRow("Taken", trades + " / " + MaxTotalTrades, takenBrush));
						s.Children.Add(MakeBar((double)trades / MaxTotalTrades, takenBrush == pal.Strong ? pal.Accent : takenBrush));
						// Win streak only turns amber one-from-limit when that warning is enabled
						Brush winBrush = (!WarnBeforeWinLimit && wins < MaxConsecutiveWins) ? pal.Strong : CountBrush(wins, MaxConsecutiveWins);
						s.Children.Add(MakeRow("Win streak", wins + " / " + MaxConsecutiveWins, winBrush));
						s.Children.Add(MakeRow("Loss streak", losses + " / " + MaxConsecutiveLosses, CountBrush(losses, MaxConsecutiveLosses)));
						bodyPanel.Children.Add(s);
					}

					if (ActiveLimitMode != icPATL_LimitMode.Quantity)
					{
						bodyPanel.Children.Add(MakeDivider());
						StackPanel s = MakeSection("P&L");
						s.Children.Add(MakeRow(UseAccountWidePnl ? "Account realized" : "Realized", FormatMoney(realized), PnlBrush(realized)));
						// Open P&L is informational. Amber if closing now would breach the max daily loss.
						Brush openBrush = (unrealized < 0 && realized + unrealized <= -MaxDailyLoss) ? pal.Warning : PnlBrush(unrealized);
						s.Children.Add(MakeRow("Open", FormatMoney(unrealized), openBrush));

						double lossFrac = lossUsed / MaxDailyLoss;
						Brush lossBrush = lossFrac >= 1.0 ? pal.Danger
							: (WarnAtLossPercent > 0 && lossFrac * 100.0 >= WarnAtLossPercent) ? pal.Warning
							: pal.Accent;
						s.Children.Add(MakeCaption("Loss used", FormatMoneyShort(lossUsed) + " of " + FormatMoneyShort(MaxDailyLoss)));
						s.Children.Add(MakeBar(lossFrac, lossBrush));

						double targetProgress = Math.Max(0.0, realized);
						s.Children.Add(MakeCaption("Target", FormatMoneyShort(targetProgress) + " of " + FormatMoneyShort(DailyProfitTarget)));
						s.Children.Add(MakeBar(targetProgress / DailyProfitTarget, pal.Positive));
						bodyPanel.Children.Add(s);
					}

					// Footer: enforcement + window state
					bodyPanel.Children.Add(MakeDivider());
					UIElement footer = MakeRow(anyBlocking ? "Blocking on" : "Blocking off", windowInfo,
						pal.Muted, pal.Muted, DashboardFontSize - 1, FontWeights.Normal);
					((FrameworkElement)footer).Margin = new Thickness(10, 6, 10, 7);
					bodyPanel.Children.Add(footer);
				}
			}

			// --- Centre message ---
			if (limit)
			{
				switch (kind)
				{
					case LimitKind.ProfitTarget:
						centerText.Text = "DAILY TARGET REACHED\nGreat job \u2014 lock it in and call it a day";
						centerText.Foreground = pal.Positive;
						break;
					case LimitKind.WinStreak:
						centerText.Text = "WIN STREAK LIMIT REACHED\nGreat session \u2014 protect your gains";
						centerText.Foreground = pal.Positive;
						break;
					case LimitKind.MaxTrades:
						centerText.Text = "TRADE LIMIT REACHED\nThat's all your trades for today";
						centerText.Foreground = pal.Accent;
						break;
					default:
						centerText.Text = "DAILY LIMIT REACHED\nSTEP AWAY FROM THE SCREENS";
						centerText.Foreground = pal.Danger;
						break;
				}
				centerText.FontSize = 40;
				centerText.Visibility = tabSelected ? Visibility.Visible : Visibility.Collapsed;

				if (dimOverlay != null && DimChartOnLimit && tabSelected)
					dimOverlay.Visibility = Visibility.Visible;
			}
			else if (inCooldown)
			{
				centerText.Text = "COOLDOWN\nTake a breath \u2014 back in " + coolClock;
				centerText.Foreground = pal.Accent;
				centerText.FontSize = 34;
				centerText.Visibility = tabSelected ? Visibility.Visible : Visibility.Collapsed;

				if (dimOverlay != null && DimChartOnLimit && tabSelected)
					dimOverlay.Visibility = Visibility.Visible;
			}
			else if (outsideWindow && timeUntilStart.TotalMinutes > 1)
			{
				centerText.Text = string.Format("Outside trade window\nOpens at {0:HH:mm}{1} \u2014 in {2}",
					StartTime, tzSuffix, FormatSpan(timeUntilStart));
				centerText.Foreground = pal.Warning;
				centerText.FontSize = 30;
				centerText.Visibility = tabSelected ? Visibility.Visible : Visibility.Collapsed;

				if (dimOverlay != null && DimChartOnLimit && tabSelected)
					dimOverlay.Visibility = Visibility.Visible;
			}
			else
			{
				centerText.Visibility = Visibility.Collapsed;
			}
		}

		#region Properties
		// ---------------- 1. General ----------------
		[NinjaScriptProperty]
		[TypeConverter(typeof(NinjaTrader.NinjaScript.AccountNameConverter))]
		[Display(Name = "Account Name", Description = "The account whose fills are tracked (and whose new entry orders are blocked when blocking is on).", GroupName = "1. General", Order = 1)]
		public string AccountName { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Limit Mode", Description = "Quantity: trade count and streak limits only. Dollar: max daily loss and profit target only. Either: whichever limit is hit first.", GroupName = "1. General", Order = 2)]
		public icPATL_LimitMode ActiveLimitMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use Account-Wide PnL", Description = "Off: P&L from this instrument's fills this session. On: the account's total realized P&L (all instruments, NinjaTrader's reset schedule). Limits always use realized (closed-trade) P&L.", GroupName = "1. General", Order = 3)]
		public bool UseAccountWidePnl { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable Trade Window", Description = "Only allow trading between the start and end time.", GroupName = "1. General", Order = 4)]
		public bool EnableTradeWindow { get; set; }

		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name = "Start Time", Description = "When you are allowed to begin trading (in the window time zone).", GroupName = "1. General", Order = 5)]
		public DateTime StartTime { get; set; }

		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name = "End Time", Description = "When you must stop trading (in the window time zone).", GroupName = "1. General", Order = 6)]
		public DateTime EndTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Window Time Zone", Description = "Time zone the start and end times are in. Daylight saving is handled automatically. 'NinjaTraderSetting' uses your NinjaTrader time zone.", GroupName = "1. General", Order = 7)]
		public icPATL_WindowTimeZone WindowTimeZone { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Block Entries After Limit", Description = "Once the daily limit is hit, cancel new entry orders for the rest of the session. A market entry that fills before it can be cancelled is immediately offset (costs commission and slippage). Positions you already hold are never closed.", GroupName = "1. General", Order = 8)]
		public bool BlockEntriesAfterLimit { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Block Entries Outside Window", Description = "Outside the trade window, cancel new entry orders. A market entry that fills before it can be cancelled is immediately offset (costs commission and slippage). Positions you already hold are never closed. Needs the trade window enabled.", GroupName = "1. General", Order = 9)]
		public bool BlockEntriesOutsideWindow { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Block Entries During Cooldown", Description = "During the cooldown after a loss, cancel new entry orders. A market entry that fills before it can be cancelled is immediately offset (costs commission and slippage). Positions you already hold are never closed. Needs Cooldown Minutes above 0.", GroupName = "1. General", Order = 10)]
		public bool BlockEntriesDuringCooldown { get; set; }

		// ---------------- 2. Visuals (no NinjaScriptProperty: keeps the indicator label short) ----------------
		[Display(Name = "Show Dashboard Box", Description = "Toggle the dashboard on or off.", GroupName = "2. Visuals", Order = 1)]
		public bool ShowDashboardBox { get; set; }

		[Display(Name = "Theme", Description = "Dark, Light, or Custom (uses the custom colours below).", GroupName = "2. Visuals", Order = 2)]
		public icPATL_Theme DashboardTheme { get; set; }

		[Display(Name = "Box Location", Description = "Where the dashboard sits on the chart.", GroupName = "2. Visuals", Order = 3)]
		public icPATL_BoxPosition BoxLocation { get; set; }

		[Display(Name = "Start Compact", Description = "Start with only the header visible. Click the header to toggle.", GroupName = "2. Visuals", Order = 4)]
		public bool StartCompact { get; set; }

		[XmlIgnore]
		[Display(Name = "Custom Background", Description = "Dashboard background colour when Theme is Custom.", GroupName = "2. Visuals", Order = 5)]
		public Brush BoxColor { get; set; }

		[Browsable(false)]
		public string BoxColorSerializable
		{
			get { return Serialize.BrushToString(BoxColor); }
			set { BoxColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Custom Text", Description = "Dashboard text colour when Theme is Custom.", GroupName = "2. Visuals", Order = 6)]
		public Brush FontColor { get; set; }

		[Browsable(false)]
		public string FontColorSerializable
		{
			get { return Serialize.BrushToString(FontColor); }
			set { FontColor = Serialize.StringToBrush(value); }
		}

		[Range(9, 24)]
		[Display(Name = "Font Size", Description = "Base font size for the dashboard.", GroupName = "2. Visuals", Order = 7)]
		public int DashboardFontSize { get; set; }

		[Range(0, 100)]
		[Display(Name = "Background Opacity", Description = "Dashboard background opacity in % (text stays fully opaque).", GroupName = "2. Visuals", Order = 8)]
		public int BoxOpacity { get; set; }

		[Display(Name = "Dim Chart When Blocked", Description = "Darken the chart behind the on-chart message when the daily limit is reached, during a cooldown, or outside the trade window.", GroupName = "2. Visuals", Order = 9)]
		public bool DimChartOnLimit { get; set; }

		[Range(10, 95)]
		[Display(Name = "Dim Opacity", Description = "How dark the chart gets when trading is blocked (%).", GroupName = "2. Visuals", Order = 10)]
		public int DimOpacity { get; set; }

		// ---------------- 3. Offsets & Thresholds ----------------
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max Total Trades", Description = "Maximum completed trades allowed per session.", GroupName = "3. Offsets & Thresholds", Order = 1)]
		public int MaxTotalTrades { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max Consecutive Wins", Description = "Stop trading after this many wins in a row.", GroupName = "3. Offsets & Thresholds", Order = 2)]
		public int MaxConsecutiveWins { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max Consecutive Losses", Description = "Stop trading after this many losses in a row.", GroupName = "3. Offsets & Thresholds", Order = 3)]
		public int MaxConsecutiveLosses { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Max Daily Loss ($)", Description = "Stop trading once realized losses reach this amount.", GroupName = "3. Offsets & Thresholds", Order = 4)]
		public double MaxDailyLoss { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Daily Profit Target ($)", Description = "Stop trading once realized profit reaches this amount.", GroupName = "3. Offsets & Thresholds", Order = 5)]
		public double DailyProfitTarget { get; set; }

		[NinjaScriptProperty]
		[Range(0, 240)]
		[Display(Name = "Cooldown Minutes", Description = "Pause for this many minutes after each losing trade. Shows a countdown and dims the chart. 0 = off.", GroupName = "3. Offsets & Thresholds", Order = 6)]
		public int CooldownMinutes { get; set; }

		// ---------------- 4. Alerts ----------------
		[NinjaScriptProperty]
		[Display(Name = "Warn One From Limit", Description = "Amber warning when one trade or one loss away from a quantity limit.", GroupName = "4. Alerts", Order = 1)]
		public bool WarnOneFromLimit { get; set; }

		[Display(Name = "Warn Before Win Limit", Description = "Also warn (amber + sound) when one win away from the max consecutive wins.", GroupName = "4. Alerts", Order = 2)]
		public bool WarnBeforeWinLimit { get; set; }

		[NinjaScriptProperty]
		[Range(0, 99)]
		[Display(Name = "Warn At Loss %", Description = "Amber warning when this % of the max daily loss is used. 0 = off.", GroupName = "4. Alerts", Order = 3)]
		public int WarnAtLossPercent { get; set; }

		[Display(Name = "Play Warning Sound", Description = "Play a sound when a new early warning appears.", GroupName = "4. Alerts", Order = 4)]
		public bool PlayWarningSound { get; set; }
		#endregion
	}
}