using UnityEngine;

namespace Vzor
{
    // camera.main is not necessarily the view camera. eft keeps reflection, optic and
    // security cameras around, and a mirrored reflection camera projects everything
    // horizontally flipped. so: ask every active camera where a point 5m ahead lands.
    // the real one lands on centre. re-checked every 5 seconds so optics re-pick.
    internal static class ViewCamera
    {
        private static Camera _camera;
        private static float _nextPick;
        private static bool _logged;

        public static Camera Get()
        {
            var now = Time.unscaledTime;
            if (_camera != null && now < _nextPick) return _camera;

            _nextPick = now + 5f;
            _camera = Pick();
            return _camera;
        }

        private static Camera Pick()
        {
            var cameras = Camera.allCameras;
            if (cameras == null) return null;

            Camera best = null;
            var bestScore = float.MaxValue;
            var halfWidth = Screen.width * 0.5f;
            var halfHeight = Screen.height * 0.5f;

            for (var i = 0; i < cameras.Length; i++)
            {
                var camera = cameras[i];
                if (camera == null || !camera.isActiveAndEnabled) continue;

                var probe = camera.transform.position + camera.transform.forward * 5f;
                var screen = camera.WorldToScreenPoint(probe);

                var score = float.IsNaN(screen.x) || float.IsNaN(screen.y)
                    ? float.MaxValue
                    : Mathf.Abs(screen.x - halfWidth) + Mathf.Abs(screen.y - halfHeight);

                if (screen.z <= 0f) score += 100000f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = camera;
                }
            }

            if (!_logged && best != null)
            {
                _logged = true;
                Plugin.Logger.LogInfo("camera picked: '" + best.name + "' type=" + best.cameraType +
                                      " fov=" + best.fieldOfView + " rect=" + best.pixelRect +
                                      " score=" + Mathf.Round(bestScore) + " screen=" + Screen.width + "x" + Screen.height);
            }

            return best;
        }
    }
}
