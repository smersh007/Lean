using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QLNet;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Indicators;
using Path = System.IO.Path;

namespace QuantConnect.Algorithm.CSharp
{
    public class PinBallMarkovStrategy : QCAlgorithm
    {
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
        private List<string> _chartLog = new();


        private Identity _regionCodePlot;

        private const string transitionFileName = "PinBall_Transitions.csv";
        private const string regionTrackingFileName = "region_tracking.csv";
        private const double transitionPlotThreshold = 0.05;
        private string resultsFolder;
        private string transitionPath;


        private Symbol _symbol;
        private ExponentialMovingAverage _emaST, _emaLT;
        private StandardDeviation _stdDevST, _stdDevLT;
        private CrossDurationBucketIndicator _xDur;

        private string _previousState = null;

        // Volatility config (tune these)
        private decimal _volLow = 0.015m;   // 1.5%
        private decimal _volHigh = 0.035m;  // 3.5%

        // --- Volatility regime (ATR% z-score) ---
        private AverageTrueRange _atr;
        private SimpleMovingAverage _volSma;       // mean of ATR%
        private StandardDeviation _volStd;         // stdev of ATR%
        private int _volLookback = 30;            // ~1 year of daily bars




        private Dictionary<(string from, string to), int> _transitions = new();
        private Dictionary<string, Dictionary<string, double>> tmx;

        private bool _trainingMode = true; // Set to false to switch to trading mode

        private Identity _pricePlot;
        private Identity _zL_ST, _zU_ST, _zL_LT, _zU_LT;

        private Date _InSampleStartDate = new Date(1, 1, 2015);
        private Date _InSampleEndDate = new Date(1, 1, 2023);
        private Date _OutOfSampleEndDate = new Date(1, 8, 2025);

        private Date _startDate;
        private Date _endDate;



        public override void Initialize()
        {
            resultsFolder = Path.Combine(Environment.CurrentDirectory, "Results");
            Directory.CreateDirectory(resultsFolder);

            transitionPath = Path.Combine(resultsFolder, transitionFileName);


            if (_trainingMode)
            {
                _startDate = _InSampleStartDate;
                _endDate  = _InSampleEndDate;
            }
            else
            {
                _startDate = _InSampleEndDate;
                _endDate = _OutOfSampleEndDate;
            }

            SetStartDate(_startDate);
            SetEndDate(_endDate);

            SetCash(100000);

            _regionCodePlot = new Identity("RegionCode");
            PlotIndicator("RegionTracking", _regionCodePlot);



            _symbol = AddEquity("BOIL", Resolution.Daily).Symbol;

            _atr = ATR(_symbol, 14, MovingAverageType.Wilders, Resolution.Daily);
            _volSma = new SimpleMovingAverage("VolSMA", _volLookback);
            _volStd = new StandardDeviation("VolSTD", _volLookback);


            _emaST = EMA(_symbol, 20, Resolution.Daily);
            _emaLT = EMA(_symbol, 100, Resolution.Daily);

            _stdDevST = STD(_symbol, 20, Resolution.Daily);
            _stdDevLT = STD(_symbol, 100, Resolution.Daily);

            _pricePlot = new Identity("Price");
            _zL_ST = new Identity("ZScoreL_ST");
            _zU_ST = new Identity("ZScoreU_ST");
            _zL_LT = new Identity("ZScoreL_LT");
            _zU_LT = new Identity("ZScoreU_LT");

            PlotIndicator("BollingerBands", _emaST, _emaLT, _pricePlot, _zL_ST, _zU_ST, _zL_LT, _zU_LT);


            _xDur = new CrossDurationBucketIndicator("XDur", _emaST, _emaLT, 0m);
            RegisterIndicator(_symbol, _xDur, Resolution.Daily);

            SetWarmUp(TimeSpan.FromDays(120));

            if (!_trainingMode)
            {
                tmx = LoadMarkovTransitions(transitionPath);
            }
        }

