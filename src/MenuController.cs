using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace Vzor
{
    // generic config menu. iterates every entry in the config file and renders a widget by
    // type, so adding a setting needs no menu code. insert toggles it.
    public class MenuController : MonoBehaviour
    {
        private bool _open;
        private Rect _window = new Rect(60f, 60f, 480f, 660f);
        private Vector2 _scroll;
        private bool _cursorSaved;
        private CursorLockMode _savedLock;
        private bool _savedVisible;

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(Plugin.MenuKey.Value)) SetOpen(!_open);
            }
            catch
            {
                // no legacy input, menu stays closed
            }

            if (_open) ForceCursorVisible();
        }

        private void SetOpen(bool open)
        {
            _open = open;

            if (open)
            {
                _savedLock = Cursor.lockState;
                _savedVisible = Cursor.visible;
                _cursorSaved = true;
                ForceCursorVisible();
            }
            else if (_cursorSaved)
            {
                Cursor.lockState = _savedLock;
                Cursor.visible = _savedVisible;
                _cursorSaved = false;
            }
        }

        private static void ForceCursorVisible()
        {
            try
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch
            {
                // cursor API unavailable, menu still usable
            }
        }

        private void OnGUI()
        {
            if (!_open) return;

            var matrix = GUI.matrix;
            var color = GUI.color;

            GUI.matrix = Matrix4x4.identity;

            GUI.Box(_window, "Vzor " + Plugin.Version);

            GUILayout.BeginArea(new Rect(_window.x + 8f, _window.y + 26f, _window.width - 16f, _window.height - 34f));
            _scroll = GUILayout.BeginScrollView(_scroll);
            DrawEntries();
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            HandleDrag();

            GUI.color = color;
            GUI.matrix = matrix;
        }

        private void HandleDrag()
        {
            var e = Event.current;
            if (e == null) return;

            var bar = new Rect(_window.x, _window.y, _window.width, 24f);
            if (e.type == EventType.MouseDrag && e.button == 0 && bar.Contains(e.mousePosition))
            {
                _window.x += e.delta.x;
                _window.y += e.delta.y;
                e.Use();
            }
        }

        private void DrawEntries()
        {
            var entries = new List<ConfigEntryBase>();
            foreach (var pair in Plugin.File) entries.Add(pair.Value);

            var ordered = entries
                .OrderBy(e => e.Definition.Section)
                .ThenBy(e => e.Definition.Key);

            string lastSection = null;

            foreach (var entry in ordered)
            {
                var section = entry.Definition.Section;
                if (section != lastSection)
                {
                    lastSection = section;
                    GUILayout.Space(6f);
                    GUILayout.Label("[" + section + "]");
                }

                DrawEntry(entry);
            }
        }

        private void DrawEntry(ConfigEntryBase entry)
        {
            var key = entry.Definition.Key;
            var type = entry.SettingType;

            try
            {
                if (type == typeof(bool))
                {
                    var value = (bool)entry.BoxedValue;
                    var next = GUILayout.Toggle(value, key);
                    if (next != value)
                    {
                        entry.BoxedValue = next;
                        Plugin.StampConfig();
                    }
                    return;
                }

                if (type == typeof(float))
                {
                    var value = (float)entry.BoxedValue;
                    var range = entry.Description.AcceptableValues as AcceptableValueRange<float>;
                    var min = range != null ? range.MinValue : 0f;
                    var max = range != null ? range.MaxValue : Mathf.Max(1f, value * 2f);

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(key, GUILayout.Width(210f));
                    var next = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(150f));
                    GUILayout.Label(next.ToString("0.###"), GUILayout.Width(62f));
                    GUILayout.EndHorizontal();

                    if (Math.Abs(next - value) > 0.0001f)
                    {
                        entry.BoxedValue = next;
                        Plugin.StampConfig();
                    }
                    return;
                }

                if (type == typeof(int))
                {
                    var value = (int)entry.BoxedValue;
                    var range = entry.Description.AcceptableValues as AcceptableValueRange<int>;
                    var min = range != null ? range.MinValue : 0;
                    var max = range != null ? range.MaxValue : Mathf.Max(1, value * 2);

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(key, GUILayout.Width(210f));
                    var next = (int)GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(150f));
                    GUILayout.Label(next.ToString(), GUILayout.Width(62f));
                    GUILayout.EndHorizontal();

                    if (next != value)
                    {
                        entry.BoxedValue = next;
                        Plugin.StampConfig();
                    }
                    return;
                }

                if (type == typeof(string))
                {
                    var value = (string)entry.BoxedValue ?? string.Empty;

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(key, GUILayout.Width(210f));
                    var next = GUILayout.TextField(value, GUILayout.Width(230f));
                    GUILayout.EndHorizontal();

                    if (next != value)
                    {
                        entry.BoxedValue = next;
                        Plugin.StampConfig();
                    }
                    return;
                }

                if (type == typeof(KeyCode))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(key, GUILayout.Width(210f));
                    GUILayout.Label(entry.BoxedValue.ToString());
                    GUILayout.EndHorizontal();
                    return;
                }

                GUILayout.BeginHorizontal();
                GUILayout.Label(key, GUILayout.Width(210f));
                GUILayout.Label(type.Name + " (edit in cfg)");
                GUILayout.EndHorizontal();
            }
            catch
            {
                // an entry misbehaved, skip it rather than break the whole menu
            }
        }
    }
}
