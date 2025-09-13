﻿using Prowl.Scribe.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Prowl.Scribe
{
    public class FontSystem
    {
        private readonly IFontRenderer renderer;
        private readonly BinPacker binPacker;
        private readonly List<FontFile> fallbackFonts;
        private readonly Dictionary<AtlasGlyph.CacheKey, AtlasGlyph> glyphCache;

        readonly LruCache<LayoutCacheKey, TextLayout> layoutCache;

        private object atlasTexture;
        private int atlasWidth;
        private int atlasHeight;

        private bool useWhiteRect;
        private float whiteU0, whiteV0, whiteU1, whiteV1;

        // Settings
        public bool AllowExpansion { get; set; } = true;
        public float ExpansionFactor { get; set; } = 2f;
        public int MaxAtlasSize { get; set; } = 4096;
        public int Padding { get; set; } = 1;
        int _maxLayout = 256;
        public int MaxLayoutCacheSize {
            get => _maxLayout;
            set { _maxLayout = Math.Max(1, value); layoutCache.Capacity = _maxLayout; }
        }
        public bool CacheLayouts { get; set; } = false;

        public IEnumerable<FontFile> FallbackFonts => fallbackFonts;
        public int Width => atlasWidth;
        public int Height => atlasHeight;
        public object Texture => atlasTexture;
        public int FontCount => fallbackFonts.Count;

        public FontSystem(IFontRenderer renderer, int initialWidth = 512, int initialHeight = 512, bool includeWhiteRect = true)
        {
            this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

            atlasWidth = initialWidth;
            atlasHeight = initialHeight;

            this.useWhiteRect = includeWhiteRect;

            atlasTexture = renderer.CreateTexture(atlasWidth, atlasHeight);
            binPacker = new BinPacker(atlasWidth, atlasHeight);
            fallbackFonts = new List<FontFile>();
            glyphCache = new Dictionary<AtlasGlyph.CacheKey, AtlasGlyph>();
            layoutCache = new LruCache<LayoutCacheKey, TextLayout>(_maxLayout);

            // Add a small white rectangle for rendering
            if (useWhiteRect)
                AddWhiteRect();
        }

        public void AddWhiteRect()
        {
            if (binPacker.TryPack(4 + Padding * 2, 4 + Padding * 2, out int x, out int y))
            {
                byte[] whiteData = new byte[4 * 4];
                Array.Fill<byte>(whiteData, 255);

                renderer.UpdateTextureRegion(atlasTexture,
                    new AtlasRect(x, y, 4, 4), whiteData);

                whiteU0 = (float)x / atlasWidth;
                whiteV0 = (float)y / atlasHeight;
                whiteU1 = (float)(x + 1) / atlasWidth;
                whiteV1 = (float)(y + 1) / atlasHeight;
            }
        }

        public void AddFallbackFont(FontFile font)
        {
            fallbackFonts.Add(font);

            glyphCache.Clear();
        }

        public IEnumerable<FontFile> EnumerateSystemFonts()
        {
            var paths = GetSystemFontPaths();
            foreach (var path in paths)
            {
                FontFile font = null;
                try
                {
                    font = new FontFile(path);
                }
                catch
                {
                    continue; // Silently skip problematic fonts
                }
                if (font != null)
                    yield return font;
            }
        }

        private IEnumerable<string> GetSystemFontPaths()
        {
            // De-dupe final results
            var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Safe enumerator that handles permissions and missing dirs
            IEnumerable<string> EnumerateFontsUnder(string root)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    yield break;

                var stack = new Stack<string>();
                stack.Push(root);

                while (stack.Count > 0)
                {
                    string dir = stack.Pop();

                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(dir); }
                    catch { files = Array.Empty<string>(); }

                    foreach (var f in files)
                    {
                        string ext;
                        try { ext = Path.GetExtension(f); }
                        catch { continue; }

                        if (string.Equals(ext, ".ttf", StringComparison.OrdinalIgnoreCase) && yielded.Add(f))
                            yield return f;
                    }

                    IEnumerable<string> subdirs;
                    try { subdirs = Directory.EnumerateDirectories(dir); }
                    catch { subdirs = Array.Empty<string>(); }

                    foreach (var d in subdirs)
                        stack.Push(d);
                }
            }

            // Build OS-specific search roots
            var roots = new List<string>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // System fonts
                roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));

                // Per-user fonts (Windows 10+)
                var userFonts = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Fonts");
                roots.Add(userFonts);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // System & local fonts
                roots.Add("/usr/share/fonts");
                roots.Add("/usr/local/share/fonts");

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                roots.Add(Path.Combine(home, ".fonts"));                  // legacy
                roots.Add(Path.Combine(home, ".local", "share", "fonts"));// modern
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // System & local fonts
                roots.Add("/System/Library/Fonts");
                roots.Add("/System/Library/Fonts/Supplemental");
                roots.Add("/Library/Fonts");

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                roots.Add(Path.Combine(home, "Library", "Fonts"));
            }

            foreach (var r in roots.Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var f in EnumerateFontsUnder(r))
                    yield return f;
        }

        public AtlasGlyph GetOrCreateGlyph(int codepoint, float pixelSize, FontFile font)
        {
            if(font == null) throw new ArgumentNullException(nameof(font));

            var glyph = TryGetGlyphFromFont(font);
            if (glyph != null)
                return glyph;

            // Check Fallback Fonts
            foreach (var f in fallbackFonts)
            {
                if (f == font) continue;
                if (f.Style != font.Style) continue; // Needs to match style to what the user requested

                glyph = TryGetGlyphFromFont(f);
                if (glyph != null)
                    return glyph;
            }

            return null; // Glyph not found in any font

            AtlasGlyph TryGetGlyphFromFont(FontFile font)
            {
                if (font.FindGlyphIndex(codepoint) <= 0)
                    return null;

                var key = new AtlasGlyph.CacheKey(codepoint, pixelSize, font);
                if (glyphCache.TryGetValue(key, out var cachedGlyph))
                    return cachedGlyph;

                var glyph = new AtlasGlyph(codepoint, pixelSize, font, this);

                if (TryAddGlyphToAtlas(glyph))
                {
                    glyphCache[key] = glyph;
                    return glyph;
                }

                if (AllowExpansion && TryExpandAtlas(glyph) && TryAddGlyphToAtlas(glyph))
                {
                    glyphCache[key] = glyph;
                    return glyph;
                }

                glyphCache[key] = glyph;
                return glyph;
            }
        }

        private bool TryAddGlyphToAtlas(AtlasGlyph glyph)
        {
            var bitmap = RenderGlyph(glyph.Font, glyph.Codepoint, glyph.PixelSize);
            if (bitmap == null) return true; // Empty glyph, nothing to pack

            int packWidth = bitmap.Value.Width + Padding * 2;
            int packHeight = bitmap.Value.Height + Padding * 2;

            if (binPacker.TryPack(packWidth, packHeight, out int x, out int y))
            {
                glyph.AtlasX = x + Padding;
                glyph.AtlasY = y + Padding;
                glyph.AtlasWidth = bitmap.Value.Width;
                glyph.AtlasHeight = bitmap.Value.Height;

                // Calculate texture coordinates
                glyph.U0 = (float)glyph.AtlasX / atlasWidth;
                glyph.V0 = (float)glyph.AtlasY / atlasHeight;
                glyph.U1 = (float)(glyph.AtlasX + glyph.AtlasWidth) / atlasWidth;
                glyph.V1 = (float)(glyph.AtlasY + glyph.AtlasHeight) / atlasHeight;

                // Upload bitmap to atlas
                renderer.UpdateTextureRegion(atlasTexture,
                    new AtlasRect(glyph.AtlasX, glyph.AtlasY, glyph.AtlasWidth, glyph.AtlasHeight),
                    bitmap.Value.Data);

                return true;
            }

            return false;
        }

        private bool TryExpandAtlas(AtlasGlyph glyph)
        {
            var bitmap = RenderGlyph(glyph.Font, glyph.Codepoint, glyph.PixelSize);
            if (bitmap == null) return true;

            int requiredWidth = bitmap.Value.Width + Padding * 2;
            int requiredHeight = bitmap.Value.Height + Padding * 2;

            int newWidth = Math.Max(atlasWidth, (int)(atlasWidth * ExpansionFactor));
            int newHeight = Math.Max(atlasHeight, (int)(atlasHeight * ExpansionFactor));

            // Ensure we can fit the glyph
            newWidth = Math.Max(newWidth, atlasWidth + requiredWidth);
            newHeight = Math.Max(newHeight, atlasHeight + requiredHeight);

            // Respect max size
            if (newWidth > MaxAtlasSize || newHeight > MaxAtlasSize)
                return false;

            // Create new atlas
            atlasWidth = newWidth;
            atlasHeight = newHeight;
            atlasTexture = renderer.CreateTexture(atlasWidth, atlasHeight);

            // Clear bin packer and glyph cache
            binPacker.Clear(atlasWidth, atlasHeight);
            glyphCache.Clear();

            // Clear the Layout Cache
            layoutCache.Clear();

            // Re-add white rect
            if (useWhiteRect)
                AddWhiteRect();

            return true;
        }

        #region Metrics and Getters

        public GlyphMetrics? GetGlyphMetrics(FontFile fontInfo, int codepoint, float pixelSize)
        {
            int glyphIndex = fontInfo.FindGlyphIndex(codepoint);
            if (glyphIndex == 0) return null;

            float scale = fontInfo.ScaleForPixelHeight(pixelSize);

            // Get advance and bearing
            int advance = 0, leftSideBearing = 0;
            fontInfo.GetGlyphHorizontalMetrics(glyphIndex, ref advance, ref leftSideBearing);

            // Get bounding box
            int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
            fontInfo.GetGlyphBitmapBoundingBox(glyphIndex, scale, scale, ref x0, ref y0, ref x1, ref y1);

            return new GlyphMetrics {
                AdvanceWidth = advance * scale,
                LeftSideBearing = leftSideBearing * scale,
                Width = x1 - x0,
                Height = y1 - y0,
                OffsetX = x0,
                OffsetY = y0
            };
        }

        public void GetScaledVMetrics(FontFile font, float pixelSize, out float ascent, out float descent, out float lineGap)
        {
            float s = font.ScaleForPixelHeight(pixelSize);
            ascent = font.Ascent * s;
            descent = font.Descent * s; // stb returns negative descent; caller may convert to positive if desired
            lineGap = font.Linegap * s;
        }

        public GlyphBitmap? RenderGlyph(FontFile fontInfo, int codepoint, float pixelSize)
        {
            int glyphIndex = fontInfo.FindGlyphIndex(codepoint);
            if (glyphIndex == 0) return null;

            float scale = fontInfo.ScaleForPixelHeight(pixelSize);

            int width = 0, height = 0, xoff = 0, yoff = 0;
            var bitmap = fontInfo.GetGlyphBitmap(scale, scale, glyphIndex, ref width, ref height, ref xoff, ref yoff);

            if (bitmap.IsNull || width == 0 || height == 0)
                return null;

            // Convert to byte array
            byte[] data = new byte[width * height];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = bitmap[i];
            }

            return new GlyphBitmap {
                Width = width,
                Height = height,
                OffsetX = xoff,
                OffsetY = yoff,
                Data = data
            };
        }

        public float GetKerning(FontFile fontInfo, int leftCodepoint, int rightCodepoint, float pixelSize)
        {
            int leftGlyph = fontInfo.FindGlyphIndex(leftCodepoint);
            int rightGlyph = fontInfo.FindGlyphIndex(rightCodepoint);

            float scale = fontInfo.ScaleForPixelHeight(pixelSize);
            int kernAdvance = fontInfo.GetGlyphKerningAdvance(leftGlyph, rightGlyph);

            return kernAdvance * scale;
        }

        #endregion

        #region Layout Methods

        public TextLayout CreateLayout(string text, TextLayoutSettings settings)
        {
            if (string.IsNullOrEmpty(text))
            {
                var empty = GetTextLayoutFromPool();
                empty.UpdateLayout(text, settings, this);
                return empty;
            }

            if (!CacheLayouts)
            {
                var direct = GetTextLayoutFromPool();
                direct.UpdateLayout(text, settings, this);
                return direct;
            }

            var key = GenerateLayoutCacheKey(text, settings);

            if (layoutCache.TryGetValue(key, out var cached))
                return cached;

            var layout = GetTextLayoutFromPool();
            layout.UpdateLayout(text, settings, this);

            layoutCache.Add(key, layout);
            return layout;
        }

        LayoutCacheKey GenerateLayoutCacheKey(string text, TextLayoutSettings s)
            => new LayoutCacheKey(text, s.PixelSize, s.LetterSpacing, s.WordSpacing, s.LineHeight,
                   s.TabSize, s.WrapMode, s.Alignment, s.MaxWidth, s.Font.GetHashCode());

        #endregion

        #region Updated API Methods

        public Vector2 MeasureText(string text, float pixelSize, FontFile font, float letterSpacing = 0)
        {
            var settings = TextLayoutSettings.Default;
            settings.PixelSize = pixelSize;
            settings.Font = font;
            settings.LetterSpacing = letterSpacing;

            var layout = CreateLayout(text, settings);
            return layout.Size;
        }

        public Vector2 MeasureText(string text, TextLayoutSettings settings)
        {
            var layout = CreateLayout(text, settings);
            return layout.Size;
        }

        public void DrawText(string text, Vector2 position, FontColor color, float pixelSize, FontFile font, float letterSpacing = 0)
        {
            var settings = TextLayoutSettings.Default;
            settings.PixelSize = pixelSize;
            settings.Font = font;
            settings.LetterSpacing = letterSpacing;

            DrawText(text, position, color, settings);
        }


        public void DrawText(string text, Vector2 position, FontColor color, TextLayoutSettings settings)
        {
            if (string.IsNullOrEmpty(text)) return;

            var layout = CreateLayout(text, settings);
            DrawLayout(layout, position, color);
        }

        private Stack<TextLayout> _textLayouts = new Stack<TextLayout>();

        private TextLayout GetTextLayoutFromPool()
        {
            if (_textLayouts.TryPop(out TextLayout layout))
            {
                return layout;
            }

            return new TextLayout();
        }
        
        List<IFontRenderer.Vertex> _vertices = new List<IFontRenderer.Vertex>();
        List<int> _indices = new List<int>();
        public void DrawLayout(TextLayout layout, Vector2 position, FontColor color)
        {
            if (layout.Lines.Count == 0) return;

            _vertices.Clear();
            var vertices = _vertices;
            _indices.Clear();
            var indices = _indices;
            int vertexCount = 0;

            foreach (var line in layout.Lines)
            {
                foreach (var glyphInstance in line.Glyphs)
                {
                    var glyph = glyphInstance.Glyph;

                    // Only render if glyph is in atlas
                    if (!glyph.IsInAtlas || glyph.AtlasWidth <= 0 || glyph.AtlasHeight <= 0)
                        continue;

                    float glyphX = position.X + line.Position.X + glyphInstance.Position.X;
                    float glyphY = position.Y + line.Position.Y + glyphInstance.Position.Y;
                    float glyphW = glyph.Metrics.Width;
                    float glyphH = glyph.Metrics.Height;

                    // Create quad vertices
                    vertices.Add(new IFontRenderer.Vertex(new Vector3(glyphX, glyphY, 0), color, new Vector2(glyph.U0, glyph.V0)));
                    vertices.Add(new IFontRenderer.Vertex(new Vector3(glyphX + glyphW, glyphY, 0), color, new Vector2(glyph.U1, glyph.V0)));
                    vertices.Add(new IFontRenderer.Vertex(new Vector3(glyphX, glyphY + glyphH, 0), color, new Vector2(glyph.U0, glyph.V1)));
                    vertices.Add(new IFontRenderer.Vertex(new Vector3(glyphX + glyphW, glyphY + glyphH, 0), color, new Vector2(glyph.U1, glyph.V1)));

                    // Create quad indices
                    indices.Add(vertexCount);
                    indices.Add(vertexCount + 1);
                    indices.Add(vertexCount + 2);
                    indices.Add(vertexCount + 1);
                    indices.Add(vertexCount + 3);
                    indices.Add(vertexCount + 2);
                    vertexCount += 4;
                }
            }

            if (vertices.Count > 0)
            {
                #if  NET5_0_OR_GREATER
                renderer.DrawQuads(atlasTexture, CollectionsMarshal.AsSpan(vertices), CollectionsMarshal.AsSpan(indices));            
                #else
                renderer.DrawQuads(atlasTexture, vertices.ToArray(), indices.ToArray());
#endif
            }
            
            _textLayouts.Push(layout);
        }

        #endregion
    }
}
