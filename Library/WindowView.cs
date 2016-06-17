using System;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Hardware;
using Android.Runtime;
using Android.Util;
using Android.Widget;
using Jmedeisis.WindowView.Sensor;

namespace Jmedeisis.WindowView
{
    /// <summary>
    /// Determines the basis in which device orientation is measured.  
    /// </summary>
    public enum OrientationMode
    {
        /// <summary>
        /// Measures absolute yaw / pitch / roll (i.e. relative to the world).
        /// </summary>
        Absolute,
        /// <summary>
        /// Measures yaw / pitch / roll relative to the starting orientation.
        /// The starting orientation is determined upon receiving the first sensor data,
        /// but can be manually reset at any time using <see cref="ResetOrientationOrigin(bool)"/>.
        /// </summary>
        Relative
    }

    /// <summary>
    /// Determines the relationship between change in device tilt and change in image translation. 
    /// </summary>
    public enum TranslateMode
    {
        /// <summary>
        /// The image is translated by a constant amount per unit of device tilt.
        /// Generally preferable when viewing multiple adjacent WindowViews that have different
        /// contents but should move in tandem.
        /// Same amount of tilt will result in the same translation for two images of differing size.
        /// </summary>
        Constant,
        /// <summary>
        /// The image is translated proportional to its off-view size. Generally preferable when
        /// viewing a single WindowView, this mode ensures that the full image can be 'explored'
        /// within a fixed tilt amount range.
        /// Same amount of tilt will result in different translation for two images of differing size.
        /// </summary>
        Proportional
    }

    public class WindowView : ImageView, TiltSensor.ITiltListener
    {

        private const SensorDelay DefaultSensorSamplingPeriodUs = SensorDelay.Game;

        private const TranslateMode DefaultTranslateMode = TranslateMode.Proportional;

        private static readonly float DEFAULT_MAX_PITCH_DEGREES = 30;
        private static readonly float DEFAULT_MAX_ROLL_DEGREES = 30;
        private static readonly float DEFAULT_HORIZONTAL_ORIGIN_DEGREES = 0;
        private static readonly float DEFAULT_VERTICAL_ORIGIN_DEGREES = 0;

        private static readonly OrientationMode DEFAULT_ORIENTATION_MODE = OrientationMode.Relative;

        private const float DefaultMaxConstantTranslationDp = 150;
        protected float HeightDifference;

        // layout
        protected bool heightMatches;
        private float horizontalOriginDeg;
        private float latestPitch;
        private float latestRoll;
        private float maxConstantTranslation;
        private float maxPitchDeg;
        private float maxRollDeg;
        private OrientationMode orientationMode;

        protected TiltSensor sensor;
        private SensorDelay sensorSamplingPeriod;
        private TranslateMode translateMode;
        private float verticalOriginDeg;
        protected float widthDifference;

        public WindowView(Context context) : base(context)
        {
        }


        public WindowView(IntPtr handle, JniHandleOwnership transfer)
            : base(handle, transfer)
        {
            // SEE: http://stackoverflow.com/questions/10593022/monodroid-error-when-calling-constructor-of-custom-view-twodscrollview/10603714#10603714
        }

        public WindowView(Context context, IAttributeSet attrs) : base(context, attrs)
        {
            Init(context, attrs);
        }

        public WindowView(Context context, IAttributeSet attrs, int defStyleAttr) : base(context, attrs, defStyleAttr)
        {
            Init(context, attrs);
        }

        public WindowView(Context context, IAttributeSet attrs, int defStyleAttr, int defStyleRes)
            : base(context, attrs, defStyleAttr, defStyleRes)
        {
            Init(context, attrs);
        }