        public override void OnData(Slice data)
        {
            if (IsWarmingUp) return;
           
            if (!_xDur.IsReady) return;

            var priceNow = Securities[_symbol].Price;

            if (_atr.IsReady && priceNow > 0)
            {
                var volPct = _atr.Current.Value / priceNow;           // ATR / Price
                _volSma.Update(Time, volPct);
                _volStd.Update(Time, volPct);
            }

            if (!_atr.IsReady || !_volSma.IsReady || !_volStd.IsReady) return;

            //           Debug($"PriceRegion: {GetPriceRegion()}, Price: {Securities[_symbol].Price:F2}");

            var priceRegion = GetPriceRegion();
            if (_regionCodeMap.TryGetValue(priceRegion, out int value))
                _regionCodePlot.Update(Time, value);


            _pricePlot.Update(Time, Securities[_symbol].Price);
            _zL_ST.Update(Time, GetZScoreLowerST());
            _zU_ST.Update(Time, GetZScoreUpperST());
            _zL_LT.Update(Time, GetZScoreLowerLT());
            _zU_LT.Update(Time, GetZScoreUpperLT());


            string trendLabel = _xDur.GetStateLabel();

            string volLabel = GetVolLabel();
            string currentState = $"{trendLabel}_{volLabel}_{priceRegion}";

            _chartLog.Add(
                $"{Time:yyyy-MM-dd}," +
                $"{Securities[_symbol].Price:F2}," +
                $"{priceRegion}," +
                $"{_regionCodeMap[priceRegion]}," +
                $"{volLabel}," +
                $"{GetZScoreLowerST():F2}," +
                $"{GetZScoreUpperST():F2}," +
                $"{GetZScoreLowerLT():F2}," +
                $"{GetZScoreUpperLT():F2}"
            );


            if (_trainingMode)
            {
                // --- Training mode ---

                if (_previousState != null)
                {
                    var key = (_previousState, currentState);
                    if (!_transitions.ContainsKey(key))
                        _transitions[key] = 0;
                    _transitions[key]++;
                }

                _previousState = currentState;

            }
            else
            {
                // --- Trading mode ---
                if (tmx != null && tmx.TryGetValue(currentState, out Dictionary<string, double> transitions))
                {
                    var bestTarget = transitions.OrderByDescending(x => x.Value).First().Key;

                    var targetPrice = ResolvePriceLevel(bestTarget);
                    Debug($"From {currentState} → {bestTarget} → target price: {targetPrice:F2}");

                    // Example action placeholder:
                    // PlaceLimitOrder(_symbol, quantity, targetPrice);
                }
            }
        }

