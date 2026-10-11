using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor
{
    /// <summary>
    /// Localization for Avatar Compressor editor UI, backed by the bundled <c>.po</c> files
    /// (imported by Unity as <see cref="LocalizationAsset"/>s).
    /// </summary>
    internal static class AvatarCompressorLocalization
    {
        private const string PackageName = "com.synthos.avatar-compressor";
        private const string LocalizationFolder =
            "Packages/" + PackageName + "/Editor/Common/Localization";
        private const string LanguagePrefsKey =
            AvatarCompressorPreferences.PrefsPrefix + "language";
        private const string FallbackLanguage = "en-US";

        private static Dictionary<string, LocalizationAsset> s_assets;
        private static string[] s_languages;
        private static GUIContent[] s_languageNames;
        private static string s_currentLanguage;

        /// <summary>
        /// The active UI language (e.g. "en-US", "ja-JP"). Persisted in EditorPrefs.
        /// </summary>
        internal static string CurrentLanguage
        {
            get
            {
                if (s_currentLanguage == null)
                    s_currentLanguage = EditorPrefs.GetString(
                        LanguagePrefsKey,
                        GetDefaultLanguage()
                    );
                return s_currentLanguage;
            }
            set
            {
                if (string.IsNullOrEmpty(value) || value == CurrentLanguage)
                    return;
                s_currentLanguage = value;
                EditorPrefs.SetString(LanguagePrefsKey, value);
            }
        }

        internal static string Tr(string key)
        {
            if (TryGetString(CurrentLanguage, key, out string value))
                return value;
            if (TryGetString(FallbackLanguage, key, out value))
                return value;
            return $"<{key}>";
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
            var displayNames = values.Select(value => EnumValue(scope, value)).ToArray();
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
                .Select(value => new GUIContent(EnumValue(scope, value)))
                .ToArray();
            int currentIndex = Array.IndexOf(values, value);
            int newIndex = EditorGUI.Popup(position, label, currentIndex, displayNames);
            return newIndex >= 0 && newIndex < values.Length ? values[newIndex] : value;
        }

        internal static void DrawLanguagePicker()
        {
            EnsureLoaded();
            if (s_languages.Length == 0)
                return;

            int current = Array.FindIndex(
                s_languages,
                language =>
                    string.Equals(language, CurrentLanguage, StringComparison.OrdinalIgnoreCase)
            );
            int selected = EditorGUILayout.Popup(
                new GUIContent(Tr("Common:label:language")),
                Math.Max(current, 0),
                s_languageNames
            );
            if (selected != current && selected >= 0 && selected < s_languages.Length)
                CurrentLanguage = s_languages[selected];
        }

        private static bool TryGetString(string language, string key, out string value)
        {
            value = null;
            EnsureLoaded();
            if (string.IsNullOrEmpty(language) || !s_assets.TryGetValue(language, out var asset))
                return false;
            if (asset == null)
            {
                // The asset was reimported; reload on next access.
                s_assets = null;
                return false;
            }

            value = asset.GetLocalizedString(key);
            return !string.IsNullOrEmpty(value) && value != key;
        }

        private static void EnsureLoaded()
        {
            if (s_assets != null)
                return;

            s_assets = new Dictionary<string, LocalizationAsset>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in LoadLocalizationAssets())
            {
                if (!string.IsNullOrEmpty(asset.localeIsoCode))
                    s_assets[asset.localeIsoCode] = asset;
            }

            s_languages = s_assets
                .Keys.OrderBy(language => language == FallbackLanguage ? 0 : 1)
                .ThenBy(language => language, StringComparer.Ordinal)
                .ToArray();
            s_languageNames = s_languages
                .Select(language => new GUIContent(GetDisplayName(language)))
                .ToArray();
        }

        private static string GetDisplayName(string language)
        {
            try
            {
                return $"{CultureInfo.GetCultureInfo(language).NativeName} ({language})";
            }
            catch (CultureNotFoundException)
            {
                return language;
            }
        }

        private static string GetDefaultLanguage()
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Japanese:
                    return "ja-JP";
                case SystemLanguage.Korean:
                    return "ko-KR";
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.Chinese:
                    return "zh-Hans";
                case SystemLanguage.ChineseTraditional:
                    return "zh-Hant";
                default:
                    return FallbackLanguage;
            }
        }

        internal static List<LocalizationAsset> LoadLocalizationAssets()
        {
            return AssetDatabase
                .FindAssets("t:LocalizationAsset", new[] { LocalizationFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".po", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<LocalizationAsset>)
                .Where(asset => asset != null)
                .ToList();
        }
    }
}
