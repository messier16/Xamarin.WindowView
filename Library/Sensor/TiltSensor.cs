using System.Collections.Generic;
using Android.Content;
using Android.Hardware;
using Android.Runtime;
using Android.Views;
using Java.Lang;
using Axis = Android.Hardware.Axis;
using Math = System.Math;
using HardwareSensor = Android.Hardware.Sensor;

namespace Jmedeisis.WindowView.Sensor
{
    /// <summary>
    /// Interprets sensor data to calculate device tilt in terms of yaw, pitch and roll.
    /// Requires one of the following sensor combinations to be accessible via SensorManager
    /// <ul>
    /// <li>TYPE_ROTATION_VECTOR</li>
    /// <li>TYPE_MAGNETIC_FIELD + TYPE_GRAVITY</li>
    /// <li>TYPE_MAGNETIC_FIELD + TYPE_ACCELEROMETER</li>
    /// </ul>
    /// </summary>
    public class TiltSensor : Object, ISensorEventListener
    {
        // 1 radian = 180 / PI = 57.2957795 degrees
        private const float DegreesPerRadian = 57.2957795f;

        private readonly SensorManager _sensorManager;
        public bool IsTracking { get; private set; }

        private bool _relativeTilt;

        public interface ITiltListener
        {
            /// <summary>
            /// Euler angles defined as per <see cref="SensorManager.GetOrientation(float[], float[])"/>.
            /// All three are in <b>radians</b> and <b>positive</b> in the <b>counter-clockwise</b> direction.
            /// </summary>
            /// <param name="yaw">rotation around -Z axis. -PI to PI.</param>
            /// <param name="pitch">rotation around -X axis. -PI/2 to PI/2.</param>
            /// <param name="roll">rotation around Y axis. -PI to PI.</param>
            void OnTiltUpdate(float yaw, float pitch, float roll);
        }

        private readonly List<ITiltListener> _listeners;

        private readonly float[] _rotationMatrix = new float[9];
        private readonly float[] _rotationMatrixOrigin = new float[9];
        private readonly float[] _rotationMatrixTemp = new float[9];

        // [w, x, y, z] 
        private readonly float[] _latestQuaternion = new float[4];
        // [w, x, y, z] 
        private readonly float[] _invQuaternionOrigin = new float[4];
        // [w, x, y, z] 
        private readonly float[] _rotationQuaternion = new float[4];

        private readonly float[] _latestAccelerations = new float[3];
        private readonly float[] _latestMagFields = new float[3];
        private readonly float[] _orientation = new float[3];

        private bool _haveGravData;
        private bool _haveAccelData;
        private bool _haveMagData;
        private bool _haveRotOrigin;
        private bool _haveQuatOrigin;
        private bool _haveRotVecData;

        private IFilter _pitchFilter;
        private IFilter _rollFilter;
        private IFilter _yawFilter;

        /// <summary>
        /// See <see cref="ExponentialSmoothingFilter.SmoothingFactor"/>
        /// </summary>
        private const float SmoothingFactorHighAcc = 0.8f;
        private const float SmoothingFactorLowAcc = 0.05f;

        public TiltSensor(Context context, bool trackRelativeOrientation)
        {
            _listeners = new List<ITiltListener>();

            InitialiseDefaultFilters(SmoothingFactorLowAcc);

            _sensorManager = (SensorManager)context.GetSystemService(Context.SensorService);
            IsTracking = false;


            ScreenRotation = context.GetSystemService(Context.WindowService).JavaCast<IWindowManager>()
                .DefaultDisplay.Rotation;

            _relativeTilt = trackRelativeOrientation;
        }

