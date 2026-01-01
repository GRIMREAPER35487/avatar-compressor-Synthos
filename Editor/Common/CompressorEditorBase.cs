using UnityEngine;
using UnityEditor;

namespace dev.limitex.avatar.compressor.editor
{
    /// <summary>
    /// Base class for compressor custom editors with common utilities.
    /// </summary>
    public abstract class CompressorEditorBase : Editor
    {
        protected bool _showAdvancedSettings;
        protected Vector2 _scrollPosition;

        /// <summary>
        /// Draws a colored button.
        /// </summary>
        protected bool DrawColoredButton(string label, string tooltip, Color color, bool isSelected, float height = 40f)
        {
            GUIStyle style = new GUIStyle(GUI.skin.button)
            {
                fontStyle = isSelected ? FontStyle.Bold : FontStyle.Normal,
                fixedHeight = height
            };

            Color originalBg = GUI.backgroundColor;
            if (isSelected)
            {
                GUI.backgroundColor = color;
            }

            bool clicked = GUILayout.Button(new GUIContent(label, tooltip), style, GUILayout.ExpandWidth(true));

            GUI.backgroundColor = originalBg;

            return clicked;
        }

        /// <summary>
        /// Draws a progress bar with custom color.
        /// </summary>
        protected void DrawProgressBar(float value, float width, float height, Color fillColor)
        {
            Rect progressRect = EditorGUILayout.GetControlRect(GUILayout.Width(width), GUILayout.Height(height));
            EditorGUI.DrawRect(progressRect, new Color(0.2f, 0.2f, 0.2f));
            Rect filledRect = new Rect(progressRect.x, progressRect.y, progressRect.width * value, progressRect.height);
            EditorGUI.DrawRect(filledRect, fillColor);
        }

        /// <summary>
        /// Draws a help box with message type.
        /// </summary>
        protected void DrawHelpBox(string message, MessageType type)
        {
            EditorGUILayout.HelpBox(message, type);
        }

        /// <summary>
        /// Draws a section header.
        /// </summary>
        protected void DrawSectionHeader(string label)
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
        }

        /// <summary>
        /// Draws a horizontal line separator.
        /// </summary>
        protected void DrawSeparator()
        {
            EditorGUILayout.Space(5);
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
            EditorGUILayout.Space(5);
        }

        /// <summary>
        /// Begins a boxed section.
        /// </summary>
        protected void BeginBox()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        }

        /// <summary>
        /// Ends a boxed section.
        /// </summary>
        protected void EndBox()
        {
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Formats bytes to human-readable string.
        /// </summary>
        protected string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / 1024f / 1024f:F2} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024f:F2} KB";
            return $"{bytes} B";
        }
    }
}
