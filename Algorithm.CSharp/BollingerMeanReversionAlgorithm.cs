using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using static QLNet.Bond;
using static QuantConnect.Messages;

namespace QuantConnect.Algorithm.CSharp
{
    public class BollingerMeanReversionAlgorithm : QCAlgorithm, IAlgorithm
    {
        private decimal _buyZ, _sellZ1, _sellZ2;
        private decimal _maxPositionSize, _maxTotalCapital;
        private int _maxConcurrentPositions, _zScorePeriod, _maPeriod;
        private string _symbolFilePath;
        private decimal _priceAdjustmentThreshold;

        private Dictionary<Symbol, SymbolData> _symbols = new();
        private StreamWriter _ordersSw;

        public class ConfigParameters
        {
            public Parameters parameters { get; set; }
        }

        public class Parameters
        {
            public string BuyZ { get; set; }
            public string SellZ1 { get; set; }
            public string SellZ2 { get; set; }
            public string MaxPositionSize { get; set; }
            public string MaxConcurrentPositions { get; set; }
            public string MaxTotalCapital { get; set; }
            public string ZScorePeriod { get; set; }
            public string MaPeriod { get; set; }
            public string SymbolFilePath { get; set; }
            public string PriceAdjustmentThreshold { get; set; }
        }

        public override void Initialize()
        {
            SetStartDate(2020, 1, 1);
            SetEndDate(2026, 1, 1);
            SetCash(100000);

            Directory.CreateDirectory("./Reports");
            _ordersSw = new StreamWriter($"./Reports/Orders{DateTime.Now.ToFileTime()}.csv");
            _ordersSw.WriteLine("Time,Symbol,ZScore,Price,Position,Signal,Note");


            var configPath = Path.Combine(Directory.GetCurrentDirectory(), "BollingerMeanReversionAlgorithmConfig.json");
            var configContent = File.ReadAllText(configPath);
            var configParameters = JsonConvert.DeserializeObject<ConfigParameters>(configContent);
            var p = configParameters?.parameters;

            _buyZ = p?.BuyZ != null ? Convert.ToDecimal(p.BuyZ) : -2.0m;
            _sellZ1 = p?.SellZ1 != null ? Convert.ToDecimal(p.SellZ1) : 0.0m;
            _sellZ2 = p?.SellZ2 != null ? Convert.ToDecimal(p.SellZ2) : 2.0m;
            _maxPositionSize = p?.MaxPositionSize != null ? Convert.ToDecimal(p.MaxPositionSize) : 1000m;
            _maxTotalCapital = p?.MaxTotalCapital != null ? Convert.ToDecimal(p.MaxTotalCapital) : 20000m;
            _maxConcurrentPositions = p?.MaxConcurrentPositions != null ? Convert.ToInt32(p.MaxConcurrentPositions) : 20;
            _zScorePeriod = p?.ZScorePeriod != null ? Convert.ToInt32(p.ZScorePeriod) : 20;
            _maPeriod = p?.MaPeriod != null ? Convert.ToInt32(p.MaPeriod) : 100;
            _priceAdjustmentThreshold = p?.PriceAdjustmentThreshold != null ? Convert.ToDecimal(p.PriceAdjustmentThreshold) : 0.05m;
            _symbolFilePath = p?.SymbolFilePath ?? "symbols.csv";

            Debug("Loaded Parameters from config:");
            Debug($"buyZ: {_buyZ}, sellZ1: {_sellZ1}, sellZ2: {_sellZ2}");
            Debug($"maxPositionSize: {_maxPositionSize}, maxConcurrentPositions: {_maxConcurrentPositions}, maxTotalCapital: {_maxTotalCapital}, zScorePeriod: {_zScorePeriod}, maPeriod: {_maPeriod}, symbolFilePath: {_symbolFilePath}");

            var lines = File.ReadAllLines(_symbolFilePath);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var ticker = line.Trim();
                    var symbol = AddEquity(ticker, Resolution.Daily).Symbol;
                    var data = new SymbolData(symbol, _zScorePeriod);
                    _symbols[symbol] = data;
                    RegisterIndicator(symbol, data.EMA, Resolution.Daily);
                    RegisterIndicator(symbol, data.STD, Resolution.Daily);
                }
            }
        }
        public override void OnEndOfAlgorithm()
        {
            _ordersSw?.Close();
        }

        public override void OnData(Slice data)
        {
            foreach (var kvp in _symbols)
            {
                var symbol = kvp.Key;
                var sd = kvp.Value;

                if (data.Bars.TryGetValue(symbol, out var bar))
                {
                    var price = bar.Close;
                    Plot(symbol.Value, "Price", price);
                    if (sd.LatestSignal == "Buy1" || sd.LatestSignal == "Buy2")
                        Plot(symbol.Value, "Buy", price);
                    if (sd.LatestSignal == "Sell1" || sd.LatestSignal == "Sell2")
                        Plot(symbol.Value, "Sell", price);
                }
            }

            foreach (var kvp in _symbols)
            {
                var symbol = kvp.Key;
                var sd = kvp.Value;
                sd.LatestSignal = "";

                bool isReady = sd.IsReady;
                bool hasBar = data.Bars.TryGetValue(symbol, out var bar);

                if (Time.Date >= new DateTime(2020, 02, 24))
                { }

                if (isReady && hasBar)
                {
                    var price = bar.Close;
                    var ema = sd.EMA.Current.Value;
                    var std = sd.STD.Current.Value;

                    if (std != 0)
                    {
                        var buyPrice = ema + _buyZ * std;
                        var sellPrice1 = ema + _sellZ1 * std;
                        var sellPrice2 = ema + _sellZ2 * std;

                        var fullQty = Math.Max(2, (int)(_maxPositionSize / price));// Desired full size
                        var currentQty = Portfolio[symbol].Quantity;

                        var openOrders = Transactions.GetOpenOrders(symbol);

                        if (currentQty == 0 && CanOpenNewPosition())
                        {
                            var existing = openOrders.FirstOrDefault(x => x.Direction == OrderDirection.Buy);
                            if (existing == null || ShouldReplaceOrder(existing, buyPrice))
                            {
                                if (existing != null) Transactions.CancelOrder(existing.Id);
                                var tkt = LimitOrder(symbol, fullQty, buyPrice);
                                sd.LatestSignal = "Buy";
                                sd.Note = "\"" + TicketToString(tkt) + "\"";
                            }
                        }
                        else if (currentQty > 0)
                        {
                            var halfQty = fullQty / 2;
                            var otherHalfQty = fullQty - halfQty;

                            if (CloseEnough(currentQty,fullQty))
                            {
                                var sellOrders = openOrders.Where(x => x.Direction == OrderDirection.Sell).ToList();
                                bool needsUpdate1 = true;
                                bool needsUpdate2 = true;

                                foreach (var order in sellOrders)
                                {
                                    if (order.Quantity == -halfQty && order.Status == OrderStatus.Submitted && order.Price > 0)
                                    {
                                        if (!ShouldReplaceOrder(order, sellPrice1))
                                            needsUpdate1 = false;
                                    }

                                    if (order.Quantity == -otherHalfQty && order.Status == OrderStatus.Submitted && order.Price > 0)
                                    {
                                        if (!ShouldReplaceOrder(order, sellPrice2))
                                            needsUpdate2 = false;
                                    }
                                }

                                if (needsUpdate1 || needsUpdate2)
                                {
                                    foreach (var order in sellOrders)
                                        Transactions.CancelOrder(order.Id);

                                    var tkt1 = LimitOrder(symbol, -halfQty, sellPrice1);
                                    var tkt2 = LimitOrder(symbol, -(fullQty - halfQty), sellPrice2);
                                    sd.LatestSignal = "SellFull";
                                    sd.Note = "\"" + TicketToString(tkt1) + "|" + TicketToString(tkt2) + "\"";

                                }
                            }
                            else if (CloseEnough(currentQty, halfQty))
                            {
                                var existing = openOrders.FirstOrDefault(x => x.Direction == OrderDirection.Buy);
                                if (existing == null || ShouldReplaceOrder(existing, buyPrice))
                                {
                                    if (existing != null)
                                        Transactions.CancelOrder(existing.Id);

                                    var tkt = LimitOrder(symbol, halfQty, buyPrice);
                                    sd.LatestSignal = "ReBuy";
                                    sd.Note = "\"" + TicketToString(tkt) + "\"";
                                }
                            }
                        }
                    }
                }
            }
        }

        private static bool CloseEnough(decimal qty1, decimal qty2)
        {
            if(qty1 == 0 && qty2 == 0)
                return true;

            double ratio = (double)qty1 / (double)qty2;

            return ratio < 1.1 && ratio > 0.9;
        }

        private bool ShouldReplaceOrder(Orders.Order order, decimal newPrice)
        {
            var limitPx = (order as Orders.LimitOrder).LimitPrice;
            var priceDiff = Math.Abs(order.Price - newPrice) / limitPx;
            return priceDiff > _priceAdjustmentThreshold;
        }

        private string TicketToString(Orders.OrderTicket tkt)
        {
            var order = Transactions.GetOrderById(tkt.OrderId);
            if ((order is null))
                return $"{tkt.OrderId} not found";
            
            var orderPx = (tkt.OrderType == OrderType.Limit) ? (order as Orders.LimitOrder).LimitPrice : 0;

            return $"id:{tkt.OrderId},sym:{tkt.Symbol},typ:{tkt.OrderType},sts:{tkt.Status},px:{orderPx:F3},qty:{tkt.Quantity}";
        }

        public override void OnEndOfDay(Symbol symbol)
        {
            var sd = _symbols[symbol];
            if (sd.IsReady)
            {
                var price = Securities[symbol].Price;
                var z = sd.STD.Current.Value == 0 ? 0 : (price - sd.EMA.Current.Value) / sd.STD.Current.Value;
                _ordersSw.WriteLine($"{Time:yyyyMMdd-HHmmss},{symbol.Value},{z:F2},{price:F2},{Portfolio[symbol].Quantity},{sd.LatestSignal},{sd.Note}");
            }
            sd.Note = "";
        }

        private bool CanOpenNewPosition()
        {
            var openPositions = Portfolio.Values.Count(p => p.Invested);
            var totalInvested = Portfolio.TotalHoldingsValue;
            return openPositions < _maxConcurrentPositions && totalInvested < _maxTotalCapital;
        }

        private class SymbolData
        {
            public Symbol Symbol;
            public ExponentialMovingAverage EMA;
            public StandardDeviation STD;
            public string LatestSignal = "";
            public string Note = "";

            public SymbolData(Symbol symbol, int zScorePeriod)
            {
                Symbol = symbol;
                EMA = new ExponentialMovingAverage(zScorePeriod);
                STD = new StandardDeviation(zScorePeriod);
            }

            public bool IsReady => EMA.IsReady && STD.IsReady;
        }
    }
}
