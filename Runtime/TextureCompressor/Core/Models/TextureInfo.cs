using System.Collections.Generic;
using UnityEngine;

namespace dev.limitex.avatar.compressor.texture
{
    /// <summary>
    /// Information about a texture and its usage context.
    /// </summary>
    public class TextureInfo
    {
        public string TextureType { get; set; }
        public string PropertyName { get; set; }
        public bool IsNormalMap { get; set; }
        public bool IsEmission { get; set; }
        public List<MaterialTextureReference> References { get; } = new List<MaterialTextureReference>();
    }

    /// <summary>
    /// Reference to a texture in a material.
    /// </summary>
    public class MaterialTextureReference
    {
        public Material Material { get; set; }
        public string PropertyName { get; set; }
        public Renderer Renderer { get; set; }
    }
}
