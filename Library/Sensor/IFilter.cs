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
    public interface IFilter
    {
        /// <summary>
        /// Update filter with the latest value
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        float Push(float value);

        /// <summary>
        /// Reset filter to the given value
        /// </summary>
        /// <param name="value"></param>
        void Reset(float value);

        /// <summary>
        /// Latest filtered value
        /// </summary>
        /// <returns></returns>
        float Get();
    }
}