        protected void Init(Context context, IAttributeSet attrs)
        {
            sensorSamplingPeriod = DefaultSensorSamplingPeriodUs;
            maxPitchDeg = DEFAULT_MAX_PITCH_DEGREES;
            maxRollDeg = DEFAULT_MAX_ROLL_DEGREES;
            verticalOriginDeg = DEFAULT_VERTICAL_ORIGIN_DEGREES;
            horizontalOriginDeg = DEFAULT_HORIZONTAL_ORIGIN_DEGREES;
            orientationMode = DEFAULT_ORIENTATION_MODE;
            translateMode = DefaultTranslateMode;
            maxConstantTranslation = DefaultMaxConstantTranslationDp * Resources.DisplayMetrics.Density;

            if (null != attrs)
            {
                var a = context.ObtainStyledAttributes(attrs, Resource.Styleable.WindowView);
                // Buggy line: 
                sensorSamplingPeriod = DefaultSensorSamplingPeriodUs;
                //(SensorDelay)(a.GetInt(Resource.Styleable.WindowView_sensor_sampling_period, (int)sensorSamplingPeriod));
                maxPitchDeg = a.GetFloat(Resource.Styleable.WindowView_max_pitch, maxPitchDeg);
                maxRollDeg = a.GetFloat(Resource.Styleable.WindowView_max_roll, maxRollDeg);
                verticalOriginDeg = a.GetFloat(Resource.Styleable.WindowView_vertical_origin,
                    verticalOriginDeg);
                horizontalOriginDeg = a.GetFloat(Resource.Styleable.WindowView_horizontal_origin,
                    horizontalOriginDeg);

                var orientationModeIndex = a.GetInt(Resource.Styleable.WindowView_orientation_mode, -1);
                if (orientationModeIndex >= 0)
                {
                    orientationMode = (OrientationMode)orientationModeIndex;
                }
                var translateModeIndex = a.GetInt(Resource.Styleable.WindowView_translate_mode, -1);
                if (translateModeIndex >= 0)
                {
                    translateMode = (TranslateMode)translateModeIndex;
                }

                maxConstantTranslation = a.GetDimension(Resource.Styleable.WindowView_max_constant_translation,
                    maxConstantTranslation);
                a.Recycle();
            }

            if (!IsInEditMode)
            {
                sensor = new TiltSensor(context, orientationMode == OrientationMode.Relative);
                sensor.AddListener(this);
            }

            SetScaleType(ScaleType.CenterCrop);
        }

        #region Life-cycle

        /// Registering for sensor events should be tied to Activity / Fragment lifecycle events.
        /// However, this would mean that WindowView cannot be independent.We tie into a few
        /// lifecycle-esque View events that allow us to make WindowView completely independent.
        ///
        /// Un-registering from sensor events is done aggressively to minimise battery drain and
        /// performance impact.

        public override void OnWindowFocusChanged(bool hasWindowFocus)
        {
            base.OnWindowFocusChanged(hasWindowFocus);
            if (hasWindowFocus)
            {
                sensor.StartTracking(sensorSamplingPeriod);
            }
            else
            {
                sensor.StopTracking();
            }
        }

        protected override void OnAttachedToWindow()
        {
            base.OnAttachedToWindow();
            if (!IsInEditMode) sensor.StartTracking(sensorSamplingPeriod);
        }

        protected override void OnDetachedFromWindow()
        {
            base.OnDetachedFromWindow();
            sensor.StopTracking();
        }

        #endregion

        #region Drawing & layout
        protected override void OnDraw(Canvas canvas)
        {
            // -1 -> 1
            var xOffset = 0f;
            var yOffset = 0f;
            if (heightMatches)
            {
                // only let user tilt horizontally
                xOffset = (-horizontalOriginDeg +
                           ClampAbsoluteFloating(horizontalOriginDeg, latestRoll, maxRollDeg)) / maxRollDeg;
            }
            else
            {
                // only let user tilt vertically
                yOffset = (verticalOriginDeg -
                           ClampAbsoluteFloating(verticalOriginDeg, latestPitch, maxPitchDeg)) / maxPitchDeg;
            }
            canvas.Save();
            switch (translateMode)
            {
                case TranslateMode.Constant:
                    canvas.Translate(
                        ClampAbsoluteFloating(0, maxConstantTranslation * xOffset, widthDifference / 2),
                        ClampAbsoluteFloating(0, maxConstantTranslation * yOffset, HeightDifference / 2));
                    break;
                case TranslateMode.Proportional:
                    canvas.Translate((float)Math.Round(widthDifference / 2f * xOffset),
                        (float)Math.Round(HeightDifference / 2f * yOffset));
                    break;
            }
            base.OnDraw(canvas);
            canvas.Restore();
        }

        private float ClampAbsoluteFloating(float origin, float value, float maxAbsolute)
        {
            return value < origin
                ? Math.Max(value, origin - maxAbsolute)
                : Math.Min(value, origin + maxAbsolute);
        }

        public TranslateMode TranslateMode
        {
            set { this.translateMode = value; }
            get { return translateMode; }
        }