        public override void OnEndOfAlgorithm()
        {
            if (_trainingMode)
            {
                var transitionPath = Path.Combine(resultsFolder, transitionFileName);
                SaveTransitions(transitionPath, _transitions);

                var tmx = MakeTransitionMatrix(_transitions);

                PlotTransitions(tmx, transitionPlotThreshold);

                var regionTrackingPath = Path.Combine(resultsFolder, regionTrackingFileName);
                File.WriteAllLines(regionTrackingPath, new[] { "Date,Price,Region,RegionCode,VolRegime,ZScoreL_ST,ZScoreU_ST,ZScoreL_LT,ZScoreU_LT" }.Concat(_chartLog));

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
            return MakeTransitionMatrixClip(transition);
            ///return MakeTransitionMatrixSimple(transition);
            //            return MakeTransitionMatrixSafe(transition);
        }

        private static Dictionary<string, Dictionary<string, double>> MakeTransitionMatrixSimple(IEnumerable<KeyValuePair<(string from, string to), int>> transition)
        {
            // Normalize transitions to probabilities
            var tmx = new Dictionary<string, Dictionary<string, double>>();

            foreach (var group in transition.GroupBy(kv => kv.Key.from))
            {
                double total = group.Sum(g => g.Value);
                string fromState = group.Key;

                tmx[fromState] = group.ToDictionary(
                    g => g.Key.to,
                    g => g.Value / total);
            }

            return tmx;
        }
        private static Dictionary<string, Dictionary<string, double>> MakeTransitionMatrixClip(IEnumerable<KeyValuePair<(string from, string to), int>> transition)
        {
            // Normalize transitions to probabilities
            var tmx = new Dictionary<string, Dictionary<string, double>>();

            foreach (var group in transition.GroupBy(kv => kv.Key.from))
            {
                double total = group.Sum(g => g.Value);
                string fromState = group.Key;

                tmx[fromState] = group.ToDictionary(
                    g => g.Key.to,
                    g => total > 3 ? g.Value / total : 0.05);// clip
            }

            return tmx;
        }

        private static Dictionary<string, Dictionary<string, double>> MakeTransitionMatrixSafe(IEnumerable<KeyValuePair<(string from, string to), int>> transition)
        {
            var tmx = new Dictionary<string, Dictionary<string, double>>();
            var toStateUniverse = transition.Select(x => x.Key.to).Distinct().ToHashSet();

            const double k = 10.0; // sensitivity constant

            foreach (var group in transition.GroupBy(kv => kv.Key.from))
            {
                string fromState = group.Key;
                int N = group.Sum(g => g.Value); // total transitions from this state

                // Raw probabilities
                var raw = group.ToDictionary(g => g.Key.to, g => (double)g.Value / N);

                // Confidence weight
                double weight = N / (N + k);
                double uniformP = 1.0 / toStateUniverse.Count;

                // Blended probabilities
                var smoothed = new Dictionary<string, double>();
                foreach (var to in toStateUniverse)
                {
                    double rawP = raw.TryGetValue(to, out var val) ? val : 0.0;
                    smoothed[to] = weight * rawP + (1.0 - weight) * uniformP;
                }

                tmx[fromState] = smoothed;
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
                    tmx[from] = new Dictionary<string, double>();

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
            Func<string,string> clusterFill = grp => grp switch
            {
                "TrendUp"   => "#114C3D",
                "TrendDown" => "#53202A",
                "Flat"      => "#2E3552",
                _           => "#1B1F2A"
            };

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
                        if (double.IsNaN(prob) || double.IsInfinity(prob) || prob < transThreshold)
                            continue;

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
            return parts.Length >= 1 ? parts[index] : "Unknown";
        }


        private string GetPriceRegion()
        {
            var price = Securities[_symbol].Price;

            var zL_ST = GetZScoreLowerST();
            var zU_ST = GetZScoreUpperST();
            var zL_LT = GetZScoreLowerLT();
            var zU_LT = GetZScoreUpperLT();
            var emaST = _emaST.Current.Value;

            if (price < zL_LT)
                return "L2";
            if (price < zL_ST)
                return "L1";
            if (price < emaST)
                return "M2";
            if (price < zU_ST)
                return "M1";
            if (price < zU_LT)
                return "U1";

            return "U2";
        }

        private string GetVolLabel()
        {
            if (!_atr.IsReady || !_volSma.IsReady || !_volStd.IsReady) return "VolNA";

            var priceNow = (decimal)Securities[_symbol].Price;

            if (priceNow <= 0) return "VolNA";

            var volPct = _atr.Current.Value / priceNow;
            var denom = _volStd.Current.Value;
            if (denom == 0) return "VolNA"; // degenerate case

            var z = (volPct - _volSma.Current.Value) / denom;

            //return z >= 1.8m ? "VolHigh"
            //     : z <= -1.0m ? "VolLow"
            //     : "VolMed";
            return z >= 30.0m ? "VolHigh" : "VolLow"; //0.674m

        }




        private decimal ResolvePriceLevel(string state)
        {
            return state switch
            {
                "ZScoreU_ST" => GetZScoreUpperST(),
                "ZScoreL_ST" => GetZScoreLowerST(),
                "EMA_ST" => _emaST.Current.Value,
                "EMA_LT" => _emaLT.Current.Value,
                _ => 0,
            };
        }

        private decimal GetZScoreUpperST() => _emaST.Current.Value + (decimal)sigmas * _stdDevST.Current.Value;
        private decimal GetZScoreLowerST() => _emaST.Current.Value - (decimal)sigmas * _stdDevST.Current.Value;

        private decimal GetZScoreUpperLT() => _emaLT.Current.Value + (decimal)sigmas * _stdDevLT.Current.Value;
        private decimal GetZScoreLowerLT() => _emaLT.Current.Value - (decimal)sigmas * _stdDevLT.Current.Value;


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
