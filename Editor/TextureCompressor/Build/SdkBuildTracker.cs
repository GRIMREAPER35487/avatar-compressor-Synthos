using UnityEditor;
using VRC.SDK3A.Editor;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Whether the VRChat SDK's builder is building an avatar right now (Build and Upload, or
    /// Build and Test), as opposed to Play Mode, a test build or the upload-size estimate.
    /// </summary>
    internal static class SdkBuildTracker
    {
        public static bool IsBuilding { get; private set; }

        [InitializeOnLoadMethod]
        private static void Init()
        {
            VRCSdkControlPanel.OnSdkPanelEnable += (_, _2) =>
            {
                if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder))
                {
                    // The panel is enabled again on every reopen: subscribe once
                    builder.OnSdkBuildStart -= OnBuildStart;
                    builder.OnSdkBuildStart += OnBuildStart;
                    builder.OnSdkBuildFinish -= OnBuildFinish;
                    builder.OnSdkBuildFinish += OnBuildFinish;
                    builder.OnSdkBuildError -= OnBuildFinish;
                    builder.OnSdkBuildError += OnBuildFinish;
                }
            };
        }

        private static void OnBuildStart(object sender, object target) => IsBuilding = true;

        private static void OnBuildFinish(object sender, string result) => IsBuilding = false;
    }
}
