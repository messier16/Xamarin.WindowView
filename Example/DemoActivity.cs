using System;
using Android.App;
using Android.Content;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Android.OS;
using Jmedeisis.WindowView;

namespace Example
{
    [Activity(Label = "@string/app_name", 
	          MainLauncher = true, 
	          Icon = "@drawable/icon",
	          Theme="@style/AppTheme")]
    public class DemoActivity : Activity
    {
        int count = 1;

        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);
            SetContentView(Resource.Layout.activity_demo);

            // re-center of WindowView tilt sensors on tap
            var windowView1 = FindViewById<WindowView>(Resource.Id.windowView1);
            windowView1.Click +=
                (s, a) =>
                {
                    windowView1.ResetOrientationOrigin(false);
                };

            var windowView2 = FindViewById<WindowView>(Resource.Id.windowView2);
            windowView2.Click +=
                (s, a) =>
                {
                    windowView2.ResetOrientationOrigin(false);
                };
        }
    }
}