        /// <summary>
        /// Maximum image translation from center when using <see cref="TranslateMode"/>.Constant
        /// </summary>
        public float MaxConstantTranslation
        {
            set { this.maxConstantTranslation = value; }
            get { return maxConstantTranslation; }
        }

        /// <summary>
        /// Maximum angle (in degrees) from origin for vertical tilts.
        /// </summary>
        public float MaxPitch
        {
            set { maxPitchDeg = value; }
            get { return maxPitchDeg; }
        }

        /// <summary>
        /// Maximum angle (in degrees) from origin for horizontal tilts.
        /// </summary>
        public float MaxRoll
        {
            set { maxRollDeg = value; }
            get { return maxRollDeg; }
        }

        /// <summary>
        /// Horizontal origin (in degrees). When <see cref="latestRoll"/> equals this value, the image is centered horizontally.
        /// </summary>
        public float HorizontalOrigin
        {
            set { horizontalOriginDeg = value; }
            get { return horizontalOriginDeg; }
        }

        /// <summary>
        /// Vertical origin (in degrees). When <see cref="latestPitch"/> equals this value, the image is centered vertically.
        /// </summary>
        public float VerticalOrigin
        {
            set { verticalOriginDeg = value; }
            get { return verticalOriginDeg; }
        }

        public override void SetImageDrawable(Drawable drawable)
        {
            base.SetImageDrawable(drawable);
            RecalculateImageDimensions();
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            RecalculateImageDimensions();
        }

        private void RecalculateImageDimensions()
        {
            var drawable = Drawable;
            if (null == drawable) return;

            var scaleType = GetScaleType();
            float width = Width;
            float height = Height;
            float imageWidth = drawable.IntrinsicWidth;
            float imageHeight = drawable.IntrinsicHeight;

            heightMatches = !WidthRatioGreater(width, height, imageWidth, imageHeight);

            if (scaleType == ScaleType.CenterCrop)
            {
                if (heightMatches)
                {
                    imageWidth *= height / imageHeight;
                    imageHeight = height;
                }
                else
                {
                    imageWidth = width;
                    imageHeight *= width / imageWidth;
                }
                widthDifference = imageWidth - width;
                HeightDifference = imageHeight - height;
            }
            else
            {
                widthDifference = 0;
                HeightDifference = 0;
            }
        }

        private bool WidthRatioGreater(float width, float height, float otherWidth, float otherHeight)
        {
            return height / otherHeight < width / otherWidth;
        }

        public override void SetScaleType(ScaleType scaleType)
        {
            if (ScaleType.CenterCrop != scaleType)
                throw new ArgumentException("Image scale type " + scaleType +
                                            " is not supported by WindowView. Use ScaleType.CenterCrop instead.");
            base.SetScaleType(scaleType);
        }

        #endregion

        #region Sensor data

        public void OnTiltUpdate(float yaw, float pitch, float roll)
        {
            latestPitch = pitch;
            latestRoll = roll;
            Invalidate();
        }

        public void AddTiltListener(TiltSensor.ITiltListener listener)
        {
            sensor.AddListener(listener);
        }

        public void RemoveTiltListener(TiltSensor.ITiltListener listener)
        {
            sensor.RemoveListener(listener);
        }

        /// <summary>
        /// Manually resets the orientation origin. Has no effect unless <see cref="OrientationMode"/> is OrientationMode.Relative.
        /// </summary>
        /// <param name="immediate">If false, the sensor values smoothly interpolate to the new origin.</param>
        public void ResetOrientationOrigin(bool immediate)
        {
            sensor.ResetOrigin(immediate);
        }

        /// <summary>
        /// Determines the mapping of orientation to image offset.
        /// </summary>
        public OrientationMode OrientationMode
        {
            set
            {
                orientationMode = value;
                sensor.SetTrackRelativeOrientation(orientationMode == OrientationMode.Relative);
                sensor.ResetOrigin(true);
            }
            get { return orientationMode; }
        }

        /// <summary>

        /// </summary>
        public SensorDelay SensorSamplingPeriod
        {
            set
            {
                sensorSamplingPeriod = value;
                if (sensor.IsTracking)
                {
                    sensor.StopTracking();
                    sensor.StartTracking(sensorSamplingPeriod);
                }

            }
            get { return sensorSamplingPeriod; }
        }

        #endregion
    }
}