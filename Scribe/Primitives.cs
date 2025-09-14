using Prowl.Scribe.Internal;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Prowl.Scribe
{

    /// <summary>
    /// Represents a node in the font atlas, typically a free rectangular space.
    /// For a skyline bottom-left bin packer, this might store skyline segments.
    /// </summary>
    internal struct AtlasNode
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public AtlasNode(int x, int y, int width, int height = 0)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Represents a rectangular area, typically in a texture atlas.
    /// </summary>
    public struct AtlasRect
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public AtlasRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    public enum TextWrapMode
    {
        NoWrap,
        Wrap
    }

    public enum TextAlignment
    {
        Left,
        Center,
        Right,
        //Justify
    }


    public enum FontStyle
    {
        Regular,
        Bold,
        Italic,
        BoldItalic
    }

    public struct TextLayoutSettings
    {
        public float PixelSize;
        public FontFile Font;
        public float LetterSpacing;
        public float WordSpacing;
        public float LineHeight; // multiplier (1.0 = normal, 1.2 = 20% larger)
        public int TabSize; // in characters
        public TextWrapMode WrapMode;
        public TextAlignment Alignment;
        public float MaxWidth; // for wrapping, 0 = no limit
        public List<StyleSpan> StyleSpans;
        public MarkdownLayoutSettings LayoutSettings;

        private static Stack<TextLayoutSettings> _pool = new Stack<TextLayoutSettings>();
        
        public static TextLayoutSettings Default => new TextLayoutSettings {
            PixelSize = 16,
            Font = null,
            LetterSpacing = 0,
            WordSpacing = 0,
            LineHeight = 1.0f,
            TabSize = 4,
            WrapMode = TextWrapMode.NoWrap,
            Alignment = TextAlignment.Left,
            MaxWidth = 0,
            StyleSpans = new List<StyleSpan>()
        };

        private void SetDefaultValues()
        {
            PixelSize = 16;
            Font = null;
            LetterSpacing = 0;
            WordSpacing = 0;
            LineHeight = 1.0f;
            TabSize = 4;
            WrapMode = TextWrapMode.NoWrap;
            Alignment = TextAlignment.Left;
            MaxWidth = 0;
            
            if (StyleSpans == null) StyleSpans = new List<StyleSpan>();
            StyleSpans.Clear();
        }
        
        public static TextLayoutSettings Get()
        {
            if (!_pool.TryPop(out TextLayoutSettings settings))
            {
                settings = new TextLayoutSettings();
            }
            
            settings.SetDefaultValues();
            return settings;
        }

        public static void Return(TextLayoutSettings settings)
        {
            settings.StyleSpans.Clear();
            _pool.Push(settings);
        }
    }

    public struct GlyphInstance
    {
        public AtlasGlyph Glyph;
        public Vector2 Position;
        public char Character;
        public float AdvanceWidth;
        public int CharIndex;

        private static Stack<GlyphInstance> _pool = new Stack<GlyphInstance>();
        
        public GlyphInstance(AtlasGlyph glyph, Vector2 position, char character, float advanceWidth, int charIndex)
        {
            Glyph = glyph;
            Position = position;
            Character = character;
            AdvanceWidth = advanceWidth;
            CharIndex = charIndex;
        }

        public static GlyphInstance Get(AtlasGlyph glyph, Vector2 position, char character, float advanceWidth, int charIndex)
        {
            if (!_pool.TryPop(out GlyphInstance instance))
            {
                instance = new GlyphInstance(glyph, position, character, advanceWidth, charIndex);
            }
            
            instance.Glyph = glyph;
            instance.Position = position;
            instance.Character = character;
            instance.AdvanceWidth = advanceWidth;
            instance.CharIndex = charIndex;

            return instance;
        }

        public static void Return(GlyphInstance instance)
        {
            _pool.Push(instance);
        }
    }

    public struct Line
    {
        public List<GlyphInstance> Glyphs;
        public float Width;
        public float Height;
        public Vector2 Position; // relative to layout origin
        public int StartIndex; // character index in original string
        public int EndIndex; // character index in original string
        public static Stack<Line> _pool = new Stack<Line>();
        public Line(Vector2 position, int startIndex)
        {
            Glyphs = new List<GlyphInstance>();
            Width = 0;
            Height = 0;
            Position = position;
            StartIndex = startIndex;
            EndIndex = startIndex;
        }

        public static Line Get(Vector2 position, int startIndex)
        {
            if (!_pool.TryPop(out Line line))
            {
                line = new Line(position, startIndex);
            }

            line.Position = position;
            line.StartIndex = startIndex;
            line.EndIndex = startIndex;
            return line;
        }

        public static void Return(Line line)
        {
            foreach (GlyphInstance instance in line.Glyphs)
            {
                GlyphInstance.Return(instance);
            }
            line.Glyphs.Clear();
            _pool.Push(line);
        }
    }
}
