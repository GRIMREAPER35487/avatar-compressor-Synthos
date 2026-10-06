using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor
{
    /// <summary>
    /// Self-contained localization system for Avatar Compressor editor UI.
    /// Decoupled from NDMF; parses bundled PO files directly.
    /// </summary>
    internal static class AvatarCompressorLocalization
    {
        private const string PrefsLanguageKey = "com.synthos.avatar-compressor.language";
        private static readonly Dictionary<string, string> _strings = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _fallbackStrings = new Dictionary<string, string>(StringComparer.Ordinal);

        private static readonly string[] SupportedLanguages = { "en-US", "ja-JP", "ko-KR", "zh-Hans", "zh-Hant" };
        private static readonly string[] LanguageDisplayNames = { "English (en-US)", "日本語 (ja-JP)", "한국어 (ko-KR)", "简体中文 (zh-Hans)", "繁體中文 (zh-Hant)" };

        private static string _currentLanguage;
        private static bool _initialized;

        public static string CurrentLanguage
        {
            get
            {
                if (!_initialized) Initialize();
                return _currentLanguage;
            }
            set
            {
                if (_currentLanguage != value)
                {
                    _currentLanguage = value;
                    EditorPrefs.SetString(PrefsLanguageKey, value);
                    Reload();
                }
            }
        }

        private static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _currentLanguage = EditorPrefs.GetString(PrefsLanguageKey, "en-US");
            if (!SupportedLanguages.Contains(_currentLanguage))
            {
                _currentLanguage = "en-US";
            }
            Reload();
        }

        private static void Reload()
        {
            _strings.Clear();
            _fallbackStrings.Clear();

            string folder = GetLocalizationFolderPath();
            if (string.IsNullOrEmpty(folder)) return;

            // Load fallback English first
            string enPath = Path.Combine(folder, "en-US.po");
            if (File.Exists(enPath))
            {
                LoadPoFile(enPath, _fallbackStrings);
            }

            // Load active locale if different
            if (_currentLanguage != "en-US")
            {
                string activePath = Path.Combine(folder, $"{_currentLanguage}.po");
                if (File.Exists(activePath))
                {
                    LoadPoFile(activePath, _strings);
                }
            }
        }

        private static string GetLocalizationFolderPath()
        {
            string[] candidates = {
                "Packages/com.synthos.avatar-compressor/Editor/Common/Localization",
                "Packages/dev.limitex.avatar-compressor/Editor/Common/Localization",
                "Assets/AvatarCompressor-Synthos/Editor/Common/Localization"
            };

            foreach (var c in candidates)
            {
                if (Directory.Exists(c)) return c;
            }

            var guids = AssetDatabase.FindAssets("en-US");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("en-US.po", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(path);
                }
            }

            return candidates[0];
        }

        private static void LoadPoFile(string filePath, Dictionary<string, string> target)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                string currentId = null;
                string currentStr = null;
                bool inStr = false;

                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("#") || string.IsNullOrEmpty(line))
                    {
                        if (!string.IsNullOrEmpty(currentId) && !string.IsNullOrEmpty(currentStr))
                        {
                            target[currentId] = currentStr;
                        }
                        currentId = null;
                        currentStr = null;
                        inStr = false;
                        continue;
                    }

                    if (line.StartsWith("msgid \""))
                    {
                        if (!string.IsNullOrEmpty(currentId) && !string.IsNullOrEmpty(currentStr))
                        {
                            target[currentId] = currentStr;
                        }
                        currentId = Unescape(line.Substring(7, line.Length - 8));
                        currentStr = null;
                        inStr = false;
                    }
                    else if (line.StartsWith("msgstr \""))
                    {
                        currentStr = Unescape(line.Substring(8, line.Length - 9));
                        inStr = true;
                    }
                    else if (line.StartsWith("\"") && line.EndsWith("\""))
                    {
                        string content = Unescape(line.Substring(1, line.Length - 2));
                        if (inStr) currentStr += content;
                        else if (currentId != null) currentId += content;
                    }
                }

                if (!string.IsNullOrEmpty(currentId) && !string.IsNullOrEmpty(currentStr))
                {
                    target[currentId] = currentStr;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AvatarCompressor] Failed to load PO file at {filePath}: {ex.Message}");
            }
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        internal static string Tr(string key)
        {
            if (!_initialized) Initialize();
            if (string.IsNullOrEmpty(key)) return string.Empty;

            if (_strings.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
                return val;

            if (_fallbackStrings.TryGetValue(key, out var fallback) && !string.IsNullOrEmpty(fallback))
                return fallback;

            return key;
        }

        internal static string Tr(string key, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, Tr(key), args);
        }

        internal static GUIContent Content(string labelKey, string tooltipKey = null)
        {
            return new GUIContent(Tr(labelKey), tooltipKey == null ? null : Tr(tooltipKey));
        }

        internal static string EnumValue<T>(string scope, T value)
            where T : struct, Enum
        {
            string name = (typeof(T).Name + value).Replace("_", string.Empty);
            string keyName = char.ToLowerInvariant(name[0]) + name.Substring(1);
            return Tr($"{scope}:enum:{keyName}");
        }

        internal static T EnumPopup<T>(string scope, GUIContent label, T value)
            where T : struct, Enum
        {
            var values = (T[])Enum.GetValues(typeof(T));
            var displayNames = values.Select(v => EnumValue(scope, v)).ToArray();
            int currentIndex = Array.IndexOf(values, value);
            int newIndex = EditorGUILayout.Popup(label, currentIndex, displayNames);
            return newIndex >= 0 && newIndex < values.Length ? values[newIndex] : value;
        }

        internal static void DrawEnumProperty<T>(
            string scope,
            SerializedProperty property,
            GUIContent label
        )
            where T : struct, Enum
        {
            var position = EditorGUILayout.GetControlRect();
            label = EditorGUI.BeginProperty(position, label, property);

            bool previousShowMixedValue = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
            EditorGUI.BeginChangeCheck();

            var currentValue = (T)Enum.ToObject(typeof(T), property.intValue);
            var newValue = EnumPopup(scope, position, label, currentValue);
            if (EditorGUI.EndChangeCheck())
            {
                property.intValue = Convert.ToInt32(newValue);
            }

            EditorGUI.showMixedValue = previousShowMixedValue;
            EditorGUI.EndProperty();
        }

        private static T EnumPopup<T>(string scope, Rect position, GUIContent label, T value)
            where T : struct, Enum
        {
            var values = (T[])Enum.GetValues(typeof(T));
            var displayNames = values
                .Select(v => new GUIContent(EnumValue(scope, v)))
                .ToArray();
            int currentIndex = Array.IndexOf(values, value);
            int newIndex = EditorGUI.Popup(position, label, currentIndex, displayNames);
            return newIndex >= 0 && newIndex < values.Length ? values[newIndex] : value;
        }

        internal static void DrawLanguagePicker()
        {
            if (!_initialized) Initialize();

            int current = Array.IndexOf(SupportedLanguages, CurrentLanguage);
            if (current < 0) current = 0;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Content("Common:label:language"), GUILayout.Width(70));
            int selected = EditorGUILayout.Popup(current, LanguageDisplayNames, GUILayout.Width(130));
            if (selected != current && selected >= 0 && selected < SupportedLanguages.Length)
            {
                CurrentLanguage = SupportedLanguages[selected];
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
