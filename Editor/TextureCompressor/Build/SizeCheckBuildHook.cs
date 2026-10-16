using System.Collections.Generic;
using System.Linq;
using dev.limitex.avatar.compressor.editor.size;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Checks the avatar's sizes against VRChat's mobile limits after every other build step, so
    /// it measures what is uploaded (Keep under the mobile size limit), and tells the user when
    /// textures were made smaller to fit or the avatar is still over.
    /// </summary>
    /// <remarks>
    /// The report goes to the console always. During an upload from the SDK's panel it is also
    /// shown in a popup that can cancel the upload. When the Batch Uploader runs the upload, it
    /// asks for the report instead (<see cref="ReportReaderKey"/>) and lists it in its summary
    /// at the end, so a batch never stops on a popup.
    /// </remarks>
    internal sealed class SizeCheckBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        /// <summary>Avatar copies whose compression asked for the check, with what the plan did.</summary>
        internal static readonly Dictionary<GameObject, AvatarSizeCheck.FitPlan> Pending =
            new Dictionary<GameObject, AvatarSizeCheck.FitPlan>();

        /// <summary>
        /// SessionState flag a tool running the uploads sets to collect the reports instead of
        /// the popup.
        /// </summary>
        internal const string ReportReaderKey = "Synthos.AvatarCompressor.SizeReportReader";

        /// <summary>
        /// SessionState string the reports are added to while <see cref="ReportReaderKey"/> is set:
        /// one line per report, "W" (over the limit) or "I" (made smaller to fit), a tab, the text.
        /// The reader clears it after reading.
        /// </summary>
        internal const string ReportsKey = "Synthos.AvatarCompressor.SizeReports";

        private const int MaxListedTextures = 12;

        public int callbackOrder => int.MaxValue;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            foreach (var stale in Pending.Keys.Where(k => k == null).ToList())
                Pending.Remove(stale);
            if (!Pending.TryGetValue(avatarGameObject, out var plan))
                return true;
            Pending.Remove(avatarGameObject);

            AvatarSizeCheck.Sizes sizes;
            bool over;
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                sizes = AvatarSizeCheck.Measure(avatarGameObject);
                over = sizes.Download > AvatarSizeCheck.DownloadTarget || sizes.Uncompressed > AvatarSizeCheck.UncompressedTarget;
                string summary =
                    $"download {AvatarSizeCheck.MiB(sizes.Download)} / {AvatarSizeCheck.MiB(AvatarSizeCheck.DownloadLimit)}, "
                    + $"uncompressed {AvatarSizeCheck.MiB(sizes.Uncompressed)} / {AvatarSizeCheck.MiB(AvatarSizeCheck.UncompressedLimit)} "
                    + $"({timer.ElapsedMilliseconds / 1000.0:0.0} s)";
                if (over)
                    Debug.LogWarning($"[Avatar Compressor] Size check: {summary}. The avatar may be over VRChat's mobile limit.", avatarGameObject);
                else
                    Debug.Log($"[Avatar Compressor] Size check: {summary}.", avatarGameObject);
            }
            catch (System.Exception ex)
            {
                // a failed check never stops the upload
                Debug.LogWarning($"[Avatar Compressor] Size check failed: {ex.Message}");
                return true;
            }

            if (!over && plan.Changes.Count == 0)
                return true;
            return Report(AvatarName(avatarGameObject), plan.Changes, sizes, over);
        }

        /// <summary>Hands the report to the Batch Uploader, or shows it. False cancels the upload.</summary>
        private static bool Report(string avatar, List<AvatarSizeCheck.FitChange> changes, AvatarSizeCheck.Sizes sizes, bool over)
        {
            string sizeText = $"{AvatarSizeCheck.MiB(sizes.Download)} download, {AvatarSizeCheck.MiB(sizes.Uncompressed)} uncompressed";
            if (SessionState.GetBool(ReportReaderKey, false))
            {
                string line = over
                    ? $"W\tStill over the mobile size limit ({sizeText})"
                        + (changes.Count > 0 ? $" after making {changes.Count} textures smaller: {ChangeList(changes, ", ")}" : "")
                    : $"I\tMade {changes.Count} textures smaller to fit ({sizeText}): {ChangeList(changes, ", ")}";
                SessionState.SetString(ReportsKey, SessionState.GetString(ReportsKey, "") + line.Replace('\n', ' ') + "\n");
                return true;
            }
            if (!SdkBuildTracker.IsBuilding || Application.isBatchMode)
                return true;

            string list = ChangeList(changes, "\n");
            string message = over
                ? AvatarCompressorLocalization.Tr(
                    changes.Count > 0 ? "TextureCompressor:dialog:sizeStillOver" : "TextureCompressor:dialog:sizeOver",
                    avatar,
                    AvatarSizeCheck.MiB(sizes.Download),
                    AvatarSizeCheck.MiB(sizes.Uncompressed),
                    list
                )
                : AvatarCompressorLocalization.Tr(
                    "TextureCompressor:dialog:sizeAdjusted",
                    avatar,
                    AvatarSizeCheck.MiB(sizes.Download),
                    AvatarSizeCheck.MiB(sizes.Uncompressed),
                    list
                );
            bool proceed = EditorUtility.DisplayDialog(
                AvatarCompressorLocalization.Tr(over ? "TextureCompressor:dialog:sizeOverTitle" : "TextureCompressor:dialog:sizeAdjustedTitle"),
                message,
                AvatarCompressorLocalization.Tr("TextureCompressor:dialog:continueUpload"),
                AvatarCompressorLocalization.Tr("TextureCompressor:dialog:cancelUpload")
            );
            if (!proceed)
                Debug.Log("[Avatar Compressor] Upload cancelled from the size report.");
            return proceed;
        }

        private static string ChangeList(List<AvatarSizeCheck.FitChange> changes, string separator)
        {
            var lines = changes.Take(MaxListedTextures)
                .Select(c => $"{(c.Texture != null ? c.Texture.name : "?")}: {c.From.x}x{c.From.y} -> {c.To.x}x{c.To.y}")
                .ToList();
            if (changes.Count > MaxListedTextures)
                lines.Add($"(+{changes.Count - MaxListedTextures})");
            return string.Join(separator, lines);
        }

        private static string AvatarName(GameObject avatar) => avatar.name.Replace("(Clone)", "").Trim();
    }
}
