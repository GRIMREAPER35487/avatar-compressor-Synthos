using System.Collections.Generic;
using System.Linq;
using dev.limitex.avatar.compressor;
using dev.limitex.avatar.compressor.editor;
using dev.limitex.avatar.compressor.editor.size;
using dev.limitex.avatar.compressor.editor.ui;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace dev.limitex.avatar.compressor.editor.texture.ui
{
    /// <summary>
    /// Draws the preview section with texture analysis results.
    /// Owns its own search box.
    /// </summary>
    /// <remarks>
    /// One per component (<see cref="For"/>), kept outside the inspector: Unity rebuilds
    /// inspectors at times (an avatar copy appearing in the scene, another tool's popup during
    /// the upload-size estimate), and a rebuilt inspector picks up the same preview.
    /// </remarks>
    internal sealed class PreviewSection
    {
        private static readonly Dictionary<int, PreviewSection> s_byComponent =
            new Dictionary<int, PreviewSection>();

        /// <summary>The preview of <paramref name="config"/>, kept across inspector rebuilds.</summary>
        public static PreviewSection For(TextureCompressor config)
        {
            int id = config.GetInstanceID();
            if (!s_byComponent.TryGetValue(id, out var section))
            {
                foreach (int stale in s_byComponent.Keys.Where(k => EditorUtility.InstanceIDToObject(k) == null).ToList())
                    s_byComponent.Remove(stale);
                s_byComponent[id] = section = new PreviewSection();
            }
            return section;
        }

        private readonly PreviewGenerator _generator = new PreviewGenerator();
        private readonly SearchBoxControl _searchBox = new();
        private TexturePreviewData[] _previewData;
        private int _previewSettingsHash;
        private bool _showPreview;
        private Vector2 _scrollPosition;
        private List<Material> _unlockedPoiyomi = new List<Material>();
        private AvatarSizeCheck.Estimate? _uploadEstimate;
        private string _uploadEstimateMessage;
        private bool _estimatePending;
        private static GUIStyle _warningNoteStyle;

        /// <summary>
        /// Draws the preview section.
        /// </summary>
        public void Draw(TextureCompressor config)
        {
            bool isOutdated = IsPreviewOutdated(config);

            if (
                GUILayout.Button(
                    AvatarCompressorLocalization.Tr("TextureCompressor:button:previewGenerate"),
                    GUILayout.Height(35)
                )
            )
            {
                GeneratePreview(config);
                _showPreview = true;
            }

            if (_showPreview && _previewData != null && _previewData.Length > 0)
            {
                if (isOutdated)
                {
                    EditorGUILayout.HelpBox(
                        AvatarCompressorLocalization.Tr(
                            "TextureCompressor:message:previewOutdated"
                        ),
                        MessageType.Warning
                    );
                }
                DrawPreview(config);
            }
            else if (_showPreview && (_previewData == null || _previewData.Length == 0))
            {
                EditorGUILayout.HelpBox(
                    AvatarCompressorLocalization.Tr("TextureCompressor:message:previewNoTextures"),
                    MessageType.Info
                );

                if (GUILayout.Button(AvatarCompressorLocalization.Tr("Common:label:close")))
                {
                    _showPreview = false;
                }
            }
        }

        private void GeneratePreview(TextureCompressor config)
        {
            var analysisBackend = TextureCompressorPreferences.AnalysisBackend;
            var resizeBackend = TextureCompressorPreferences.ResizeBackend;
            _previewSettingsHash = PreviewGenerator.ComputeSettingsHash(
                config,
                analysisBackend,
                resizeBackend
            );
            _previewData = _generator.Generate(config, analysisBackend, resizeBackend);
            _unlockedPoiyomi = PoiyomiLockState.FindUnlocked(config.gameObject);
            _searchBox.InvalidateCountCache();
            _uploadEstimate = null;
            _uploadEstimateMessage = null;
        }

        /// <summary>
        /// Predicts the avatar's upload sizes against VRChat's mobile limits. Runs the avatar's
        /// build steps (VRCFury and other tools) on a copy, so it takes a while and is run on
        /// request rather than with every preview.
        /// </summary>
        private void EstimateUploadSize(TextureCompressor config, GameObject avatar)
        {
            _uploadEstimate = null;
            _uploadEstimateMessage = null;
            try
            {
                _uploadEstimate = AvatarSizeCheck.EstimateUpload(
                    avatar,
                    config,
                    AvatarCompressorLocalization.Tr("TextureCompressor:message:previewUploadEstimating")
                );
            }
            catch (AvatarSizeCheck.BuildStepsFailedException)
            {
                _uploadEstimateMessage = AvatarCompressorLocalization.Tr(
                    "TextureCompressor:message:previewUploadBuildStepsFailed"
                );
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                _uploadEstimateMessage = AvatarCompressorLocalization.Tr(
                    "TextureCompressor:message:previewUploadFailed"
                );
            }
        }

        /// <summary>
        /// Runs <see cref="EstimateUploadSize"/> once the inspector has finished drawing: the
        /// build steps change the scene and can show popups, which must not happen in the
        /// middle of drawing the inspector.
        /// </summary>
        private void QueueEstimate(TextureCompressor config, GameObject avatar)
        {
            if (_estimatePending)
                return;
            _estimatePending = true;
            EditorApplication.delayCall += () =>
            {
                try
                {
                    if (config != null && avatar != null)
                        EstimateUploadSize(config, avatar);
                }
                finally
                {
                    _estimatePending = false;
                    UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                }
            };
        }

        /// <summary>
        /// Target Platform set to the other platform than the build target: sizes come from that
        /// platform's import settings (see <see cref="PlatformImport"/>), which is close but not
        /// what the build target shows.
        /// </summary>
        private static void DrawOtherPlatformNote(TextureCompressor config)
        {
            string platform = PlatformImport.OtherPlatformName(config.TargetPlatform);
            if (platform == null)
                return;
            EditorGUILayout.HelpBox(
                AvatarCompressorLocalization.Tr(
                    platform == "Android"
                        ? "TextureCompressor:message:previewOtherPlatformMobile"
                        : "TextureCompressor:message:previewOtherPlatformDesktop",
                    BuildTargetName()
                ),
                MessageType.Info
            );
        }

        private static string BuildTargetName()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            return target switch
            {
                BuildTarget.StandaloneWindows or BuildTarget.StandaloneWindows64 => "Windows",
                BuildTarget.Android => "Android",
                BuildTarget.iOS => "iOS",
                _ => target.ToString(),
            };
        }

        private void DrawUploadEstimate(TextureCompressor config)
        {
            if (_uploadEstimate.HasValue)
            {
                var estimate = _uploadEstimate.Value;
                bool fits =
                    estimate.Sizes.Download <= AvatarSizeCheck.DownloadTarget
                    && estimate.Sizes.Uncompressed <= AvatarSizeCheck.UncompressedTarget;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    AvatarCompressorLocalization.Tr("TextureCompressor:label:previewUpload"),
                    GUILayout.Width(75)
                );
                Color originalColor = GUI.color;
                GUI.color = fits ? Color.green : new Color(1f, 0.45f, 0.4f);
                EditorGUILayout.LabelField(
                    AvatarCompressorLocalization.Tr(
                        "TextureCompressor:message:previewUploadSizes",
                        AvatarSizeCheck.MiB(estimate.Sizes.Download),
                        AvatarSizeCheck.MiB(AvatarSizeCheck.DownloadLimit),
                        AvatarSizeCheck.MiB(estimate.Sizes.Uncompressed),
                        AvatarSizeCheck.MiB(AvatarSizeCheck.UncompressedLimit)
                    ),
                    EditorStyles.boldLabel,
                    GUILayout.MinWidth(0)
                );
                GUI.color = originalColor;
                EditorGUILayout.EndHorizontal();

                var changes = estimate.Changes ?? new List<AvatarSizeCheck.FitChange>();
                string note =
                    estimate.CannotFit
                        ? AvatarCompressorLocalization.Tr("TextureCompressor:message:previewUploadCannotFit")
                    : changes.Count > 0
                        ? AvatarCompressorLocalization.Tr(
                            "TextureCompressor:message:previewUploadShrinks",
                            changes.Count,
                            string.Join(
                                ", ",
                                changes.Select(c =>
                                    $"{(c.Texture != null ? c.Texture.name : "?")} {Mathf.Max(c.From.x, c.From.y)} -> {Mathf.Max(c.To.x, c.To.y)}"
                                )
                            )
                        )
                    : !fits && !config.KeepUnderSizeLimit
                        ? AvatarCompressorLocalization.Tr("TextureCompressor:message:previewUploadOver")
                    : null;
                if (note != null)
                {
                    _warningNoteStyle ??= new GUIStyle(EditorStyles.wordWrappedMiniLabel)
                    {
                        normal = { textColor = new Color(1f, 0.6f, 0.2f) },
                    };
                    EditorGUILayout.LabelField(note, _warningNoteStyle);
                }
            }
            else if (_uploadEstimateMessage != null)
            {
                EditorGUILayout.LabelField(_uploadEstimateMessage, EditorStyles.wordWrappedMiniLabel);
            }
            else if (!AvatarSizeCheck.IsSupported(out _))
            {
                var target = EditorUserBuildSettings.activeBuildTarget;
                EditorGUILayout.LabelField(
                    target == BuildTarget.Android || target == BuildTarget.iOS
                        ? AvatarCompressorLocalization.Tr(
                            "TextureCompressor:message:previewUploadUnityVersion",
                            Application.unityVersion
                        )
                        : AvatarCompressorLocalization.Tr(
                            "TextureCompressor:message:previewUploadUnsupported",
                            BuildTargetName()
                        ),
                    EditorStyles.wordWrappedMiniLabel
                );
            }
            else
            {
                var descriptor = config.GetComponentInParent<VRCAvatarDescriptor>(true);
                if (descriptor == null)
                    return;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    AvatarCompressorLocalization.Tr("TextureCompressor:label:previewUpload"),
                    GUILayout.Width(75)
                );
                bool estimate;
                using (new EditorGUI.DisabledScope(_estimatePending))
                {
                    estimate = GUILayout.Button(
                        _estimatePending
                            ? new GUIContent(
                                AvatarCompressorLocalization.Tr(
                                    "TextureCompressor:message:previewUploadEstimating"
                                )
                            )
                            : AvatarCompressorLocalization.Content(
                                "TextureCompressor:button:previewEstimateUpload",
                                "TextureCompressor:button:previewEstimateUpload:tooltip"
                            ),
                        GUILayout.ExpandWidth(false)
                    );
                }
                EditorGUILayout.EndHorizontal();
                if (estimate)
                    QueueEstimate(config, descriptor.gameObject);
            }
        }

        private bool IsPreviewOutdated(TextureCompressor config)
        {
            if (!_showPreview || _previewData == null)
                return false;

            return PreviewGenerator.ComputeSettingsHash(
                    config,
                    TextureCompressorPreferences.AnalysisBackend,
                    TextureCompressorPreferences.ResizeBackend
                ) != _previewSettingsHash;
        }

        private void DrawPreview(TextureCompressor config)
        {
            EditorGUILayout.Space(10);

            bool isSearching = _searchBox.IsSearching;
            int totalCount = _previewData.Length;
            int filteredCount = _searchBox.CountMatches(_previewData, MatchesPreviewSearch);

            // Build header text
            string headerText;
            if (isSearching && filteredCount != totalCount)
            {
                headerText =
                    _generator.FrozenCount > 0
                        ? AvatarCompressorLocalization.Tr(
                            "TextureCompressor:message:previewHeaderFilteredFrozen",
                            filteredCount,
                            totalCount,
                            _generator.FrozenCount
                        )
                        : AvatarCompressorLocalization.Tr(
                            "TextureCompressor:message:previewHeaderFiltered",
                            filteredCount,
                            totalCount
                        );
            }
            else
            {
                headerText =
                    _generator.FrozenCount > 0
                        ? AvatarCompressorLocalization.Tr(
                            "TextureCompressor:message:previewHeaderSummaryFrozen",
                            _generator.ProcessedCount,
                            _generator.FrozenCount,
                            _generator.SkippedCount
                        )
                        : AvatarCompressorLocalization.Tr(
                            "TextureCompressor:message:previewHeaderSummary",
                            _generator.ProcessedCount,
                            _generator.SkippedCount
                        );
            }
            EditorGUILayout.LabelField(headerText, EditorStyles.boldLabel);

            // Calculate totals
            long totalOriginal = 0;
            long totalAfter = 0;
            foreach (var data in _previewData)
            {
                totalOriginal += data.OriginalMemory;
                totalAfter += data.EstimatedMemory;
            }

            float savings = totalOriginal > 0 ? 1f - (float)totalAfter / totalOriginal : 0f;

            // Draw summary box
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                AvatarCompressorLocalization.Tr("TextureCompressor:label:previewOriginal"),
                GUILayout.Width(75)
            );
            EditorGUILayout.LabelField(
                MemoryCalculator.FormatBytes(totalOriginal),
                EditorStyles.boldLabel
            );
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                AvatarCompressorLocalization.Tr("TextureCompressor:label:previewAfter"),
                GUILayout.Width(75)
            );
            EditorGUILayout.LabelField(
                MemoryCalculator.FormatBytes(totalAfter),
                EditorStyles.boldLabel
            );
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                AvatarCompressorLocalization.Tr("TextureCompressor:label:previewSavings"),
                GUILayout.Width(75)
            );
            Color originalColor = GUI.color;
            GUI.color = Color.green;
            long savedBytes = totalOriginal - totalAfter;
            EditorGUILayout.LabelField(
                $"{savings:P0} (-{MemoryCalculator.FormatBytes(savedBytes)})",
                EditorStyles.boldLabel
            );
            GUI.color = originalColor;
            EditorGUILayout.EndHorizontal();

            DrawUploadEstimate(config);

            EditorGUILayout.EndVertical();

            if (config.DetectUnusedTextures || config.BakeLilToonTextures)
            {
                string helpKey =
                    config.DetectUnusedTextures && config.BakeLilToonTextures
                        ? "TextureCompressor:message:previewBuildOnlyBoth"
                    : config.DetectUnusedTextures
                        ? "TextureCompressor:message:previewBuildOnlyUnused"
                    : "TextureCompressor:message:previewBuildOnlyBake";

                EditorGUILayout.HelpBox(AvatarCompressorLocalization.Tr(helpKey), MessageType.Info);
            }

            DrawOtherPlatformNote(config);
            DrawUnlockedPoiyomi(config);

            // Search box for preview textures
            _searchBox.Draw(filteredCount, totalCount);

            // Show "no results" message when searching with no matches
            if (isSearching && filteredCount == 0)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox(
                    AvatarCompressorLocalization.Tr(
                        "TextureCompressor:message:previewNoSearchResults"
                    ),
                    MessageType.Info
                );
            }
            else
            {
                EditorGUILayout.Space(5);

                _scrollPosition = EditorGUILayout.BeginScrollView(
                    _scrollPosition,
                    GUILayout.MaxHeight(300)
                );

                bool hasDrawnProcessedHeader = false;
                bool hasDrawnFrozenHeader = false;
                bool hasDrawnSkippedHeader = false;

                for (int i = 0; i < _previewData.Length; i++)
                {
                    var data = _previewData[i];

                    // Skip items that don't match search
                    if (isSearching && !MatchesPreviewSearch(data))
                        continue;

                    // Section headers
                    if (data.IsProcessed && !data.IsFrozen && !hasDrawnProcessedHeader)
                    {
                        EditorGUILayout.LabelField(
                            AvatarCompressorLocalization.Tr(
                                "TextureCompressor:label:previewToCompress"
                            ),
                            EditorStyles.boldLabel
                        );
                        hasDrawnProcessedHeader = true;
                    }

                    if (data.IsProcessed && data.IsFrozen && !hasDrawnFrozenHeader)
                    {
                        EditorGUILayout.Space(10);
                        EditorGUILayout.LabelField(
                            AvatarCompressorLocalization.Tr(
                                "TextureCompressor:label:previewFrozenManual"
                            ),
                            EditorStyles.boldLabel
                        );
                        hasDrawnFrozenHeader = true;
                    }

                    if (!data.IsProcessed && !hasDrawnSkippedHeader)
                    {
                        EditorGUILayout.Space(10);
                        EditorGUILayout.LabelField(
                            AvatarCompressorLocalization.Tr(
                                "TextureCompressor:label:previewSkippedTextures"
                            ),
                            EditorStyles.boldLabel
                        );
                        hasDrawnSkippedHeader = true;
                    }

                    DrawPreviewEntry(config, data);
                }

                EditorGUILayout.EndScrollView();

                // Show hidden count when searching
                if (isSearching && filteredCount < totalCount)
                {
                    EditorDrawUtils.DrawHiddenCount(totalCount - filteredCount);
                }
            }

            EditorGUILayout.Space(5);

            if (
                GUILayout.Button(
                    AvatarCompressorLocalization.Tr("TextureCompressor:button:previewClose")
                )
            )
            {
                _showPreview = false;
                _previewData = null;
            }
        }

        /// <summary>
        /// Poiyomi locks its materials on upload before the compressor runs and drops the textures
        /// of features that are off, so unlocked materials show textures the upload won't contain.
        /// </summary>
        private void DrawUnlockedPoiyomi(TextureCompressor config)
        {
            _unlockedPoiyomi.RemoveAll(m => m == null || m.GetFloat("_ShaderOptimizerEnabled") >= 0.5f);
            if (_unlockedPoiyomi.Count == 0)
                return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.HelpBox(
                AvatarCompressorLocalization.Tr(
                    "TextureCompressor:message:previewUnlockedPoiyomi",
                    _unlockedPoiyomi.Count,
                    string.Join(", ", _unlockedPoiyomi.Take(5).Select(m => m.name))
                        + (_unlockedPoiyomi.Count > 5 ? ", ..." : "")
                ),
                MessageType.Info
            );
            if (
                PoiyomiLockState.CanLock
                && GUILayout.Button(
                    AvatarCompressorLocalization.Tr("TextureCompressor:button:previewLockPoiyomi")
                )
            )
            {
                PoiyomiLockState.Lock(_unlockedPoiyomi);
                GeneratePreview(config);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawPreviewEntry(TextureCompressor config, TexturePreviewData data)
        {
            bool isSkipped = !data.IsProcessed;
            // A texture following a frozen slot is frozen through that entry, not its own
            bool frozenStateChanged =
                data.FollowsGuid == null && data.IsFrozen != config.IsFrozen(data.Guid);

            if (isSkipped || frozenStateChanged)
            {
                EditorGUI.BeginDisabledGroup(true);
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            EditorGUILayout.BeginVertical(GUILayout.Width(45));
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal(GUILayout.Width(45));
            GUILayout.FlexibleSpace();
            ThumbnailControl.DrawClickable(data.Texture);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(
                AvatarCompressorLocalization.EnumValue("TextureCompressor", data.TextureType),
                EditorStylesCache.CenteredBoldLabel,
                GUILayout.Width(45)
            );
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                data.Texture.name,
                EditorStylesCache.ClippedBoldLabel,
                GUILayout.MinWidth(0)
            );

            // Freeze/Unfreeze button
            if (data.FollowsGuid != null)
            {
                EditorGUILayout.LabelField(
                    AvatarCompressorLocalization.Tr(
                        "TextureCompressor:label:previewFollowsSlot",
                        System.IO.Path.GetFileNameWithoutExtension(
                            AssetDatabase.GUIDToAssetPath(data.FollowsGuid)
                        )
                    ),
                    EditorStyles.miniLabel,
                    GUILayout.MinWidth(0)
                );
            }
            else if (data.IsProcessed)
            {
                if (data.IsFrozen)
                {
                    var savedColor = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
                    if (
                        GUILayout.Button(
                            AvatarCompressorLocalization.Tr("TextureCompressor:button:unfreeze"),
                            GUILayout.Width(85)
                        )
                    )
                    {
                        Undo.RecordObject(config, "Unfreeze Texture");
                        config.UnfreezeTexture(data.Guid);
                        EditorUtility.SetDirty(config);
                    }
                    GUI.backgroundColor = savedColor;
                }
                else
                {
                    if (
                        GUILayout.Button(
                            AvatarCompressorLocalization.Tr(
                                "TextureCompressor:button:previewFreeze"
                            ),
                            GUILayout.Width(85)
                        )
                    )
                    {
                        Undo.RecordObject(config, "Freeze Texture");
                        var frozenSettings = new FrozenTextureSettings(
                            data.Guid,
                            data.RecommendedDivisor,
                            FrozenTextureFormat.Auto,
                            false
                        );
                        config.SetFrozenSettings(data.Guid, frozenSettings);
                        EditorUtility.SetDirty(config);
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            if (data.IsProcessed)
            {
                DrawProcessedDetails(data);
            }
            else
            {
                DrawSkippedDetails(data);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            if (isSkipped || frozenStateChanged)
            {
                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawProcessedDetails(TexturePreviewData data)
        {
            // Complexity bar
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                AvatarCompressorLocalization.Tr("TextureCompressor:label:previewComplexity"),
                GUILayout.Width(85)
            );

            Color complexityColor = Color.Lerp(Color.green, Color.red, data.Complexity);
            EditorDrawUtils.DrawProgressBar(data.Complexity, 100, 16, complexityColor);

            EditorGUILayout.LabelField($"{data.Complexity:P0}", GUILayout.Width(45));
            EditorGUILayout.EndHorizontal();

            // Size
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                AvatarCompressorLocalization.Tr("TextureCompressor:label:previewSize"),
                GUILayout.Width(85)
            );

            string sizeText;
            string manualIndicator = data.IsFrozen
                ? AvatarCompressorLocalization.Tr("TextureCompressor:label:previewManualSuffix")
                : "";
            // Compare sizes, not the divisor: Max Resolution can shrink a texture at 1x, the
            // texture can be resized from a larger source file, and limits can keep it at its
            // imported size whatever the divisor says.
            if (data.RecommendedSize == data.OriginalSize)
            {
                sizeText = AvatarCompressorLocalization.Tr(
                    "TextureCompressor:message:previewUnchanged",
                    data.OriginalSize.x,
                    data.OriginalSize.y,
                    manualIndicator
                );
            }
            else if (data.RecommendedSize.x > data.OriginalSize.x)
            {
                sizeText = AvatarCompressorLocalization.Tr(
                    "TextureCompressor:message:previewEnlarged",
                    data.OriginalSize.x,
                    data.OriginalSize.y,
                    data.RecommendedSize.x,
                    data.RecommendedSize.y,
                    manualIndicator
                );
            }
            else
            {
                sizeText = AvatarCompressorLocalization.Tr(
                    "TextureCompressor:message:previewResized",
                    data.OriginalSize.x,
                    data.OriginalSize.y,
                    data.RecommendedSize.x,
                    data.RecommendedSize.y,
                    Mathf.Max(
                        1,
                        Mathf.RoundToInt(data.OriginalSize.x / (float)data.RecommendedSize.x)
                    ),
                    manualIndicator
                );
            }
            EditorGUILayout.LabelField(sizeText, GUILayout.MinWidth(0));
            EditorGUILayout.EndHorizontal();

            if (data.Density.HasValue)
            {
                var density = data.Density.Value;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    AvatarCompressorLocalization.Tr("TextureCompressor:label:previewOnAvatar"),
                    GUILayout.Width(85)
                );
                EditorGUILayout.LabelField(
                    AvatarCompressorLocalization.Tr(
                        "TextureCompressor:message:previewDensity",
                        density.DensityAt(data.BaseSize).ToString("F0"),
                        density.DensityAt(data.RecommendedSize).ToString("F0"),
                        density.TargetDensity.ToString("F0"),
                        AvatarCompressorLocalization.Tr(
                            density.FromHead
                                ? "TextureCompressor:label:densityHead"
                                : "TextureCompressor:label:densityBody"
                        )
                    ),
                    EditorStyles.miniLabel,
                    GUILayout.MinWidth(0)
                );
                EditorGUILayout.EndHorizontal();

                if (data.MaxResolutionWanted.HasValue)
                {
                    var wanted = data.MaxResolutionWanted.Value;
                    EditorGUILayout.HelpBox(
                        AvatarCompressorLocalization.Tr(
                            "TextureCompressor:message:previewMaxResolutionLimit",
                            wanted.x,
                            wanted.y,
                            density.DensityAt(wanted).ToString("F0"),
                            (
                                (float)wanted.x
                                * wanted.y
                                / ((float)data.RecommendedSize.x * data.RecommendedSize.y)
                            ).ToString("F0")
                        ),
                        MessageType.Warning
                    );
                }
            }

            // Format
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                AvatarCompressorLocalization.Tr("TextureCompressor:label:previewFormat"),
                GUILayout.Width(85)
            );
            if (data.PredictedFormat.HasValue)
            {
                string formatName = TextureFormatUtils.GetDisplayName(data.PredictedFormat.Value);
                string formatInfo = TextureFormatUtils.GetInfo(data.PredictedFormat.Value);
                if (
                    data.IsFrozen
                    && data.FrozenSettings != null
                    && data.FrozenSettings.Format != FrozenTextureFormat.Auto
                )
                {
                    formatInfo += AvatarCompressorLocalization.Tr(
                        "TextureCompressor:label:previewManualSuffix"
                    );
                }
                var formatColor = TextureFormatUtils.GetColor(data.PredictedFormat.Value);

                var savedGuiColor = GUI.color;
                GUI.color = formatColor;
                EditorGUILayout.LabelField(formatName, EditorStyles.boldLabel, GUILayout.Width(70));
                GUI.color = savedGuiColor;
                EditorGUILayout.LabelField(
                    formatInfo,
                    EditorStyles.miniLabel,
                    GUILayout.MinWidth(0)
                );
            }
            else
            {
                EditorGUILayout.LabelField(
                    AvatarCompressorLocalization.Tr("Common:label:notAvailable"),
                    EditorStyles.miniLabel
                );
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSkippedDetails(TexturePreviewData data)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                AvatarCompressorLocalization.Tr("TextureCompressor:label:previewSize"),
                GUILayout.Width(85)
            );
            EditorGUILayout.LabelField($"{data.OriginalSize.x}x{data.OriginalSize.y}");
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                AvatarCompressorLocalization.Tr("TextureCompressor:label:previewReason"),
                GUILayout.Width(85)
            );
            string reasonText = AvatarCompressorLocalization.EnumValue(
                "TextureCompressor",
                data.SkipReason
            );
            EditorGUILayout.LabelField(reasonText, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private bool MatchesPreviewSearch(TexturePreviewData data)
        {
            string assetPath = GuidPathCache.GetPath(data.Guid);
            string textureName = data.Texture != null ? data.Texture.name : "";

            return _searchBox.MatchesSearchAny(
                textureName,
                assetPath,
                data.TextureType.ToString(),
                AvatarCompressorLocalization.EnumValue("TextureCompressor", data.TextureType)
            );
        }
    }
}
