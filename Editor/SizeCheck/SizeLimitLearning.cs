using System.Linq;
using UnityEditor;
using UnityEngine;

namespace dev.limitex.avatar.compressor.editor.size
{
    /// <summary>
    /// Lets an upload tool (the Synthos Batch Uploader, through reflection) ask whether an avatar
    /// should get a build-only "learning" pass before it is uploaded: "Keep under the mobile size
    /// limit" learns from each build how far its download prediction was off for that avatar
    /// (<see cref="AvatarSizeCheck.Learn"/>), so a build first makes the real upload's plan fit
    /// closer to the limit.
    /// </summary>
    public static class SizeLimitLearning
    {
        /// <summary>
        /// True when a build of <paramref name="avatarRoot"/> for <paramref name="target"/> would
        /// teach the size plan something: the avatar keeps under the mobile size limit, the target
        /// is Android or iOS, and no build of this avatar ID on that target has been checked yet.
        /// </summary>
        /// <param name="avatarId">The VRChat avatar ID the upload goes to (or empty to read it from the avatar).</param>
        public static bool NeedsLearningBuild(GameObject avatarRoot, string avatarId, BuildTarget target)
        {
            if (avatarRoot == null || (target != BuildTarget.Android && target != BuildTarget.iOS))
                return false;
            if (!Application.unityVersion.StartsWith("2022.3."))
                return false;
            var settings = avatarRoot.GetComponentsInChildren<TextureCompressor>(true).FirstOrDefault();
            if (settings == null || !settings.KeepUnderSizeLimit)
                return false;
            if (string.IsNullOrWhiteSpace(avatarId))
                avatarId = AvatarSizeCheck.AvatarId(avatarRoot);
            return !EditorPrefs.HasKey(AvatarSizeCheck.LearnedKey(avatarId, target));
        }
    }
}
