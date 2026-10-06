using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using dev.limitex.avatar.compressor.editor;
using dev.limitex.avatar.compressor.editor.texture;
using dev.limitex.avatar.compressor.editor.texture.ui;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.tests
{
    [TestFixture]
    internal sealed class AvatarCompressorLocalizationTests
    {
        private const string LocalizationFolder =
            "Packages/com.synthos.avatar-compressor/Editor/Common/Localization";

        private static string GetActualLocalizationFolder()
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

        private static readonly string[] BundledLocales =
        {
            "en-US",
            "zh-Hans",
            "zh-Hant",
            "ja-JP",
            "ko-KR",
        };

        private string _originalLanguage;

        [SetUp]
        public void SetUp()
        {
            _originalLanguage = AvatarCompressorLocalization.CurrentLanguage;
        }

        [TearDown]
        public void TearDown()
        {
            AvatarCompressorLocalization.CurrentLanguage = _originalLanguage;
        }

        [Test]
        public void LocalizationFiles_HaveMatchingKeysAndFormatPlaceholders()
        {
            string folder = GetActualLocalizationFolder();
            string enPath = Path.Combine(folder, "en-US.po");
            if (!File.Exists(enPath)) return;

            var english = ReadMessages(enPath);

            foreach (var locale in BundledLocales)
            {
                string locPath = Path.Combine(folder, $"{locale}.po");
                if (!File.Exists(locPath)) continue;

                var localized = ReadMessages(locPath);
                CollectionAssert.AreEquivalent(
                    english.Keys,
                    localized.Keys,
                    $"Locale {locale} has a different key set."
                );

                foreach (string key in english.Keys)
                {
                    Assert.IsNotEmpty(localized[key], $"Locale {locale} has an empty '{key}'.");
                    CollectionAssert.AreEquivalent(
                        GetPlaceholders(english[key]),
                        GetPlaceholders(localized[key]),
                        $"Locale {locale}, key '{key}' has different format placeholders."
                    );
                }
            }
        }

        [Test]
        public void EnglishSource_UsesMsgstrForCrowdinSourceText()
        {
            string folder = GetActualLocalizationFolder();
            string enPath = Path.Combine(folder, "en-US.po");
            if (!File.Exists(enPath)) return;

            string english = File.ReadAllText(enPath);
            StringAssert.Contains("\"X-Crowdin-SourceKey: msgstr\\n\"", english);
        }

        [TestCase("en-US", "General")]
        [TestCase("zh-Hans", "常规")]
        [TestCase("zh-Hant", "一般")]
        [TestCase("ja-JP", "一般")]
        [TestCase("ko-KR", "일반")]
        public void LanguagePrefs_SelectsRequestedLocalization(string language, string expected)
        {
            AvatarCompressorLocalization.CurrentLanguage = language;

            Assert.AreEqual(expected, AvatarCompressorLocalization.Tr("Common:label:general"));
        }

        [TestCaseSource(nameof(BundledLocales))]
        public void NotAvailableMarker_RemainsLanguageIndependent(string language)
        {
            AvatarCompressorLocalization.CurrentLanguage = language;

            Assert.AreEqual("N/A", AvatarCompressorLocalization.Tr("Common:label:notAvailable"));
        }

        [Test]
        public void EnumMappings_CoverEveryDisplayedValue()
        {
            AvatarCompressorLocalization.CurrentLanguage = "en-US";

            AssertEnumLocalized<AnalysisStrategyType>();
            AssertEnumLocalized<CompressionPlatform>();
            AssertEnumLocalized<FrozenTextureFormat>();
            AssertEnumLocalized<AnalysisBackendPreference>();
            AssertEnumLocalized<ResizeBackendPreference>();
            AssertEnumLocalized<TexturePropertyCategory>();
            AssertEnumLocalized<SkipReason>();
        }

        [Test]
        public void CustomPresetInspector_PreservesMultiObjectEditingWithoutHeaderDecorators()
        {
            Assert.IsTrue(
                Attribute.IsDefined(
                    typeof(CustomTextureCompressorPresetEditor),
                    typeof(CanEditMultipleObjects)
                )
            );

            string[] fieldsWithHeaderDecorators = typeof(CustomTextureCompressorPreset)
                .GetFields()
                .Where(field => Attribute.IsDefined(field, typeof(HeaderAttribute)))
                .Select(field => field.Name)
                .ToArray();
            CollectionAssert.IsEmpty(fieldsWithHeaderDecorators);
        }

        [TestCase(CompressorPreset.HighQuality, "High Quality")]
        [TestCase(CompressorPreset.Quality, "Quality")]
        [TestCase(CompressorPreset.Balanced, "Balanced")]
        [TestCase(CompressorPreset.Aggressive, "Aggressive")]
        [TestCase(CompressorPreset.Maximum, "Maximum")]
        [TestCase(CompressorPreset.Custom, "Custom")]
        public void StandardPreset_KeepsDisplayNameEnglish(
            CompressorPreset preset,
            string expectedName
        )
        {
            AvatarCompressorLocalization.CurrentLanguage = "zh-Hans";

            Assert.AreEqual(expectedName, PresetSection.GetPresetDisplayName(preset));
        }

        private static void AssertEnumLocalized<T>()
            where T : struct, Enum
        {
            foreach (T value in Enum.GetValues(typeof(T)))
            {
                string localized = AvatarCompressorLocalization.EnumValue(
                    "TextureCompressor",
                    value
                );
                Assert.IsFalse(
                    localized.StartsWith("<", StringComparison.Ordinal),
                    $"Missing localization for {typeof(T).Name}.{value}."
                );
            }
        }

        private static Dictionary<string, string> ReadMessages(string path)
        {
            var messages = new Dictionary<string, string>(StringComparer.Ordinal);
            string currentId = null;

            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith("msgid \"", StringComparison.Ordinal))
                {
                    currentId = ReadQuotedValue(line);
                }
                else if (
                    currentId != null
                    && currentId.Length > 0
                    && line.StartsWith("msgstr \"", StringComparison.Ordinal)
                )
                {
                    Assert.IsFalse(
                        messages.ContainsKey(currentId),
                        $"Duplicate key '{currentId}'."
                    );
                    messages.Add(currentId, ReadQuotedValue(line));
                    currentId = null;
                }
            }

            return messages;
        }

        private static string ReadQuotedValue(string line)
        {
            int firstQuote = line.IndexOf('"');
            return line.Substring(firstQuote + 1, line.Length - firstQuote - 2);
        }

        private static string[] GetPlaceholders(string value)
        {
            return Regex
                .Matches(value, @"\{\d+(?::[^}]*)?\}")
                .Cast<Match>()
                .Select(match => match.Value)
                .OrderBy(placeholder => placeholder, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
