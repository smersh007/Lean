using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using QLNet;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using Path = System.IO.Path;

namespace QuantConnect.Algorithm.CSharp
{

    public class PinBallMarkovStrategy : QCAlgorithm
    {
        private bool _trainingMode = false; // Set to false to switch to trading mode


        string symbolFolder = $"D:\\Dev\\StockData\\Symbols";

        private readonly Dictionary<string, int> _regionCodeMap = new()
            {
                { "L2", 0 },
                { "L1", 1 },
                { "M2", 2 },
                { "M1", 3 },
                { "U1", 4 },
                { "U2", 5 }
            };

        private readonly Dictionary<Symbol, Identity> _regionPlots = [];

        private string transitionFileName = $"_PinBall_Transitions.csv";
        private const string regionTrackingFileName = "region_tracking.csv";
        private const double transitionPlotThreshold = 0.25;
        private const double minTrasitionProbability = 0.3;
        private string resultsFolder;
        private string transitionPath;

        private Dictionary<Symbol, SymbolData> _symbols;


        private Dictionary<string, Dictionary<string, double>> tmx;

        private readonly Date _InSampleStartDate = new(1, 1, 2015);
        private readonly Date _InSampleEndDate = new(1, 1, 2023);
        private readonly Date _OutOfSampleEndDate = new(1, 8, 2025);
        private readonly decimal NotionalAccountSize = 100000;
        private Date _startDate;
        private Date _endDate;

        private int _volLookback = 30;

        public void Debug(string sym, string message)
        {
            Debug($"{Time:yyyy-MM-dd} {sym}: {message}");
        }

        public override void Initialize()
        {
            var tickers = ReadFirstColumn(Path.Combine(symbolFolder, _trainingMode ? "ETFsAll_Cleaned.csv" : "Test1.csv"));
            var excludedTickers = ReadFirstColumn(Path.Combine(symbolFolder, "LeveragedETFs.csv"));
            var bannedTickers = ReadFirstColumn(Path.Combine(symbolFolder, "Banned.csv"));

            tickers = tickers.Where(t => !bannedTickers.Contains(t)).ToList();
            tickers = tickers.Where(t => !excludedTickers.Contains(t)).ToList();

            resultsFolder = Path.Combine(Environment.CurrentDirectory, "Results");
            Directory.CreateDirectory(resultsFolder);

            transitionPath = Path.Combine(resultsFolder, transitionFileName);


            if (_trainingMode)
            {
                _startDate = _InSampleStartDate;
                _endDate = _InSampleEndDate;
            }
            else
            {
                _startDate = _InSampleEndDate;
                _endDate = _OutOfSampleEndDate;
            }

            SetStartDate(_startDate);
            SetEndDate(_endDate);

            SetCash(NotionalAccountSize);

            _symbols = new Dictionary<Symbol, SymbolData>();
            foreach (var ticker in tickers)
            {
                var symbol = AddEquity(ticker, Resolution.Daily).Symbol;
                _symbols[symbol] = new SymbolData(symbol, this, _volLookback, [0.5m, 1.5m, 2]);

                var regionId = new Identity($"RegionCode_{symbol.Value}");
                _regionPlots[symbol] = regionId;
                PlotIndicator($"RegionTracking_{symbol.Value}", regionId);

            }

            SetWarmUp(TimeSpan.FromDays(120));

            if (!_trainingMode)
            {
                tmx = LoadMarkovTransitions(transitionPath);
            }
        }

