using System;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// A range of a text, from <see cref="Start"/> up to but not including <see cref="End"/>, in UTF-16 code units: C#'s
    /// string indices, which are also NSString's on iOS and Java's on Android, so a range means the same on every side.
    /// <see cref="None"/> (-1, -1) is no range: no composition, say.
    /// </summary>
    [Serializable]
    public readonly struct TextRange : IEquatable<TextRange>
    {
        /// <summary>No range.</summary>
        public static readonly TextRange None = new(-1, -1);

        /// <summary>Where it starts.</summary>
        public readonly int Start;

        /// <summary>Where it ends: one past its last code unit.</summary>
        public readonly int End;

        /// <summary>A range between two indices, given either way round.</summary>
        public TextRange(int start, int end)
        {
            if (start <= end)
            {
                Start = start;
                End = end;
            }
            else
            {
                Start = end;
                End = start;
            }
        }

        /// <summary>An empty range at <paramref name="index"/>.</summary>
        public static TextRange Collapsed(int index) => new(index, index);

        /// <summary>Whether it is a range at all (not <see cref="None"/>).</summary>
        public bool IsValid => Start >= 0;

        /// <summary>Whether it is a range with nothing in it.</summary>
        public bool IsEmpty => IsValid && Start == End;

        /// <summary>How many code units it covers; 0 for <see cref="None"/>.</summary>
        public int Length => IsValid ? End - Start : 0;

        /// <summary>Whether <paramref name="index"/> lies in it, either end included.</summary>
        public bool Contains(int index) => IsValid && index >= Start && index <= End;

        /// <summary>It kept within a text <paramref name="length"/> long; <see cref="None"/> stays none.</summary>
        public TextRange Clamp(int length) =>
            IsValid ? new TextRange(Math.Min(Start, length), Math.Min(End, length)) : None;

        public bool Equals(TextRange other) => Start == other.Start && End == other.End;

        public override bool Equals(object obj) => obj is TextRange other && Equals(other);

        public override int GetHashCode() => (Start * 397) ^ End;

        public static bool operator ==(TextRange a, TextRange b) => a.Equals(b);

        public static bool operator !=(TextRange a, TextRange b) => !a.Equals(b);

        public override string ToString() => IsValid ? $"[{Start}, {End})" : "none";
    }
}