        /// <summary>
        /// Registers for motion sensor events.
        /// Do this to begin receiving <see cref="ITiltListener.OnTiltUpdate(float, float, float)"/> callbacks.
        /// You must call <see cref="StopTracking"/> to unregister when tilt updates are no longer needed.
        /// </summary>
        /// <param name="samplingPeriodUs">See <see cref="SensorManager.RegisterListener(ISensorEventListener, Jmedeisis.WindowView.Sensor, SensorDelay)"/></param>
        public void StartTracking(SensorDelay samplingPeriodUs)
        {
            _sensorManager.RegisterListener(this, _sensorManager.GetDefaultSensor(SensorType.RotationVector),
                samplingPeriodUs);

            _sensorManager.RegisterListener(this, _sensorManager.GetDefaultSensor(SensorType.MagneticField),
                samplingPeriodUs);

            _sensorManager.RegisterListener(this, _sensorManager.GetDefaultSensor(SensorType.Gravity), samplingPeriodUs);

            _sensorManager.RegisterListener(this, _sensorManager.GetDefaultSensor(SensorType.Accelerometer),
                samplingPeriodUs);

            IsTracking = true;
        }

        public void StopTracking()
        {
            _sensorManager.UnregisterListener(this);
            _yawFilter?.Reset(0);
            _pitchFilter?.Reset(0);
            _rollFilter?.Reset(0);
            IsTracking = false;
        }

        public void AddListener(ITiltListener listener)
        {
            _listeners.Add(listener);
        }

        public void RemoveListener(ITiltListener listener)
        {
            _listeners.Remove(listener);
        }

        public void SetTrackRelativeOrientation(bool trackRelative)
        {
            _relativeTilt = trackRelative;
        }

        public SurfaceOrientation ScreenRotation { get; }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="factor">See <see cref="ExponentialSmoothingFilter.SmoothingFactor"/></param>
        private void InitialiseDefaultFilters(float factor)
        {
            _yawFilter = new ExponentialSmoothingFilter(factor, null == _yawFilter ? 0 : _yawFilter.Get());
            _pitchFilter = new ExponentialSmoothingFilter(factor, null == _pitchFilter ? 0 : _pitchFilter.Get());
            _rollFilter = new ExponentialSmoothingFilter(factor, null == _rollFilter ? 0 : _rollFilter.Get());
        }

        public void OnSensorChanged(SensorEvent e)
        {
            switch (e.Sensor.Type)
            {
                case SensorType.RotationVector:
                    var values = new float[e.Values.Count];
                    e.Values.CopyTo(values, 0);
                    SensorManager.GetQuaternionFromVector(_latestQuaternion, values);
                    if (!_haveRotVecData)
                    {
                        InitialiseDefaultFilters(SmoothingFactorHighAcc);
                    }
                    _haveRotVecData = true;
                    break;
                case SensorType.Gravity:
                    if (_haveRotVecData)
                    {
                        // rotation vector sensor data is better
                        _sensorManager.UnregisterListener(this, _sensorManager.GetDefaultSensor(SensorType.Gravity));
                        break;
                    }
                    e.Values.CopyTo(_latestAccelerations, 0);
                    _haveGravData = true;
                    break;
                case SensorType.Accelerometer:
                    if (_haveGravData || _haveRotVecData)
                    {
                        // rotation vector / gravity sensor data is better!
                        // let's not listen to the accelerometer anymore
                        _sensorManager.UnregisterListener(this, _sensorManager.GetDefaultSensor(SensorType.Accelerometer));
                        break;
                    }
                    e.Values.CopyTo(_latestAccelerations, 0);
                    _haveAccelData = true;
                    break;

                case SensorType.MagneticField:
                    if (_haveRotVecData)
                    {
                        // rotation vector sensor data is better
                        _sensorManager.UnregisterListener(this, _sensorManager.GetDefaultSensor(SensorType.MagneticField));
                        break;
                    }
                    e.Values.CopyTo(_latestMagFields, 0);
                    _haveMagData = true;
                    break;
            }

            if (HaveDataNecessaryToComputeOrientation())
            {
                ComputeOrientation();
            }
        }

