using System;
using Synthos.TextureAnalysis;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace dev.limitex.avatar.compressor.editor.texture
{
    /// <summary>
    /// Compresses avatars in Play Mode that VRCFury does not process.
    /// </summary>
    /// <remarks>
    /// VRCFury runs every avatar build hook (including ours) when entering Play Mode, but only
    /// on avatars that have at least one VRCFury component. Avatars without one would otherwise
    /// show uncompressed textures in Play Mode. Avatars VRCFury handles are skipped here, so no
    /// avatar is compressed twice.
    /// </remarks>
    [InitializeOnLoad]
    internal static class PlayModeCompressionTrigger
    {
        private const string VrcfuryComponentTypeName = "VRCFuryComponent";
        private const string VrcfuryDebugInfoTypeName = "VRCFuryDebugInfo";

        static PlayModeCompressionTrigger()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
                return;

            foreach (var descriptor in Object.FindObjectsOfType<VRCAvatarDescriptor>())
            {
                var avatar = descriptor.gameObject;
                if (!avatar.activeInHierarchy || IsAv3EmulatorClone(avatar.transform))
                    continue;
                if (avatar.GetComponentInChildren<TextureCompressor>(true) == null)
                    continue;
                if (HasVrcfuryComponent(avatar))
                    continue;

                try
                {
                    TextureCompressorBuildHook.Process(avatar);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, avatar);
                }
            }
        }

        /// <summary>
        /// Mirrors VRCFuryBuilder.ShouldRun: VRCFury processes an avatar in Play Mode when it has
        /// any VRCFury component other than its debug info.
        /// </summary>
        private static bool HasVrcfuryComponent(GameObject avatar)
        {
            foreach (var behaviour in avatar.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null)
                    continue;

                var type = behaviour.GetType();
                if (type.Name == VrcfuryDebugInfoTypeName)
                    continue;

                for (var current = type; current != null; current = current.BaseType)
                {
                    if (current.Name == VrcfuryComponentTypeName)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Matches VRCFury's check for Av3Emulator's shadow and mirror clones.
        /// </summary>
        private static bool IsAv3EmulatorClone(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                if (
                    current.name.Contains("(ShadowClone)")
                    || current.name.Contains("(MirrorReflection)")
                )
                {
                    return true;
                }
            }

            return false;
        }
    }
}
