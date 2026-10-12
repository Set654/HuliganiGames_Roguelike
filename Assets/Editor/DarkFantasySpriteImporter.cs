#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

public sealed class DarkFantasySpriteImporter : AssetPostprocessor
{
    private void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith("Assets/Resources/GameArt/")) return;
        TextureImporter ti = (TextureImporter)assetImporter;
        ti.textureType = TextureImporterType.Sprite;
        ti.spriteImportMode = SpriteImportMode.Single;
        ti.spritePixelsPerUnit = 128f;
        ti.alphaIsTransparency = true;
        ti.filterMode = FilterMode.Point;
        ti.mipmapEnabled = false;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.textureCompression = TextureImporterCompression.Uncompressed;
        ti.npotScale = TextureImporterNPOTScale.None;
        ti.isReadable = false;
        ti.maxTextureSize = 2048;
        ti.spritePivot = new Vector2(0.5f, 0.5f);
    }
}
#endif
