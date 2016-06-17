using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

namespace Jmedeisis.WindowView.Sensor
{
    /// <summary>
    /// Performs exponential smoothing with an exponentially-weighted moving average.
    /// Analogous to an infinite-impulse-response, single-pole low-pass filter.
    /// </summary>
    public class ExponentialSmoothingFilter : IFilter
    {
        private float _lastValue;
        private float _factor;

        public ExponentialSmoothingFilter(float smoothingFactor, float initialValue)
        {
            _factor = smoothingFactor;
            Reset(initialValue);
        }

        /// <summary>
        /// Sets the smoothing factor.
        /// Calculated as dt / (t + dt), where t is the system's time constant and dt
        /// is the sampling period, i.e. the rate that new values are delivered via
        /// <see cref="Push(float)"/>
        /// The closer to 0, the greater the inertia, i.e. the filter responds more slowly
        /// to new input values.
        /// </summary>
        public float SmoothingFactor
        {
            set { _factor = value; }
        }

        public void Reset(float value)
        {
            _lastValue = value;
        }

        /// <summary>
        /// Pushes new sample to filter.
        /// </summary>
        /// <param name="value"></param>
        /// <returns>New smoothed value</returns>
        public float Push(float value)
        {
            // do low-pass
            _lastValue = _lastValue + _factor * (value - _lastValue);
            return Get();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns>Smoothed value</returns>
        public float Get()
        {
            return _lastValue;
        }
    }
}