        public override void OnData(Slice slice)
        {
            if (IsWarmingUp) return;

            foreach (var kvp in _symbols)
            {
                var sym = kvp.Key;
                var sd = kvp.Value;

                if (!sd.XDur.IsReady)
                {
                    Debug(sym.Value, "XDur warming up");
                    continue;
                }

                var currentPx = Securities[sym].Price;

                if (sd.Atr.IsReady && currentPx > 0)
                {
                    var volPct = sd.Atr.Current.Value / currentPx;           // ATR / Price
                    sd.VolSma.Update(Time, volPct);
                    sd.VolStd.Update(Time, volPct);
                }

                if (!sd.Atr.IsReady || !sd.VolSma.IsReady || !sd.VolStd.IsReady)
                {
                    Debug(sym.Value, $"Indicators warming up ATR:{sd.Atr.IsReady} SMA:{sd.VolSma.IsReady} STD:{sd.VolStd.IsReady}");
                    continue;
                }

      //     Debug($"PriceRegion: {GetPriceRegion()}, Price: {Securities[_symbol].Price:F2}");


                string trendLabel = sd.XDur.GetStateLabel();

                string volLabel = GetVolLabel(sym, sd);
                var zones = GetZoneStates(sd, (decimal)currentPx);

                //string currentState = $"{trendLabel}_{volLabel}_{priceRegion}";
                string currentState = $"{trendLabel}_{zones}";

                if (_trainingMode)
                {
                    // --- Training mode ---

                    if (sd.PreviousState != null)
                    {
                        var key = (sd.PreviousState, currentState);
                        if (!sd.Transitions.ContainsKey(key))
                            sd.Transitions[key] = 0;
                        sd.Transitions[key]++;
                    }

                    sd.PreviousState = currentState;

                }
                else
                {
                    // --- Trading mode ---
                    if (tmx != null && tmx.TryGetValue(currentState, out Dictionary<string, double> transitions))
                        if (Portfolio[sym].Invested)
                        {
                            if (Portfolio[sym].Quantity == 0)
                            {
                                sd.StopTicket?.Cancel();
                                sd.TpTicket?.Cancel();
                            }
                        }
                        else
                        {
                            var nextTransitions = transitions.Where(s => s.Key != currentState)
                                .OrderByDescending(x => x.Value)
                                .Where(s => s.Value > minTrasitionProbability).ToList();

                            foreach (var trans in nextTransitions)
                            {
                                if (trans.Key != null)
                                {
                                    var currentPrice = Securities[sym].Price;
                                    var targetPrice = GetZonePrice(trans.Key, sd);
                                    if (targetPrice < 0)
                                    {
                                        Debug(sym, "Error with target price");
                                    }

                                    if (targetPrice > currentPrice)
                                    {
                                        decimal atrRiskPerUnit = 2m * (decimal)sd.Atr.Current.Value;                 // $/share
                                        decimal reward = targetPrice - currentPrice;
                                        decimal priceMove = targetPrice / currentPrice;
                                        decimal rewardToRisk = reward / atrRiskPerUnit;

                                        if (rewardToRisk > 1.99m)
                                        {
                                            decimal maxDollarRisk = 0.01m * NotionalAccountSize;                             // 1% per trade
                                            decimal qty = maxDollarRisk / atrRiskPerUnit;                          // shares
                                            qty = qty < 1 ? 1 : qty;
                                            qty = (int)qty;

                                            //   var cashCapQty = Portfolio.Cash / (decimal)currentPrice;                    // don’t exceed cash
                                            decimal tradeValue = qty * (decimal)currentPrice;
                                            decimal dollarRisk = qty * atrRiskPerUnit;

                                            decimal maxTradeValue = 0.1m * NotionalAccountSize;

                                            if (tradeValue >  maxTradeValue)
                                            {
                                                qty = qty * maxTradeValue / tradeValue;
                                                tradeValue = qty * (decimal)currentPrice;
                                                dollarRisk = qty * atrRiskPerUnit;
                                                qty = qty < 1 ? 1 : qty;
                                                qty = (int)qty;
                                            }

                                            if (tradeValue <= 1.05m * maxTradeValue)
                                            {

                                                Debug(sym, $"From {currentState} Current Px:{currentPrice:F3} → Trans:{trans} → Target Px: {targetPrice:F3} Qty: {qty:F2} Value: {tradeValue:F1} RewardToRisk: {rewardToRisk:F2}");

                                                sd.EntryTicket = LimitOrder(sym, qty, (decimal)currentPrice); // marketable limit at current price

                                                sd.TargetPrice = targetPrice;

                                                break; // only take the first valid transition
                                            }
                                            else { }
                                        }
                                    }
                                }
                            }

                        }

                }
            }
        }

