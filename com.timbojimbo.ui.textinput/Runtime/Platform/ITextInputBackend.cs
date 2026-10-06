using System.Collections.Generic;
using UnityEngine;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// How the value pushed to a backend differs from what the platform's keyboard last saw, which decides how a backend
    /// tells the keyboard (rarely restarting it, which loses its autocorrect state; see the backends).
    /// </summary>
    internal enum TextChangeKind
    {
        /// <summary>Only the selection moved (a tap, a handle, an arrow key).</summary>
        Selection,
        /// <summary>The text changed outside any composition (a chip inserted at the caret, undo).</summary>
        Text,
        /// <summary>The composing range or its text was changed or cleared from our side.</summary>
        Composing,
        /// <summary>The text was replaced as a whole (cleared after sending, set from code).</summary>
        Wholesale,
    }

    /// <summary>What a backend reports.</summary>
    internal enum TextInputEventKind
    {
        /// <summary>The platform's keyboard changed the value (<see cref="TextInputEvent.Value"/>), based on <see cref="TextInputEvent.BaseSerial"/>.</summary>
        Edit,
        /// <summary>An editing key (<see cref="TextInputEvent.Intent"/>, <see cref="TextInputEvent.Extend"/>); the return key is <see cref="TextEditIntent.Return"/>.</summary>
        Intent,
        /// <summary>Text typed on a hardware or desktop keyboard (<see cref="TextInputEvent.Text"/>).</summary>
        InsertText,
        /// <summary>A desktop composition is now <see cref="TextInputEvent.Text"/> (empty: gone).</summary>
        Composing,
        /// <summary>The platform ended the session: the user dismissed the keyboard, or the system took focus.</summary>
        SessionEnded,
    }

    /// <summary>One thing a backend reports, for the session it belongs to.</summary>
    internal struct TextInputEvent
    {
        public TextInputEventKind Kind;
        public int Session;
        public int BaseSerial;
        public TextEditingValue Value;
        public TextEditIntent Intent;
        public bool Extend;
        public string Text;

        public static TextInputEvent Edit(int session, int baseSerial, in TextEditingValue value) =>
            new() { Kind = TextInputEventKind.Edit, Session = session, BaseSerial = baseSerial, Value = value };

        public static TextInputEvent ForIntent(int session, TextEditIntent intent, bool extend) =>
            new() { Kind = TextInputEventKind.Intent, Session = session, Intent = intent, Extend = extend };

        public static TextInputEvent Insert(int session, string text) =>
            new() { Kind = TextInputEventKind.InsertText, Session = session, Text = text };

        public static TextInputEvent Compose(int session, string text) =>
            new() { Kind = TextInputEventKind.Composing, Session = session, Text = text };

        public static TextInputEvent Ended(int session) => new() { Kind = TextInputEventKind.SessionEnded, Session = session };
    }

    /// <summary>
    /// The platform side of <see cref="TextInputSystem"/>: on iOS and Android a small native input client that the
    /// platform's keyboard types into (a hidden UITextInput view, an InputConnection) holding a mirror of the value it
    /// answers the keyboard's questions from; on desktop and in the editor, the Input System. All calls are made on Unity's
    /// main thread.
    /// </summary>
    /// <remarks>
    /// A session is one stretch of editing one client, numbered by the system; anything a backend reports carries it, and
    /// the system drops what belongs to another. Values pushed carry a rising serial: the backend applies them in order,
    /// never reports a pushed value back, and reports each edit of its own with the serial of the last value it had applied
    /// (<see cref="TextInputEvent.BaseSerial"/>), so that an edit made before a newer push reached it is known to be stale.
    /// </remarks>
    internal interface ITextInputBackend
    {
        /// <summary>A session starts: the keyboard comes up for a client with this config, holding this value (serial `serial`).</summary>
        void Attach(int session, in TextInputConfig config, in TextEditingValue value, int serial);

        /// <summary>The session ends: the keyboard goes down unless another session attaches within the frame.</summary>
        void Detach(int session);

        /// <summary>The client's config changed while editing (the keyboard is reloaded).</summary>
        void SetConfig(int session, in TextInputConfig config);

        /// <summary>The client changed its value itself; `kind` says how it differs from what the keyboard last saw.</summary>
        void SetValue(int session, int serial, in TextEditingValue value, TextChangeKind kind);

        /// <summary>Where the client is: screen pixels, y up. `composing` is empty with no composition.</summary>
        void SetGeometry(int session, Rect field, Rect caret, Rect composing);

        /// <summary>Whether it shows the platform's own edit menu (copy, paste) for <see cref="ShowEditMenu"/>.</summary>
        bool SupportsEditMenu { get; }

        /// <summary>Shows the platform's edit menu by `target` (screen pixels, y up); what it does comes back as edits.</summary>
        void ShowEditMenu(int session, Rect target, TextEditActions actions);

        /// <summary>Hides the edit menu, if it shows.</summary>
        void HideEditMenu();

        /// <summary>Once a frame, before events are drained: a backend polls the platform here.</summary>
        void Update();

        /// <summary>Moves what it has to report into `into`, oldest first.</summary>
        void DrainEvents(List<TextInputEvent> into);

        /// <summary>How far up from the bottom of the screen a docked software keyboard covers it this frame, in screen pixels.</summary>
        float KeyboardHeight { get; }

        /// <summary>Whether a software keyboard is up (docked or floating).</summary>
        bool KeyboardVisible { get; }
    }
}
