using System;
using EFT;
using UnityEngine;

namespace Vzor
{
    // aim only, no triggerbot. writes MovementContext.Rotation, which is the same field the
    // game's own mouse look writes, so we are not fighting input, just answering it faster.
    // also maintains the magic bullet target, which is picked in screen space like the
    // reference implementation does.
    public class AimbotController : MonoBehaviour
    {
        private GameWorld _world;
        private Player _player;
        private float _nextWorldSearch;

        private void Update()
        {
            if (_world == null && Time.unscaledTime >= _nextWorldSearch)
            {
                _nextWorldSearch = Time.unscaledTime + 1f;
                _world = UnityEngine.Object.FindObjectOfType<GameWorld>();
            }

            if (_world == null) return;
            if (_player == null) _player = _world.MainPlayer;
            if (_player == null) return;

            var camera = ViewCamera.Get();
            if (camera == null) return;

            try
            {
                if (Plugin.AimMagicBullet.Value) UpdateMagicTarget(camera);

                if (!Plugin.AimEnabled.Value || !IsAimKeyHeld()) return;

                var target = FindTarget(camera);
                if (!target.HasValue) return;

                var cameraPosition = camera.transform.position;
                var toTarget = target.Value - cameraPosition;
                if (toTarget.sqrMagnitude < 0.01f) return;

                var yaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
                var flat = Mathf.Sqrt(toTarget.x * toTarget.x + toTarget.z * toTarget.z);
                var pitch = -Mathf.Atan2(toTarget.y, flat) * Mathf.Rad2Deg;
                pitch = Mathf.Clamp(pitch, -89f, 89f);

                var movement = _player.MovementContext;
                if (movement == null) return;

                // this build wants yaw in x and pitch in y
                var desired = new Vector2(yaw, pitch);
                var smooth = Plugin.AimSmooth.Value;

                if (smooth > 0.01f)
                {
                    var current = movement.Rotation;
                    desired = Vector2.MoveTowards(current, desired, smooth * Time.unscaledDeltaTime);
                }

                movement.Rotation = desired;
            }
            catch
            {
                // target despawned mid-frame or player not ready
            }
        }

        // nearest head to the screen centre, on screen, same selection the reference uses
        private void UpdateMagicTarget(Camera camera)
        {
            Plugin.MagicBulletHasTarget = false;

            var players = _world.AllAlivePlayersList;
            if (players == null) return;

            var halfWidth = Screen.width * 0.5f;
            var halfHeight = Screen.height * 0.5f;
            var maxDistance = Plugin.AimDistance.Value;

            var bestScore = float.MaxValue;
            var bestPoint = Vector3.zero;
            var found = false;

            for (var i = 0; i < players.Count; i++)
            {
                try
                {
                    var candidate = players[i];
                    if (candidate == null || candidate.IsYourPlayer) continue;
                    if (!TryGetTargetPoint(candidate, Plugin.AimHead.Value, out var point)) continue;

                    var distance = Vector3.Distance(camera.transform.position, point);
                    if (distance > maxDistance) continue;

                    var screen = camera.WorldToScreenPoint(point);
                    if (screen.z <= 0f) continue;
                    if (screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height)
                        continue;

                    var dx = screen.x - halfWidth;
                    var dy = screen.y - halfHeight;
                    var score = dx * dx + dy * dy;

                    if (score >= bestScore) continue;

                    bestScore = score;
                    bestPoint = point;
                    found = true;
                }
                catch
                {
                    // candidate despawned mid-loop
                }
            }

            if (!found) return;

            Plugin.MagicBulletTarget = bestPoint;
            Plugin.MagicBulletHasTarget = true;
        }

        private Vector3? FindTarget(Camera camera)
        {
            var players = _world.AllAlivePlayersList;
            if (players == null) return null;

            var cameraPosition = camera.transform.position;
            var forward = camera.transform.forward;
            var maxAngle = Plugin.AimFov.Value * 0.5f;
            var maxDistance = Plugin.AimDistance.Value;
            var visibleOnly = Plugin.AimVisibleOnly.Value;

            Vector3? best = null;
            var bestAngle = float.MaxValue;

            for (var i = 0; i < players.Count; i++)
            {
                try
                {
                    var candidate = players[i];
                    if (candidate == null || candidate.IsYourPlayer) continue;
                    if (!TryGetTargetPoint(candidate, Plugin.AimHead.Value, out var point)) continue;

                    var toTarget = point - cameraPosition;
                    var distance = toTarget.magnitude;
                    if (distance > maxDistance || distance < 0.5f) continue;

                    var angle = Vector3.Angle(forward, toTarget);
                    if (angle > maxAngle || angle >= bestAngle) continue;

                    if (visibleOnly && !Visibility.HasLineOfSight(cameraPosition, point, candidate.transform))
                        continue;

                    bestAngle = angle;
                    best = point;
                }
                catch
                {
                    // candidate despawned mid-loop
                }
            }

            return best;
        }

        private static bool TryGetTargetPoint(Player candidate, bool head, out Vector3 point)
        {
            point = Vector3.zero;

            try
            {
                var body = candidate.PlayerBody;
                var bones = body != null ? body.PlayerBones : null;

                if (head && bones != null && bones.Head != null)
                {
                    // stance-aware position, unlike the raw Original transform
                    point = bones.Head.position;
                    return true;
                }

                if (bones != null && bones.Neck != null)
                {
                    point = bones.Neck.position;
                    return true;
                }

                point = candidate.transform.position + Vector3.up * (head ? 1.45f : 1.2f);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsAimKeyHeld()
        {
            try
            {
                return Input.GetKey(Plugin.AimKey.Value);
            }
            catch
            {
                return false;
            }
        }
    }
}
