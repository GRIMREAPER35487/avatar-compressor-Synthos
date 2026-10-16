using System.Collections.Generic;
using dev.limitex.avatar.compressor.editor.size;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Checks the avatar's sizes against VRChat's mobile limits after every other build step, so
    /// it measures what is uploaded (Keep under the mobile size limit).
    /// </summary>
    internal sealed class SizeCheckBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        /// <summary>Avatar copies whose compression asked for the check.</summary>
        internal static readonly HashSet<GameObject> Pending = new HashSet<GameObject>();

        public int callbackOrder => int.MaxValue;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            Pending.RemoveWhere(a => a == null);
            if (!Pending.Remove(avatarGameObject))
                return true;
            try
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var sizes = AvatarSizeCheck.Measure(avatarGameObject);
                string summary =
                    $"download {AvatarSizeCheck.MiB(sizes.Download)} / {AvatarSizeCheck.MiB(AvatarSizeCheck.DownloadLimit)}, "
                    + $"uncompressed {AvatarSizeCheck.MiB(sizes.Uncompressed)} / {AvatarSizeCheck.MiB(AvatarSizeCheck.UncompressedLimit)} "
                    + $"({timer.ElapsedMilliseconds / 1000.0:0.0} s)";
                if (sizes.Download > AvatarSizeCheck.DownloadTarget || sizes.Uncompressed > AvatarSizeCheck.UncompressedTarget)
                    Debug.LogWarning($"[Avatar Compressor] Size check: {summary}. The avatar may be over VRChat's mobile limit.", avatarGameObject);
                else
                    Debug.Log($"[Avatar Compressor] Size check: {summary}.", avatarGameObject);
            }
            catch (System.Exception ex)
            {
                // a failed check never stops the upload
                Debug.LogWarning($"[Avatar Compressor] Size check failed: {ex.Message}");
            }
            return true;
        }
    }
}
