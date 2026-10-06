using UnityEngine;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// Something the keyboard types into, while <see cref="TextInputSystem"/> has it as its client: a
    /// <see cref="TextField"/>, or an editor of your own. It owns the text being edited (what is drawn, and any filter);
    /// the system keeps the platform's keyboard told of it, and hands it what the keyboard does.
    /// </summary>
    public interface ITextInputClient
    {
        /// <summary>The text, selection and composition being edited.</summary>
        TextEditingValue Value { get; }

        /// <summary>What it asks of the keyboard.</summary>
        TextInputConfig Config { get; }

        /// <summary>
        /// The platform's keyboard changed the value: typing, autocorrect, a suggestion, a composition, dictation, the
        /// platform's edit menu. Take it up; a client that filters it (a length limit) sets its own value instead and
        /// calls <see cref="TextInputSystem.NotifyValueChanged"/>.
        /// </summary>
        void ApplyPlatformValue(in TextEditingValue value);

        /// <summary>A hardware or desktop editing key (see <see cref="TextEditIntent"/>); `extend`: shift is held, so the selection grows.</summary>
        void PerformIntent(TextEditIntent intent, bool extend);

        /// <summary>Text typed on a hardware or desktop keyboard: it replaces the composition, or else the selection.</summary>
        void InsertText(string text);

        /// <summary>A desktop composition (an IME's marked text) is now <paramref name="text"/>; empty, it is gone.</summary>
        void SetComposingText(string text);

        /// <summary>The platform ended the session (the keyboard dismissed by the user, focus taken by the system).</summary>
        void OnSessionEnded();

        /// <summary>
        /// Where it is on screen, in screen pixels with y up (as <see cref="Screen.safeArea"/>): itself, the caret, and the
        /// text being composed (an empty rect with none). The platform places its candidate window, autocorrect highlight
        /// and edit menu by these. False while it is not laid out.
        /// </summary>
        bool TryGetScreenGeometry(out Rect field, out Rect caret, out Rect composing);
    }
}
