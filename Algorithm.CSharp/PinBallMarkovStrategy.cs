using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QLNet;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using Path = System.IO.Path;

namespace QuantConnect.Algorithm.CSharp
{

    public class PinBallMarkovStrategy : QCAlgorithm
    {
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

        private readonly double sigmas = 1.8;

        private readonly Dictionary<Symbol, Identity> _regionPlots = new();

        private const string transitionFileName = "_PinBall_Transitions.csv";
        private const string regionTrackingFileName = "region_tracking.csv";
        private const double transitionPlotThreshold = 0.25;
        private string resultsFolder;
        private string transitionPath;

        private Dictionary<Symbol, SymbolData> _symbols;

        private bool _trainingMode = true; // Set to false to switch to trading mode

        private Dictionary<string, Dictionary<string, double>> tmx;

        private Date _InSampleStartDate = new Date(1, 1, 2015);
        private Date _InSampleEndDate = new Date(1, 1, 2023);
        private Date _OutOfSampleEndDate = new Date(1, 8, 2025);

        private Date _startDate;
        private Date _endDate;

        private int _volLookback = 30;

        public override void Initialize()
        {
            var tickers = ReadFirstColumn(Path.Combine(symbolFolder, "ETFsAll_Cleaned.csv"));
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

            SetCash(100000);

            _symbols = new Dictionary<Symbol, SymbolData>();
            foreach (var ticker in tickers)
            {
                var symbol = AddEquity(ticker, Resolution.Daily).Symbol;
                _symbols[symbol] = new SymbolData(symbol, this, _volLookback, sigmas);

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

        public override void OnData(Slice data)
        {
            if (IsWarmingUp) return;

            foreach (var kvp in _symbols)
            {
                var sym = kvp.Key;
                var sd = kvp.Value;

                if (!sd.XDur.IsReady)
                {
                    Debug($"{Time:yyyy-MM-dd} {sym.Value}: XDur warming up");
                    continue;
                }

                var priceNow = Securities[sym].Price;

                if (sd.Atr.IsReady && priceNow > 0)
                {
                    var volPct = sd.Atr.Current.Value / priceNow;           // ATR / Price
                    sd.VolSma.Update(Time, volPct);
                    sd.VolStd.Update(Time, volPct);
                }

                if (!sd.Atr.IsReady || !sd.VolSma.IsReady || !sd.VolStd.IsReady)
                {
                    Debug($"{Time:yyyy-MM-dd} {sym.Value}: vol stats warming up ATR:{sd.Atr.IsReady} SMA:{sd.VolSma.IsReady} STD:{sd.VolStd.IsReady}");
                    continue;
                }

                //           Debug($"PriceRegion: {GetPriceRegion()}, Price: {Securities[_symbol].Price:F2}");

                var priceRegion = GetPriceRegion(sym, sd);
                if (_regionCodeMap.TryGetValue(priceRegion, out int value))
                    _regionPlots[sym].Update(Time, value);


                string trendLabel = sd.XDur.GetStateLabel();

                string volLabel = GetVolLabel(sym, sd);
                string currentState = $"{trendLabel}_{volLabel}_{priceRegion}";

                sd.ChartLog.Add(
                    $"{Time:yyyy-MM-dd}," +
                    $"{Securities[sym].Price:F2}," +
                    $"{priceRegion}," +
                    $"{_regionCodeMap[priceRegion]}," +
                    $"{volLabel}," +
                    $"{GetZScoreLowerST(sd):F2}," +
                    $"{GetZScoreUpperST(sd):F2}," +
                    $"{GetZScoreLowerLT(sd):F2}," +
                    $"{GetZScoreUpperLT(sd):F2}"
                );


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
                    {
                        var bestTarget = transitions.OrderByDescending(x => x.Value).First().Key;

                        var targetPrice = ResolvePriceLevel(bestTarget, sd);
                        Debug($"From {currentState} → {bestTarget} → target price: {targetPrice:F2}");

                        // Example action placeholder:
                        // PlaceLimitOrder(_symbol, quantity, targetPrice);
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

                // Per-symbol region tracking CSVs
                foreach (var sd in _symbols.Values)
                {
                    var path = Path.Combine(resultsFolder, $"{sd.Symbol.Value}_{regionTrackingFileName}");
                    File.WriteAllLines(path,
                        new[] { "Date,Price,Region,RegionCode,VolRegime,ZScoreL_ST,ZScoreU_ST,ZScoreL_LT,ZScoreU_LT" }
                        .Concat(sd.ChartLog));
                }


                //             Open(regionTrackingPath);

                //              Open(transitionPath);

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
            var svgPath = "transition.svg";

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
                writer.WriteLine($"  label=\"PinBall Markov Transition Diagram ({_symbol}) {_startDate:yyyy-MM-dd}- {_endDate:yyyy-MM-dd}\";");
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

        private string GetPriceRegion(Symbol sym, SymbolData sd)
        {
            var price = Securities[sym].Price;
            var emaST = sd.EmaST.Current.Value;
            var zL_ST = emaST - (decimal)sigmas * sd.StdDevST.Current.Value;
            var zU_ST = emaST + (decimal)sigmas * sd.StdDevST.Current.Value;
            var zL_LT = sd.EmaLT.Current.Value - (decimal)sigmas * sd.StdDevLT.Current.Value;
            var zU_LT = sd.EmaLT.Current.Value + (decimal)sigmas * sd.StdDevLT.Current.Value;

            if (price < zL_LT) return "L2";
            if (price < zL_ST) return "L1";
            if (price < emaST) return "M2";
            if (price < zU_ST) return "M1";
            if (price < zU_LT) return "U1";
            return "U2";
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

        private decimal ResolvePriceLevel(string state, SymbolData sd)
        {
            return state switch
            {
                "ZScoreU_ST" => GetZScoreUpperST(sd),
                "ZScoreL_ST" => GetZScoreLowerST(sd),
                "EMA_ST" => sd.EmaST.Current.Value,
                "EMA_LT" => sd.EmaLT.Current.Value,
                _ => 0,
            };
        }
        private decimal GetZScoreUpperST(SymbolData sd)
        {
            return sd.EmaST.Current.Value + (decimal)sigmas * sd.StdDevST.Current.Value;
        }

        private decimal GetZScoreLowerST(SymbolData sd)
        {
            return sd.EmaST.Current.Value - (decimal)sigmas * sd.StdDevST.Current.Value;
        }

        private decimal GetZScoreUpperLT(SymbolData sd)
        {
            return sd.EmaLT.Current.Value + (decimal)sigmas * sd.StdDevLT.Current.Value;
        }

        private decimal GetZScoreLowerLT(SymbolData sd)
        {
            return sd.EmaLT.Current.Value - (decimal)sigmas * sd.StdDevLT.Current.Value;
        }

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
            public List<string> ChartLog { get; } = new();
            public Dictionary<(string from, string to), int> Transitions { get; } = new();

            public SymbolData(Symbol symbol, QCAlgorithm algo, int volLookback, double sigmas)
            {
                Symbol = symbol;
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
