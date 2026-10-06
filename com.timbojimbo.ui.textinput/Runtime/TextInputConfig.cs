using System;
using UnityEngine;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// What a field holds, which picks the keyboard the platform shows and what it suggests, as UIKit's textContentType
    /// and keyboardType and Android's input type do.
    /// </summary>
    public enum TextContentType
    {
        /// <summary>Any text: the full keyboard, with suggestions and autocorrect.</summary>
        Standard,
        /// <summary>An email address.</summary>
        Email,
        /// <summary>A web address.</summary>
        Url,
        /// <summary>Whole numbers.</summary>
        Number,
        /// <summary>Numbers with a decimal point.</summary>
        Decimal,
        /// <summary>A phone number.</summary>
        Phone,
        /// <summary>A person's name.</summary>
        Name,
        /// <summary>A user name: no autocorrect or capitals.</summary>
        Username,
        /// <summary>A password: hidden as it is typed, nothing suggested or learnt.</summary>
        Password,
        /// <summary>A new password, which the platform's password manager may offer to make.</summary>
        NewPassword,
        /// <summary>A one-time code, which the platform may fill in from a message.</summary>
        OneTimeCode,
        /// <summary>A search: the return key searches.</summary>
        Search,
    }

    /// <summary>What the keyboard's return key says, and so what it does.</summary>
    public enum TextReturnKey
    {
        /// <summary>Return: a new line in a multi-line field; otherwise the field is submitted.</summary>
        Default,
        Done,
        Go,
        Next,
        Search,
        Send,
    }

    /// <summary>Which letters the keyboard starts in capitals.</summary>
    public enum TextCapitalization
    {
        /// <summary>The first letter of each sentence.</summary>
        Sentences,
        /// <summary>None.</summary>
        None,
        /// <summary>The first letter of each word.</summary>
        Words,
        /// <summary>Every letter.</summary>
        Characters,
    }

    /// <summary>Whether the keyboard corrects and suggests words.</summary>
    public enum TextAutocorrection
    {
        /// <summary>What the content type calls for.</summary>
        Default,
        On,
        Off,
    }

    /// <summary>What a field asks of the platform's keyboard (see <see cref="TextInputSystem"/>).</summary>
    [Serializable]
    public struct TextInputConfig : IEquatable<TextInputConfig>
    {
        [Tooltip("What the field holds: picks the keyboard and what it suggests.")]
        public TextContentType ContentType;

        [Tooltip("What the return key says. Default: a new line in a multi-line field, otherwise submit.")]
        public TextReturnKey ReturnKey;

        [Tooltip("Whether the text can run over several lines.")]
        public bool Multiline;

        [Tooltip("Which letters start in capitals.")]
        public TextCapitalization Capitalization;

        [Tooltip("Whether the keyboard corrects and suggests words.")]
        public TextAutocorrection Autocorrection;

        [Tooltip("Whether the return key is greyed out while the field is empty.")]
        public bool EnablesReturnKeyAutomatically;

        /// <summary>Whether what is typed is hidden: a password.</summary>
        public bool IsSecure => ContentType == TextContentType.Password || ContentType == TextContentType.NewPassword;

        /// <summary>Whether the return key types a new line rather than submitting: a multi-line field whose return key says Return.</summary>
        public bool ReturnInsertsNewline => Multiline && ReturnKey == TextReturnKey.Default;

        /// <summary>Whether the keyboard should correct and suggest words, as asked or as the content type calls for.</summary>
        public bool CorrectsWords => Autocorrection switch
        {
            TextAutocorrection.On => !IsSecure,
            TextAutocorrection.Off => false,
            _ => ContentType == TextContentType.Standard || ContentType == TextContentType.Name || ContentType == TextContentType.Search,
        };

        public bool Equals(TextInputConfig other) =>
            ContentType == other.ContentType && ReturnKey == other.ReturnKey && Multiline == other.Multiline
            && Capitalization == other.Capitalization && Autocorrection == other.Autocorrection
            && EnablesReturnKeyAutomatically == other.EnablesReturnKeyAutomatically;

        public override bool Equals(object obj) => obj is TextInputConfig other && Equals(other);

        public override int GetHashCode() =>
            ((int)ContentType * 397) ^ ((int)ReturnKey * 31) ^ (Multiline ? 1 : 0) ^ ((int)Capitalization << 8) ^ ((int)Autocorrection << 12)
            ^ (EnablesReturnKeyAutomatically ? 1 << 16 : 0);
    }

    /// <summary>What an edit menu (the platform's copy and paste bubble) offers.</summary>
    [Flags]
    public enum TextEditActions
    {
        None = 0,
        Cut = 1,
        Copy = 2,
        Paste = 4,
        SelectAll = 8,
    }

    /// <summary>
    /// An editing key from a hardware or desktop keyboard, already mapped from the platform's keys (arrows and the
    /// modifiers that make them jump a word or a line, deletion, the clipboard, undo) to what it does, as Flutter's text
    /// editing intents are. The field carries it out with its own layout, so a word or a line means what it shows.
    /// <para>
    /// Left and right are where the arrows point on screen, as in iOS, macOS and Android text views: in text that reads
    /// right to left the left arrow moves on through the text, and in text that reads both ways the caret goes to the
    /// place beside it on screen. Start, end, backward and forward are the text's own order, whichever way it reads.
    /// </para>
    /// </summary>
    public enum TextEditIntent
    {
        /// <summary>The caret to the place beside it on the left (the left arrow).</summary>
        MoveLeft,
        /// <summary>The caret to the place beside it on the right (the right arrow).</summary>
        MoveRight,
        MoveUp,
        MoveDown,
        /// <summary>A word towards the left (Option or Ctrl and the left arrow): back a word in left-to-right text, on a word in right-to-left text.</summary>
        MoveWordLeft,
        /// <summary>A word towards the right: on a word in left-to-right text, back a word in right-to-left text.</summary>
        MoveWordRight,
        /// <summary>The start of the line, as the text reads (Home, Ctrl-A).</summary>
        MoveLineStart,
        /// <summary>The end of the line, as the text reads (End, Ctrl-E).</summary>
        MoveLineEnd,
        MoveDocumentStart,
        MoveDocumentEnd,
        MovePageUp,
        MovePageDown,
        /// <summary>The line's left end on screen (Cmd and the left arrow on a Mac or an iPad): its start in left-to-right text, its end in right-to-left text.</summary>
        MoveLineLeft,
        /// <summary>The line's right end on screen (Cmd and the right arrow).</summary>
        MoveLineRight,
        /// <summary>Back a character in the text's order, whichever way it reads (Ctrl-B on a Mac).</summary>
        MoveBackward,
        /// <summary>On a character in the text's order (Ctrl-F on a Mac).</summary>
        MoveForward,
        DeleteBackward,
        DeleteForward,
        DeleteWordBackward,
        DeleteWordForward,
        DeleteToLineStart,
        DeleteToLineEnd,
        SelectAll,
        Copy,
        Cut,
        Paste,
        Undo,
        Redo,
        /// <summary>A new line whatever the return key does (shift and return in a chat composer).</summary>
        Newline,
        /// <summary>The return key: a new line or submit, as the field's <see cref="TextInputConfig.ReturnInsertsNewline"/> says.</summary>
        Return,
        /// <summary>Escape: editing ends.</summary>
        Cancel,
    }
}
