using System;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// Everything about a text being edited that the platform's keyboard and the field agree on, as Flutter's
    /// TextEditingValue: the text, what is selected, and the part still being composed (marked text on iOS, the composing
    /// region on Android: the word Gboard is still guessing at, a Japanese phrase not yet converted), drawn underlined. All
    /// indices are UTF-16 code units. Immutable; the With and Replace methods return a new value. A value is always
    /// consistent: its selection and composing range lie within its text.
    /// </summary>
    public readonly struct TextEditingValue : IEquatable<TextEditingValue>
    {
        private readonly string _text;

        /// <summary>The text (never null).</summary>
        public string Text => _text ?? string.Empty;

        /// <summary>What is selected, or where the caret is.</summary>
        public readonly TextSelection Selection;

        /// <summary>The part still being composed, or <see cref="TextRange.None"/>.</summary>
        public readonly TextRange Composing;

        /// <summary>A value, its selection and composing range kept within its text; an empty composing range is none.</summary>
        public TextEditingValue(string text, TextSelection selection, TextRange composing)
        {
            _text = text ?? string.Empty;
            int length = _text.Length;
            Selection = selection.Clamp(length);
            var clamped = composing.Clamp(length);
            Composing = clamped.IsValid && !clamped.IsEmpty ? clamped : TextRange.None;
        }

        /// <summary>No text, the caret at its start.</summary>
        public static TextEditingValue Empty => new(string.Empty, TextSelection.Collapsed(0), TextRange.None);

        /// <summary><paramref name="text"/> with the caret at its end.</summary>
        public static TextEditingValue FromText(string text)
        {
            text ??= string.Empty;
            return new TextEditingValue(text, TextSelection.Collapsed(text.Length), TextRange.None);
        }

        /// <summary>Whether part of it is being composed.</summary>
        public bool IsComposing => Composing.IsValid && !Composing.IsEmpty;

        /// <summary>The same value with another selection.</summary>
        public TextEditingValue WithSelection(TextSelection selection) => new(Text, selection, Composing);

        /// <summary>The same value with another composing range.</summary>
        public TextEditingValue WithComposing(TextRange composing) => new(Text, Selection, composing);

        /// <summary>The same value with its composition committed: the text stays, no longer underlined.</summary>
        public TextEditingValue CommitComposition() => new(Text, Selection, TextRange.None);

        /// <summary>
        /// <paramref name="range"/> replaced by <paramref name="with"/>, the caret after what was put in and nothing being
        /// composed.
        /// </summary>
        public TextEditingValue Replace(TextRange range, string with)
        {
            with ??= string.Empty;
            var text = Text;
            var clamped = range.Clamp(text.Length);
            if (!clamped.IsValid) clamped = TextRange.Collapsed(text.Length);
            var result = string.Concat(text.Substring(0, clamped.Start), with, text.Substring(clamped.End));
            return new TextEditingValue(result, TextSelection.Collapsed(clamped.Start + with.Length), TextRange.None);
        }

        /// <summary>
        /// What typing <paramref name="with"/> does: it replaces the text being composed if there is some, as UIKit's
        /// insertText does, and otherwise what is selected.
        /// </summary>
        public TextEditingValue ReplaceSelection(string with) => Replace(IsComposing ? Composing : Selection.Range, with);

        public bool Equals(TextEditingValue other) =>
            string.Equals(Text, other.Text, StringComparison.Ordinal) && Selection == other.Selection && Composing == other.Composing;

        public override bool Equals(object obj) => obj is TextEditingValue other && Equals(other);

        public override int GetHashCode() => (Text.GetHashCode() * 397) ^ (Selection.GetHashCode() * 31) ^ Composing.GetHashCode();

        public static bool operator ==(TextEditingValue a, TextEditingValue b) => a.Equals(b);

        public static bool operator !=(TextEditingValue a, TextEditingValue b) => !a.Equals(b);

        public override string ToString() => $"\"{Text}\" selection {Selection}, composing {Composing}";
    }
}
