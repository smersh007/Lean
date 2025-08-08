using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Orders;
namespace QuantConnect.Algorithm.CSharp
{
    public class MyScalingStrategy : QCAlgorithm
    {
        private Symbol _symbol;
        private SimpleMovingAverage _fast;
        private SimpleMovingAverage _slow;
        private decimal[] _entryThresholds = { 0.01m, 0.02m, 0.03m }; // % above slow MA
        private decimal[] _exitThresholds = { -0.01m, -0.02m, -0.03m }; // % below slow MA
        private int _targetScaleSteps = 3;
        private int _currentScaleStep = 0;
        private int _baseQuantity;

        public override void Initialize()
        {
            SetStartDate(2000, 1, 1);
            SetEndDate(2001, 1, 1);
            SetCash(100000);

            _symbol = AddEquity("SPY", Resolution.Daily).Symbol;

            _fast = SMA(_symbol, 10, Resolution.Daily);
            _slow = SMA(_symbol, 30, Resolution.Daily);
        }

        public override void OnData(Slice data)
        {
            if (!_fast.IsReady || !_slow.IsReady) return;
            if (!data.ContainsKey(_symbol)) return;

            var price = data[_symbol].Close;
            var maSlow = _slow.Current.Value;
            var delta = (price - maSlow) / maSlow;

            _baseQuantity = (int)(Portfolio.Cash / price / _targetScaleSteps);

            // === SCALE IN ===
            if (delta > _entryThresholds[_currentScaleStep] && _fast > _slow && _currentScaleStep < _targetScaleSteps)
            {
                var qty = _baseQuantity;
                MarketOrder(_symbol, qty);
                _currentScaleStep++;
                Debug($"Scale IN: Step {_currentScaleStep} at {price:C}");
            }

            // === SCALE OUT ===
            if (_currentScaleStep > 0 && delta < _exitThresholds[_currentScaleStep - 1])
            {
                var qty = _baseQuantity;
                MarketOrder(_symbol, -qty);
                _currentScaleStep--;
                Debug($"Scale OUT: Step {_currentScaleStep} at {price:C}");
            }
        }
    }
}