        /// <summary>
        /// After <see cref="StartTracking(SensorDelay)"/> has been called and sensor data has been received,
        /// this method returns the sensor type chosen for orientation calculations.
        /// </summary>
        /// <returns>One of the sensors</returns>
        public SensorType GetChosenSensorType()
        {
            if (_haveRotVecData) return SensorType.RotationVector;
            if (_haveGravData) return SensorType.Gravity;
            if (_haveAccelData) return SensorType.Accelerometer;
            return 0;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns>True if both {@link #latestAccelerations} and {@link #latestMagFields} have valid values.</returns>
        private bool HaveDataNecessaryToComputeOrientation()
        {
            return _haveRotVecData || ((_haveGravData || _haveAccelData) && _haveMagData);
        }

        /// <summary>
        /// Computes the latest rotation, remaps it according to the current <see cref="ScreenRotation"/>,
        /// and it stores it in <see cref="_rotationMatrix"/>.
        /// 
        /// Should only be called if <see cref="HaveDataNecessaryToComputeOrientation"/> returns true and
        /// <see cref="_haveRotVecData"/> is false, else result may be undefined.
        /// </summary>
        /// <returns>true if rotation was retrieved and recalculated, false otherwise.</returns>
        private bool ComputeRotationMatrix()
        {
            if (SensorManager.GetRotationMatrix(_rotationMatrixTemp, null, _latestAccelerations, _latestMagFields))
            {
                switch (ScreenRotation)
                {
                    case SurfaceOrientation.Rotation0:
                        SensorManager.RemapCoordinateSystem(_rotationMatrixTemp,
                            Axis.X,
                            Axis.Y, _rotationMatrix);
                        break;
                    case SurfaceOrientation.Rotation90:
                        //noinspection SuspiciousNameCombination
                        SensorManager.RemapCoordinateSystem(_rotationMatrixTemp,
                            Axis.Y,
                            Axis.MinusX, _rotationMatrix);
                        break;
                    case SurfaceOrientation.Rotation180:
                        SensorManager.RemapCoordinateSystem(_rotationMatrixTemp,
                            Axis.MinusX,
                            Axis.MinusY, _rotationMatrix);
                        break;
                    case SurfaceOrientation.Rotation270:
                        //noinspection SuspiciousNameCombination
                        SensorManager.RemapCoordinateSystem(_rotationMatrixTemp,
                            Axis.MinusY,
                            Axis.X, _rotationMatrix);
                        break;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Computes the latest orientation and notifies any <see cref="ITiltListener"/>s.
        /// </summary>
        private void ComputeOrientation()
        {
            var updated = false;
            float yaw = 0;
            float pitch = 0;
            float roll = 0;

            if (_haveRotVecData)
            {
                RemapQuaternionToScreenRotation(_latestQuaternion, ScreenRotation);
                if (_relativeTilt)
                {
                    if (!_haveQuatOrigin)
                    {
                        _latestQuaternion.CopyTo(_invQuaternionOrigin, 0);
                        InvertQuaternion(_invQuaternionOrigin);
                        _haveQuatOrigin = true;
                    }
                    MultQuaternions(_rotationQuaternion, _invQuaternionOrigin, _latestQuaternion);
                }
                else
                {
                    _latestQuaternion.CopyTo(_rotationQuaternion, 0);
                }

                // https://en.wikipedia.org/wiki/Conversion_between_quaternions_and_Euler_angles
                var q0 = _rotationQuaternion[0]; // w
                var q1 = _rotationQuaternion[1]; // x
                var q2 = _rotationQuaternion[2]; // y
                var q3 = _rotationQuaternion[3]; // z

                var rotXRad = (float)Math.Atan2(2 * (q0 * q1 + q2 * q3), 1 - 2 * (q1 * q1 + q2 * q2));
                var rotYRad = (float)Math.Asin(2 * (q0 * q2 - q3 * q1));
                var rotZRad = (float)Math.Atan2(2 * (q0 * q3 + q1 * q2), 1 - 2 * (q2 * q2 + q3 * q3));

                // constructed to match output of SensorManager#getOrientation
                yaw = -rotZRad * DegreesPerRadian;
                pitch = -rotXRad * DegreesPerRadian;
                roll = rotYRad * DegreesPerRadian;
                updated = true;
            }
            else if (ComputeRotationMatrix())
            {
                if (_relativeTilt)
                {
                    if (!_haveRotOrigin)
                    {
                        _rotationMatrix.CopyTo(_rotationMatrixOrigin, 0);
                        _haveRotOrigin = true;
                    }
                    // get yaw / pitch / roll relative to original rotation
                    SensorManager.GetAngleChange(_orientation, _rotationMatrix, _rotationMatrixOrigin);
                }
                else
                {
                    // get absolute yaw / pitch / roll
                    SensorManager.GetOrientation(_rotationMatrix, _orientation);
                }
                /*
				 * [0] : yaw, rotation around -z axis
				 * [1] : pitch, rotation around -x axis
				 * [2] : roll, rotation around y axis
				 */
                yaw = _orientation[0] * DegreesPerRadian;
                pitch = _orientation[1] * DegreesPerRadian;
                roll = _orientation[2] * DegreesPerRadian;
                updated = true;
            }

            if (!updated) return;


            if (null != _yawFilter) yaw = _yawFilter.Push(yaw);
            if (null != _pitchFilter) pitch = _pitchFilter.Push(pitch);
            if (null != _rollFilter) roll = _rollFilter.Push(roll);

            for (var i = 0; i < _listeners.Count; i++)
            {
                _listeners[i].OnTiltUpdate(yaw, pitch, roll);
            }
        }

        /*
		 * @param immediate 
		 *                  
		 */

        /// <summary>
        /// 
        /// </summary>
        /// <param name="immediate">
        /// If true, any sensor data filters are reset to new origin immediately.
        /// If false, values transition smoothly to new origin.
        /// </param>
        public void ResetOrigin(bool immediate)
        {
            _haveRotOrigin = false;
            _haveQuatOrigin = false;
            if (immediate)
            {
                if (null != _yawFilter) _yawFilter.Reset(0);
                if (null != _pitchFilter) _pitchFilter.Reset(0);
                if (null != _rollFilter) _rollFilter.Reset(0);
            }
        }

        public void OnAccuracyChanged(HardwareSensor sensor, [GeneratedEnum] SensorStatus accuracy)
        {
            //throw new NotImplementedException();
        }

        /// <summary>
        /// Please drop me a PM if you know of a more elegant way to accomplish this - Justas
        /// </summary>
        /// <param name="q">[w, x, y, z]</param>
        /// <param name="screenRotation">See <see cref="Display.Rotation"/></param>
        private void RemapQuaternionToScreenRotation(float[] q, SurfaceOrientation screenRotation)
        {
            var x = q[1];
            var y = q[2];
            switch (screenRotation)
            {
                case SurfaceOrientation.Rotation0:
                    break;
                case SurfaceOrientation.Rotation90:
                    q[1] = -y;
                    q[2] = x;
                    break;
                case SurfaceOrientation.Rotation180:
                    q[1] = -x;
                    q[2] = -y;
                    break;
                case SurfaceOrientation.Rotation270:
                    q[1] = y;
                    q[2] = -x;
                    break;
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="qOut">[w, x, y, z] result.</param>
        /// <param name="q1">[w, x, y, z] left.</param>
        /// <param name="q2">[w, x, y, z] right.</param>
        private void MultQuaternions(float[] qOut, float[] q1, float[] q2)
        {
            // multiply quaternions
            var a = q1[0];
            var b = q1[1];
            var c = q1[2];
            var d = q1[3];

            var e = q2[0];
            var f = q2[1];
            var g = q2[2];
            var h = q2[3];

            qOut[0] = a * e - b * f - c * g - d * h;
            qOut[1] = b * e + a * f + c * h - d * g;
            qOut[2] = a * g - b * h + c * e + d * f;
            qOut[3] = a * h + b * g - c * f + d * e;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="q">[w, x, y, z]</param>
        private void InvertQuaternion(float[] q)
        {
            for (var i = 1; i < 4; i++)
            {
                q[i] = -q[i]; // invert quaternion
            }
        }
    }
}