        public override void OnEndOfAlgorithm()
        {
            if (_trainingMode)
            {
                // Aggregate all transitions across symbols → one CSV + one graph
                var allTransitions = _symbols.Values.SelectMany(sd => sd.Transitions);
                SaveTransitions(transitionPath, allTransitions);

                var tmxAll = MakeTransitionMatrix(allTransitions);
                PlotTransitions(tmxAll, transitionPlotThreshold);

                var show_more = false;
                // Per-symbol region tracking CSVs
                //foreach (var sd in _symbols.Values)
                //{
                //    var path = Path.Combine(resultsFolder, $"{sd.Symbol.Value}_{regionTrackingFileName}");
                //    File.WriteAllLines(path,
                //        new[] { "Date,Price,Region,RegionCode,VolRegime,ZScoreL_ST,ZScoreU_ST,ZScoreL_LT,ZScoreU_LT" }
                //        .Concat(sd.ChartLog));

                //    if (show_more)
                //        Open(path);
                //}

                if (show_more)
                    Open(transitionPath);

            }
        }

        private static void Open(string Path)
        {
            // Windows-only: auto-open the CSV file using default handler (e.g., Excel or Notepad)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path,
                UseShellExecute = true // Required to launch with default app
            });
        }

        private static void SaveTransitions(string transitionPath, IEnumerable<KeyValuePair<(string from, string to), int>> transition)
        {
            var tmx = MakeTransitionMatrix(transition);


            // Output to CSV
            using (var writer = new StreamWriter(transitionPath))
            {
                writer.WriteLine("From,To,Probability");

                foreach (var from in tmx.Keys)
                    foreach (var to in tmx[from].Keys)
                        writer.WriteLine($"{from},{to},{tmx[from][to]:F4}");
            }
        }

        private static Dictionary<string, Dictionary<string, double>> MakeTransitionMatrix(IEnumerable<KeyValuePair<(string from, string to), int>> transition)
        {
            return MakeTransitionMatrixSimple(transition, 3);
        }

        private static Dictionary<string, Dictionary<string, double>> MakeTransitionMatrixSimple(IEnumerable<KeyValuePair<(string from, string to), int>> transition, int N)
        {

            // Normalize transitions to probabilities (merge duplicate 'to' keys first)
            var tmx = new Dictionary<string, Dictionary<string, double>>();

            foreach (var group in transition.GroupBy(kv => kv.Key.from))
            {
                string fromState = group.Key;
                // Merge duplicates: sum all counts for the same 'to'
                var totalsByTo = group
                            .GroupBy(g => g.Key.to)
                           .ToDictionary(g => g.Key, g => g.Sum(x => x.Value));

                double total = totalsByTo.Values.Sum();
                tmx[fromState] = total > 0
                    ? totalsByTo.ToDictionary(kv => kv.Key, kv => total > N ? kv.Value / total : 0.05)
                    : [];
            }

            return tmx;
        }

        private static Dictionary<string, Dictionary<string, double>> LoadMarkovTransitions(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            var tmx = new Dictionary<string, Dictionary<string, double>>();

            var lines = File.ReadAllLines(filePath);
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split(',');
                var from = parts[0].Trim();
                var to = parts[1].Trim();
                var prob = double.Parse(parts[2]);

                if (!tmx.ContainsKey(from))
                    tmx[from] = [];

                tmx[from][to] = prob;
            }

            return tmx;
        }

        private void PlotTransitions(Dictionary<string, Dictionary<string, double>> tmx, double transThreshold)
        {
            //var dotPath = Path.Combine(resultsFolder, "transition.dot");
            //var svgPath = Path.Combine(resultsFolder, "transition.svg");
            var dotPath = "transition.dot";
            var svgPath = $"transition_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.svg";

            // Semantic node palette (price region) - contrast tuned
            var regionColors = new Dictionary<string, string>
            {
                { "L1", "#228822" },
                { "L2", "#114411" },
                { "M1", "#885588" },
                { "M2", "#880088" },
                { "U1", "#884444" },
                { "U2", "#661111" },
            };

            // Trend cluster fills (backgrounds) - slightly lighter for contrast
            Func<string, string> clusterFill = grp => grp switch
            {
                "TrendUp" => "#114C3D",
                "TrendDown" => "#53202A",
                "Flat" => "#2E3552",
                _ => "#1B1F2A"
            };

            var _symbol = "ALL";

            using (var writer = new StreamWriter(dotPath))
            {
                writer.WriteLine("digraph MarkovTransitions {");
                writer.WriteLine("  rankdir=LR;");
                writer.WriteLine("  labelloc=\"t\";");
                writer.WriteLine($"  label=\"PinBall Markov Transition Diagram ({DateTime.Now}) ({_symbol}) {_startDate:yyyy-MM-dd}- {_endDate:yyyy-MM-dd}\";");
                writer.WriteLine("  fontcolor=\"#EAECEF\";");
                writer.WriteLine("  bgcolor=\"#111418\";");

                // Global defaults
                writer.WriteLine("  edge [fontsize=10, fontname=\"Segoe UI Bold\", fontcolor=\"#FFFFFF\", labelfontcolor=\"#FFFFFF\", arrowsize=0.9, arrowhead=vee];");
                writer.WriteLine("  node [shape=box, fontsize=10, style=\"rounded,filled\", fontcolor=\"#FFFFFF\", penwidth=1.8, color=\"#B0BEC5\"];");

                // Group states by trend regime for clusters
                var nodeGroups = new Dictionary<string, List<string>>();
                foreach (var from in tmx.Keys)
                {
                    foreach (var to in tmx[from].Keys)
                    {
                        foreach (var state in new[] { from, to })
                        {
                            var trend = GetComponentFromState(state, 0);
                            if (!nodeGroups.ContainsKey(trend))
                                nodeGroups[trend] = new List<string>();
                            if (!nodeGroups[trend].Contains(state))
                                nodeGroups[trend].Add(state);
                        }
                    }
                }

                // Write clusters with new fills
                foreach (var group in nodeGroups.Keys.OrderBy(k => k))
                {
                    string fillColor = clusterFill(group);

                    writer.WriteLine($"  subgraph cluster_{group} {{");
                    writer.WriteLine($"    label = \"{group}\";");
                    writer.WriteLine($"    style=filled;");
                    writer.WriteLine($"    color=\"#8C9BAA\";");
                    writer.WriteLine($"    fillcolor=\"{fillColor}\";");

                    foreach (var node in nodeGroups[group])
                    {
                        var region = GetComponentFromState(node, 3); // L1, M1, etc.
                        var nodeColor = regionColors.TryGetValue(region, out var c) ? c : "#374151";
                        writer.WriteLine($"    \"{node}\" [fillcolor=\"{nodeColor}\"];");
                    }

                    writer.WriteLine("  }");
                }

                // Edge styling by probability (color ramp + width + dashed for low p)
                foreach (var from in tmx.Keys)
                {
                    foreach (var to in tmx[from].Keys)
                    {
                        var prob = tmx[from][to];
                        if (!double.IsNaN(prob) && !double.IsInfinity(prob) && prob >= transThreshold)
                        {
                            string color =
                                                    prob >= 0.80 ? "#64FFDA" :
                                                    prob >= 0.60 ? "#4FC3F7" :
                                                    prob >= 0.40 ? "#81D4FA" :
                                                    prob >= 0.20 ? "#B3E5FC" :
                                                    prob >= 0.10 ? "#CFD8DC" :
                                                                   "#90A4AE";

                            string style = prob < 0.20 ? "dashed" : "solid";
                            if (prob < 0.20) color = "#B0BEC5"; // lighter dashed edges for visibility

                            double penWidth = Math.Min(1.0 + 5.0 * prob, 6.0);

                            writer.WriteLine($"  \"{from}\" -> \"{to}\" [label=\"{prob:F2}\", color=\"{color}\", style={style}, penwidth={penWidth:F1}];");
                        }
                    }
                }

                writer.WriteLine("}");
            }

            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dot",
                    Arguments = $"-Tsvg \"{dotPath}\" -o \"{svgPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            try
            {
                process.Start();
                process.WaitForExit();

                // Open SVG
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = svgPath,
                    UseShellExecute = true
                });
            }
            catch (Exception e)
            {
                Debug($"Graphviz rendering failed: {e.Message}");
            }
        }

        private static string GetComponentFromState(string state, int index)
        {
            var parts = state.Split('_');
            return (index >= 0 && index < parts.Length) ? parts[index] : "Unknown";
        }

        private string GetVolLabel(Symbol sym, SymbolData sd)
        {
            return "VolNA";


            if (!sd.Atr.IsReady || !sd.VolSma.IsReady || !sd.VolStd.IsReady) return "VolNA";

            var priceNow = Securities[sym].Price;
            if (priceNow <= 0) return "VolNA";

            var volPct = sd.Atr.Current.Value / priceNow;
            var denom = sd.VolStd.Current.Value;
            if (denom == 0) return "VolNA";

            var z = (volPct - sd.VolSma.Current.Value) / denom;
            return z >= 1.8m ? "VolHigh"
                 : z <= -1.0m ? "VolLow"
                 : "VolMed";
        }

        // target resolver using zones --- (Parses "..._Zx? _Zy?" and maps to  prices;
        private static decimal GetZonePrice(string state, SymbolData sd)
        {
            // state format now: "<Trend>_Z*?_Z*?"
            var parts = state.Split('_');
            if (parts.Length < 3)
                return -1;

            var zoneStateLT = parts[2]; // e.g., "Z2U", "Z0D"
            var zoneStateST = parts[3]; // e.g., "Z1U", "Z0D"

            (int zone, char dir) ParseZoneState(string z)
            {
                // expect: 'Z' <digit(s)> <U|D>
                if (string.IsNullOrWhiteSpace(z) || z[0] != 'Z')
                    return (0, 'U');

                int i = 1;
                int zone = 0;
                while (i < z.Length && char.IsDigit(z[i]))
                {
                    zone = zone * 10 + (z[i] - '0');
                    i++;
                }

                char dir = (i < z.Length && (z[i] == 'U' || z[i] == 'D')) ? z[i] : 'U';

                return (zone, dir);
            }

            if (sd.Symbol == "DRIP" && state == "TrendUp_ST_Z2U_Z3U")
            { }


            try
            {

                var lt = ParseZoneState(zoneStateLT);
                var st = ParseZoneState(zoneStateST);

                decimal sigmaLT = GetZoneSigma(sd, lt.zone);
                var pxLT = st.dir == 'U'
                    ? GetZScoreUpperLT(sd, (double)sigmaLT)
                    : GetZScoreLowerLT(sd, (double)sigmaLT);

                var sigmaST = GetZoneSigma(sd, st.zone);
                var pxST = st.dir == 'U'
                    ? GetZScoreUpperST(sd, (double)sigmaST)
                    : GetZScoreLowerST(sd, (double)sigmaST);


                return (pxLT + pxST) / 2;
            }
            catch (Exception ex)
            {

                throw;
            }

            static decimal GetZoneSigma(SymbolData sd, int zoneIndex)
            {
                return zoneIndex == 0 ? sd.ZoneSigmas[zoneIndex] / 2
                    : zoneIndex >= sd.ZoneSigmas.Count ? sd.ZoneSigmas[zoneIndex - 1]
                    : (sd.ZoneSigmas[zoneIndex - 1] + sd.ZoneSigmas[zoneIndex]) / 2;
            }
        }





        private static string GetRegionFromState(string state) => GetComponentFromState(state, 2);  // 0=Trend, 1=Vol, 2=Region

        private static string GetZoneStates(SymbolData sd, decimal px)
        {
            return
                GetZone(sd.EmaLT.Current.Value, sd.StdDevLT.Current.Value, px, sd)// LT
            + "_" + GetZone(sd.EmaST.Current.Value, sd.StdDevST.Current.Value, px, sd);// ST
        }

        private static string GetZone(decimal ema, decimal sdev, decimal price, SymbolData sd)
        {
            // Guard against divide-by-zero or invalid inputs
            if (sdev <= 0) return "Z0N"; // N = neutral (no deviation computable)

            var stretch = (price - ema) / sdev;     // signed z-score
            var absStretch = Math.Abs(stretch);     // magnitude only
            var direction = stretch >= 0 ? "U" : "D";

            // Find first threshold that the magnitude is below.
            // If none match, zone = count (the catch-all highest zone).
            int zone = 0;
            for (; zone < sd.ZoneSigmas.Count; zone++)
            {
                if (absStretch < sd.ZoneSigmas[zone]) break;
            }

            //if(zone == 0)
            //    return $"Z{zone}";
            return $"Z{zone}{direction}";
        }



        private static decimal GetEmaST(SymbolData sd)
            => sd.EmaST.Current.Value;

        private static decimal GetEmaLT(SymbolData sd)
            => sd.EmaLT.Current.Value;

        private static decimal GetZScoreUpperST(SymbolData sd, double sigmas)
            => GetEmaST(sd) + (decimal)sigmas * sd.StdDevST.Current.Value;

        private static decimal GetZScoreLowerST(SymbolData sd, double sigmas)
            => GetEmaST(sd) - (decimal)sigmas * sd.StdDevST.Current.Value;

        private static decimal GetZScoreUpperLT(SymbolData sd, double sigmas)
            => GetEmaLT(sd) + (decimal)sigmas * sd.StdDevLT.Current.Value;

        private static decimal GetZScoreLowerLT(SymbolData sd, double sigmas)
            => GetEmaLT(sd) - (decimal)sigmas * sd.StdDevLT.Current.Value;

        public static List<string> ReadFirstColumn(string path)
        {
            return File.ReadLines(path)
                       .Where(line => !string.IsNullOrWhiteSpace(line))
                       .Select(line => line.Split(',')[0].Trim())
                       .ToList();
        }


        // --- Per-symbol state ---
        public class SymbolData
        {
            public Symbol Symbol { get; }
            public ExponentialMovingAverage EmaST { get; }
            public ExponentialMovingAverage EmaLT { get; }
            public StandardDeviation StdDevST { get; }
            public StandardDeviation StdDevLT { get; }
            public CrossDurationBucketIndicator XDur { get; }
            public AverageTrueRange Atr { get; }
            public SimpleMovingAverage VolSma { get; }
            public StandardDeviation VolStd { get; }
            public string PreviousState { get; set; }
            public Dictionary<(string from, string to), int> Transitions { get; } = new();
            public OrderTicket EntryTicket { get; set; }
            public OrderTicket StopTicket { get; set; }
            public OrderTicket TpTicket { get; set; }
            public decimal? EntryFillPrice { get; set; }    // cached when entry fills
            public decimal TargetPrice { get; set; }

            // Ascending thresholds in sigmas, e.g. [0.5, 1.0, 2.0]
            public IReadOnlyList<decimal> ZoneSigmas { get; init; }

            public SymbolData(Symbol symbol, QCAlgorithm algo, int volLookback, IEnumerable<decimal> zoneSigmas)
            {
                Symbol = symbol;

                this.ZoneSigmas = (IReadOnlyList<decimal>)zoneSigmas;

                Atr = algo.ATR(symbol, 14, MovingAverageType.Wilders, Resolution.Daily);
                VolSma = new SimpleMovingAverage($"VolSMA_{symbol.Value}", volLookback);
                VolStd = new StandardDeviation($"VolSTD_{symbol.Value}", volLookback);

                EmaST = algo.EMA(symbol, 20, Resolution.Daily);
                EmaLT = algo.EMA(symbol, 100, Resolution.Daily);
                StdDevST = algo.STD(symbol, 20, Resolution.Daily);
                StdDevLT = algo.STD(symbol, 100, Resolution.Daily);

                XDur = new CrossDurationBucketIndicator($"XDur_{symbol.Value}", EmaST, EmaLT, 0m);
                algo.RegisterIndicator(symbol, XDur, Resolution.Daily);
            }


        }




        // Choose a target based on probability-weighted, ATR-adjusted expectancy
        private decimal ChooseBestTarget(Symbol sym, SymbolData sd, decimal currentPrice, IEnumerable<(decimal targetPrice, double probability)> candidates, double minProb = 0.25)
        {
            if (sd?.Atr == null || !sd.Atr.IsReady) return 0m;

            var scored = candidates
                .Where(c => c.probability >= minProb)
                .Select(c =>
                {
                    var expDollars = c.targetPrice - currentPrice;
                    var atrDollars = (decimal)sd.Atr.Current.Value;
                    var score = (double)(expDollars / atrDollars) * c.probability;
                    return (c.targetPrice, c.probability, expDollars, score);
                })
                .Where(x => x.expDollars > 0m)
                .OrderByDescending(x => x.score)
                .ToList();

            return scored.Count == 0 ? 0m : scored[0].targetPrice;
        }

        // Place a long entry with bracket exits (stop + two take-profits)

        // ===================== Order Management =====================

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (!orderEvent.Status.IsFill()) return;

            var sym = orderEvent.Symbol;
            if (!_symbols.TryGetValue(sym, out var sd) || sd == null) return;

            StringBuilder msg = new();

            if (orderEvent.Ticket != null)
            {
                msg.Append($"Order Event Sym:{orderEvent.Symbol} {orderEvent.Status} {orderEvent.Ticket.OrderType}  OrderId:{orderEvent.Ticket.OrderId} Px:{orderEvent.Ticket.AverageFillPrice} Qty:{orderEvent.Quantity}");

                if (orderEvent.Ticket.OrderId == sd.EntryTicket?.OrderId) // Entry filled → place stop + single TP
                {
                    msg.Append($" - Ent");

                    var filledQty = orderEvent.Ticket.QuantityFilled;
                    if (filledQty <= 0) return;

                    var avgFill = orderEvent.Ticket.AverageFillPrice;
                    var atrDollars = (decimal)sd.Atr.Current.Value;

                    // Stop below fill
                    var stopPrice = avgFill - 2m * atrDollars;

                    // Take-profit at planned target
                    var tpPrice = sd.TargetPrice;

                    sd.StopTicket = StopMarketOrder(sym, -filledQty, stopPrice);
                    sd.TpTicket = LimitOrder(sym, -filledQty, tpPrice);


                }
                else
                {
                    // Take-profit or stop filled → cleanup
                    if (orderEvent.Ticket.OrderId == sd.TpTicket?.OrderId)
                    {
                        msg.Append($" - TP");
                        if (Portfolio[sym].Quantity == 0)
                            sd.StopTicket?.Cancel();
                    }
                    if (orderEvent.Ticket.OrderId == sd.StopTicket?.OrderId)
                    {
                        msg.Append($" - SL");
                        if (Portfolio[sym].Quantity == 0)
                            sd.TpTicket?.Cancel();
                    }
                }
                Debug(sym, msg.ToString());
            }
        }








    }


    public class CrossDurationBucketIndicator : IndicatorBase<IndicatorDataPoint>
    {
        private readonly Indicator _emaST;
        private readonly Indicator _emaLT;
        private readonly decimal _epsilon;
        private int _sign;
        private int _count;

        public CrossDurationBucketIndicator(string name, Indicator emaST, Indicator emaLT, decimal epsilon = 0m)
            : base(name)
        {
            _emaST = emaST;
            _emaLT = emaLT;
            _epsilon = epsilon;
        }

        public override bool IsReady => _emaST.IsReady && _emaLT.IsReady;

        protected override decimal ComputeNextValue(IndicatorDataPoint input)
        {
            if (!IsReady) return 0m;

            var diff = _emaST.Current.Value - _emaLT.Current.Value;
            var newSign = Math.Abs(diff) <= _epsilon ? 0 : (diff > 0 ? 1 : -1);

            if (newSign == 0)
            {
                _sign = 0;
                _count = 0;
            }
            else if (newSign == _sign)
            {
                _count += 1;
            }
            else
            {
                _sign = newSign;
                _count = 1;
            }

            return _sign * _count;
        }

        public string GetStateLabel()
        {
            if (!IsReady) return "Unknown";

            int val = (int)Current.Value;

            if (val == 0) return "Flat";

            int abs = Math.Abs(val);
            //            string duration = abs <= 5 ? "ST" : abs <= 20 ? "MT" : "LT";
            string duration = abs <= 20 ? "ST" : "LT";
            string dir = val > 0 ? "TrendUp" : "TrendDown";

            return $"{dir}_{duration}";
        }
    }
}
