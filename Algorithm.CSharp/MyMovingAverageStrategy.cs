using QuantConnect.Algorithm;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect;
using System;
using QuantConnect.Data;

namespace QuantConnect.Algorithm.CSharp
{
    public class MyMovingAverageStrategy : QCAlgorithm
    {
        private Symbol _symbol;
        private ExponentialMovingAverage _fast;
        private ExponentialMovingAverage _slow;

        public override void Initialize()
        {
            SetStartDate(2020, 1, 1);
            SetEndDate(2021, 1, 1);
            SetCash(100000);

            _symbol = AddEquity("SPY", Resolution.Daily).Symbol;

            _fast = EMA(_symbol, 10, Resolution.Daily);
            _slow = EMA(_symbol, 30, Resolution.Daily);
        }

        public override void OnData(Slice data)
        {
            if (!_fast.IsReady || !_slow.IsReady) return;

            if (!Portfolio[_symbol].Invested && _fast > _slow)
            {
                SetHoldings(_symbol, 1);
            }
            else if (Portfolio[_symbol].Invested && _fast < _slow)
            {
                Liquidate(_symbol);
            }
        }
    }
}
