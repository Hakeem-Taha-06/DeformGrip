using System;
using Godot;

namespace DeformGrip.App.Scene
{
    /// <summary>
    /// Simple orbit/zoom camera preserved across Ours / SOFA / Side-by-Side mode switches.
    /// Left-drag: orbit, Right-drag or Middle-drag: pan, Mouse wheel: zoom.
    /// </summary>
    public partial class OrbitCameraController : Camera3D
    {
        [Export] public float DefaultDistance = 0.21f;
        [Export] public float DefaultYawDeg = 28f;
        [Export] public float DefaultPitchDeg = 18f;
        [Export] public float MinDistance = 0.08f;
        [Export] public float MaxDistance = 0.65f;
        [Export] public float OrbitSensitivity = 0.35f;
        [Export] public float PanSensitivity = 0.00045f;
        [Export] public float ZoomFactor = 0.90f;

        private Vector3 _target = Vector3.Zero;
        private float _distance;
        private float _yawDeg;
        private float _pitchDeg;
        private bool _orbiting;
        private bool _panning;

        public override void _Ready()
        {
            ResetView();
        }

        public void ResetView()
        {
            _target = Vector3.Zero;
            _distance = DefaultDistance;
            _yawDeg = DefaultYawDeg;
            _pitchDeg = DefaultPitchDeg;
            UpdateCameraTransform();
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (@event is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.Left)
                    _orbiting = mb.Pressed;
                else if (mb.ButtonIndex == MouseButton.Right || mb.ButtonIndex == MouseButton.Middle)
                    _panning = mb.Pressed;
                else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
                {
                    _distance = Math.Clamp(_distance * ZoomFactor, MinDistance, MaxDistance);
                    UpdateCameraTransform();
                }
                else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
                {
                    _distance = Math.Clamp(_distance / ZoomFactor, MinDistance, MaxDistance);
                    UpdateCameraTransform();
                }
            }
            else if (@event is InputEventMouseMotion mm)
            {
                if (_orbiting)
                {
                    _yawDeg -= mm.Relative.X * OrbitSensitivity;
                    _pitchDeg = Math.Clamp(_pitchDeg + mm.Relative.Y * OrbitSensitivity, -85f, 85f);
                    UpdateCameraTransform();
                }
                else if (_panning)
                {
                    Vector3 right = GlobalTransform.Basis.X;
                    Vector3 up = GlobalTransform.Basis.Y;
                    _target -= right * (mm.Relative.X * PanSensitivity * _distance / DefaultDistance);
                    _target += up * (mm.Relative.Y * PanSensitivity * _distance / DefaultDistance);
                    UpdateCameraTransform();
                }
            }
            else if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.R)
            {
                ResetView();
            }
        }

        private void UpdateCameraTransform()
        {
            float yaw = Mathf.DegToRad(_yawDeg);
            float pitch = Mathf.DegToRad(_pitchDeg);

            float cosP = MathF.Cos(pitch);
            Vector3 offset = new Vector3(
                MathF.Sin(yaw) * cosP,
                MathF.Sin(pitch),
                MathF.Cos(yaw) * cosP) * _distance;

            GlobalPosition = _target + offset;
            LookAt(_target, Vector3.Up);
        }
    }
}
