using System;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// What is selected in a text, with a direction: from <see cref="Base"/>, where the selection was started and stays,
    /// to <see cref="Extent"/>, the end that moves (shift and an arrow key, or a dragged handle). Collapsed, both are the
    /// caret. Indices are UTF-16 code units, as <see cref="TextRange"/>'s.
    /// </summary>
    [Serializable]
    public readonly struct TextSelection : IEquatable<TextSelection>
    {
        /// <summary>No selection.</summary>
        public static readonly TextSelection None = new(-1, -1);

        /// <summary>The end the selection was started from, which stays.</summary>
        public readonly int Base;

        /// <summary>The end that moves.</summary>
        public readonly int Extent;

        public TextSelection(int baseIndex, int extentIndex)
        {
            Base = baseIndex;
            Extent = extentIndex;
        }

        /// <summary>A caret at <paramref name="index"/>.</summary>
        public static TextSelection Collapsed(int index) => new(index, index);

        /// <summary>Its first index.</summary>
        public int Start => Math.Min(Base, Extent);

        /// <summary>One past its last index.</summary>
        public int End => Math.Max(Base, Extent);

        /// <summary>Whether it is a selection at all (not <see cref="None"/>).</summary>
        public bool IsValid => Base >= 0 && Extent >= 0;

        /// <summary>Whether it is a caret: nothing selected.</summary>
        public bool IsCollapsed => Base == Extent;

        /// <summary>What it covers, without its direction.</summary>
        public TextRange Range => IsValid ? new TextRange(Start, End) : TextRange.None;

        /// <summary>It kept within a text <paramref name="length"/> long; <see cref="None"/> becomes a caret at the end.</summary>
        public TextSelection Clamp(int length) =>
            IsValid ? new TextSelection(Math.Min(Base, length), Math.Min(Extent, length)) : Collapsed(length);

        public bool Equals(TextSelection other) => Base == other.Base && Extent == other.Extent;

        public override bool Equals(object obj) => obj is TextSelection other && Equals(other);

        public override int GetHashCode() => (Base * 397) ^ Extent;

        public static bool operator ==(TextSelection a, TextSelection b) => a.Equals(b);

        public static bool operator !=(TextSelection a, TextSelection b) => !a.Equals(b);

        public override string ToString() => IsValid ? IsCollapsed ? $"caret {Base}" : $"{Base} to {Extent}" : "none";
    }
}
