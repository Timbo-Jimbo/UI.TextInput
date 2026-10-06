using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// Editing keys and typed text from the Input System's keyboards, turned into what a text field does with them
    /// (<see cref="TextEditIntent"/>s and inserted text), for desktop players, the editor, and hardware keyboards on
    /// Android. Both are queued in the order they were typed in, and handed over once a frame (<see cref="Drain"/>).
    /// <para>
    /// Text comes from <see cref="Keyboard.onTextInput"/>, which carries the OS's own key repeat and the keyboard layout,
    /// less the control characters some platforms also send for editing keys (backspace, return, macOS's function-key
    /// range) and what is typed with Ctrl or Command held, which are shortcuts. AltGr (Ctrl and right Alt) still types.
    /// Editing keys come from <see cref="InputSystem.onEvent"/>, which sees each keyboard event before it is applied, so a
    /// key going down is told apart within the frame and stays in order with the text around it. Shortcuts follow the
    /// platform (Command and Option on macOS, Ctrl elsewhere, as Flutter's default text editing shortcuts), and letters
    /// are matched by what the key types in the current layout, so Ctrl-A is where the A is on AZERTY too. Return is the
    /// return key (with Shift, a new line), Escape cancels, and Tab is left to move focus.
    /// </para>
    /// <para>
    /// The Input System has no key repeat of its own, so a held editing key repeats on a timer here, as the OS would:
    /// only the last key pressed repeats, any other key pressed stops it, the modifiers held are read again on each repeat,
    /// and it stops as the key is found released (checked on each repeat, since a device reset releases keys without an
    /// event to listen to), as the device resets, and as the application loses focus. Return, Escape, select all, copy
    /// and cut do not repeat, so holding return submits once.
    /// </para>
    /// </summary>
    internal sealed class InputSystemKeyInput
    {
        // Close to the OS defaults: half a second before the first repeat, then about 30 a second.
        private const double RepeatDelay = 0.5;
        private const double RepeatInterval = 0.033;

        [Flags]
        private enum Modifiers
        {
            None = 0,
            Shift = 1,
            Ctrl = 2,
            Alt = 4,
            // Command on macOS, the Windows key elsewhere.
            Meta = 8,
        }

        // What is queued: an editing key, or a run of text (a range of _text).
        private struct Entry
        {
            public TextInputEventKind Kind;
            public TextEditIntent Intent;
            public bool Extend;
            public int TextStart;
            public int TextLength;
        }

        private readonly bool _takesText;
        private readonly bool _mac;
        private readonly List<Entry> _queue = new();
        private readonly StringBuilder _text = new();
        private readonly Action<InputEventPtr, InputDevice> _onEvent;
        private readonly Action<InputDevice, InputDeviceChange> _onDeviceChange;
        private readonly Action<char> _onText;
        private readonly Action<bool> _onFocusChanged;
        private bool _enabled;

        // The first half of a character outside the Basic Multilingual Plane (an emoji), which the Input System hands
        // over as two calls, waiting for its second half.
        private char _highSurrogate;

        // The key that repeats while held, on its keyboard, and when it next does (unscaled time).
        private Keyboard _repeatKeyboard;
        private Key _repeatKey;
        private double _repeatAt;

        /// <param name="takesText">Whether it also reports typed text, not only editing keys.</param>
        public InputSystemKeyInput(bool takesText)
        {
            _takesText = takesText;
            _mac = SystemInfo.operatingSystemFamily == OperatingSystemFamily.MacOSX;
            _onEvent = OnEvent;
            _onDeviceChange = OnDeviceChange;
            _onText = OnText;
            _onFocusChanged = OnFocusChanged;
        }

        /// <summary>
        /// Whether it listens to the keyboards. Turning it off forgets what is queued and stops any key repeating.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (value == _enabled) return;
                _enabled = value;
                Forget();
                if (value)
                {
                    InputSystem.onEvent += _onEvent;
                    InputSystem.onDeviceChange += _onDeviceChange;
                    Application.focusChanged += _onFocusChanged;
                }
                else
                {
                    InputSystem.onEvent -= _onEvent;
                    InputSystem.onDeviceChange -= _onDeviceChange;
                    Application.focusChanged -= _onFocusChanged;
                }
                if (!_takesText) return;
                foreach (var device in InputSystem.devices)
                {
                    if (device is Keyboard keyboard)
                        ListenToText(keyboard, value);
                }
            }
        }

        /// <summary>
        /// Whether an IME composition is under way: editing keys, return and escape are then the IME's (moving within
        /// the composition, committing it, cancelling it), and nothing is reported for them.
        /// </summary>
        public bool Composing { get; set; }

        /// <summary>Once a frame, after the Input System's update: repeats the editing key held down, when it is due.</summary>
        public void Update()
        {
            if (_repeatKeyboard == null) return;
            if (Composing || !_repeatKeyboard.added || !_repeatKeyboard[_repeatKey].isPressed)
            {
                StopRepeat();
                return;
            }
            double now = Time.unscaledTimeAsDouble;
            if (now < _repeatAt) return;
            // At most once a frame: a long frame does not let repeats pile up.
            _repeatAt = Math.Max(_repeatAt + RepeatInterval, now);
            if (TryMap(_repeatKeyboard, _repeatKey, ModifiersOf(_repeatKeyboard, default), out var intent, out bool extend)
                && Repeats(intent))
                Enqueue(intent, extend);
        }

        /// <summary>Moves what was typed since the last call into <paramref name="into"/>, in order, as events of <paramref name="session"/>.</summary>
        public void Drain(List<TextInputEvent> into, int session)
        {
            for (int i = 0; i < _queue.Count; i++)
            {
                var entry = _queue[i];
                into.Add(entry.Kind == TextInputEventKind.Intent
                    ? TextInputEvent.ForIntent(session, entry.Intent, entry.Extend)
                    : TextInputEvent.Insert(session, _text.ToString(entry.TextStart, entry.TextLength)));
            }
            _queue.Clear();
            _text.Clear();
        }

        private void Forget()
        {
            _queue.Clear();
            _text.Clear();
            _highSurrogate = '\0';
            StopRepeat();
        }

        private void StopRepeat() => _repeatKeyboard = null;

        private void ListenToText(Keyboard keyboard, bool listen)
        {
            if (listen)
                keyboard.onTextInput += _onText;
            else
                keyboard.onTextInput -= _onText;
        }

        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device is not Keyboard keyboard) return;
            switch (change)
            {
                case InputDeviceChange.Added:
                case InputDeviceChange.Reconnected:
                    if (_takesText)
                        ListenToText(keyboard, true);
                    break;
                case InputDeviceChange.Removed:
                    if (_takesText)
                        ListenToText(keyboard, false);
                    if (keyboard == _repeatKeyboard)
                        StopRepeat();
                    break;
                case InputDeviceChange.SoftReset:
                case InputDeviceChange.HardReset:
                    if (keyboard == _repeatKeyboard)
                        StopRepeat();
                    break;
            }
        }

        private void OnFocusChanged(bool focused)
        {
            if (!focused)
                StopRepeat();
        }

        // ── Text ─────────────────────────────────────────────────────────────────

        private void OnText(char character)
        {
            // In the editor, keys typed while the Game view does not have focus are the editor's.
            if (InputState.currentUpdateType == InputUpdateType.Editor) return;

            if (char.IsHighSurrogate(character))
            {
                _highSurrogate = TypesText() ? character : '\0';
                return;
            }
            if (char.IsLowSurrogate(character))
            {
                if (_highSurrogate != '\0')
                {
                    Append(_highSurrogate);
                    Append(character);
                }
                _highSurrogate = '\0';
                return;
            }
            _highSurrogate = '\0';

            // Control characters (backspace, tab, return, escape, macOS's backspace as 0x7F) and macOS's function keys
            // (U+F700-U+F7FF: arrows, home, page up) are editing keys, read as keys. U+F8FF, the Apple logo, types.
            if (character < ' ' || character == '\u007F' || (character >= '\uF700' && character <= '\uF7FF')) return;
            if (!TypesText()) return;
            Append(character);
        }

        // Whether what the keyboard sends now is text rather than a shortcut: not with Ctrl held (except AltGr, which is
        // Ctrl and Alt), nor with Command (or the Windows key).
        private static bool TypesText()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return true;
            var modifiers = ModifiersOf(keyboard, default);
            if ((modifiers & Modifiers.Meta) != 0) return false;
            return (modifiers & (Modifiers.Ctrl | Modifiers.Alt)) != Modifiers.Ctrl;
        }

        // Adds a character to the text run at the end of the queue, or starts one: a burst of typing in one frame, or an
        // IME's commit, goes in as one insertion.
        private void Append(char character)
        {
            int last = _queue.Count - 1;
            if (last >= 0 && _queue[last].Kind == TextInputEventKind.InsertText)
            {
                var entry = _queue[last];
                entry.TextLength++;
                _queue[last] = entry;
            }
            else
            {
                _queue.Add(new Entry { Kind = TextInputEventKind.InsertText, TextStart = _text.Length, TextLength = 1 });
            }
            _text.Append(character);
        }

        // ── Keys ─────────────────────────────────────────────────────────────────

        // Called for every event before the Input System applies it: a key whose state in the event is pressed while the
        // keyboard still has it released has just gone down.
        private void OnEvent(InputEventPtr eventPtr, InputDevice device)
        {
            if (device is not Keyboard keyboard) return;
            if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>()) return;
            if (InputState.currentUpdateType == InputUpdateType.Editor) return;

            var keys = keyboard.allKeys;
            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                if (key.isPressed || !key.ReadValueFromEvent(eventPtr, out float value) || !key.IsValueConsideredPressed(value))
                    continue;
                OnKeyDown(keyboard, key.keyCode, ModifiersOf(keyboard, eventPtr));
            }
        }

        private void OnKeyDown(Keyboard keyboard, Key key, Modifiers modifiers)
        {
            if (IsModifier(key)) return;
            StopRepeat();
            if (Composing) return;
            if (!TryMap(keyboard, key, modifiers, out var intent, out bool extend)) return;
            Enqueue(intent, extend);
            if (!Repeats(intent)) return;
            _repeatKeyboard = keyboard;
            _repeatKey = key;
            _repeatAt = Time.unscaledTimeAsDouble + RepeatDelay;
        }

        private void Enqueue(TextEditIntent intent, bool extend) =>
            _queue.Add(new Entry { Kind = TextInputEventKind.Intent, Intent = intent, Extend = extend });

        private static bool IsModifier(Key key) =>
            key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftMeta or Key.RightMeta;

        // The modifiers held: as the event has them when there is one (the keyboard has not taken it in yet), otherwise
        // as the keyboard has them now.
        private static Modifiers ModifiersOf(Keyboard keyboard, InputEventPtr eventPtr)
        {
            var modifiers = Modifiers.None;
            if (Held(keyboard.shiftKey, eventPtr)) modifiers |= Modifiers.Shift;
            if (Held(keyboard.ctrlKey, eventPtr)) modifiers |= Modifiers.Ctrl;
            if (Held(keyboard.altKey, eventPtr)) modifiers |= Modifiers.Alt;
            if (Held(keyboard.leftMetaKey, eventPtr) || Held(keyboard.rightMetaKey, eventPtr)) modifiers |= Modifiers.Meta;
            return modifiers;
        }

        private static bool Held(ButtonControl control, InputEventPtr eventPtr) =>
            eventPtr.valid && control.ReadValueFromEvent(eventPtr, out float value)
                ? control.IsValueConsideredPressed(value)
                : control.isPressed;

        // Return, escape, select all, copy and cut do what they do once.
        private static bool Repeats(TextEditIntent intent) =>
            intent != TextEditIntent.Return && intent != TextEditIntent.Cancel && intent != TextEditIntent.SelectAll
            && intent != TextEditIntent.Copy && intent != TextEditIntent.Cut;

        // What a key does in a text field with these modifiers held, on this platform; false for nothing (it is typed, or
        // it is not a text field's key: Tab moves focus).
        private bool TryMap(Keyboard keyboard, Key key, Modifiers modifiers, out TextEditIntent intent, out bool extend)
        {
            bool shift = (modifiers & Modifiers.Shift) != 0;
            var held = modifiers & ~Modifiers.Shift;
            TextEditIntent? mapped = key switch
            {
                // Shift and return: a new line whatever the return key does, as chat apps have it.
                Key.Enter or Key.NumpadEnter when held == Modifiers.None => shift ? TextEditIntent.Newline : TextEditIntent.Return,
                Key.Escape when held == Modifiers.None => TextEditIntent.Cancel,
                _ => _mac ? MacKeyIntent(key, held, shift) : KeyIntent(key, held, shift),
            };
            if (mapped == null && (held == Modifiers.Ctrl || held == Modifiers.Meta))
            {
                char letter = LetterOf(keyboard, key);
                if (letter != '\0')
                    mapped = _mac ? MacLetterIntent(letter, held, shift) : LetterIntent(letter, held, shift);
            }
            intent = mapped.GetValueOrDefault();
            // Shift extends the selection for moves; for the rest it picks the key (Shift-Delete cuts) or does nothing.
            extend = mapped != null && shift && intent >= TextEditIntent.MoveLeft && intent <= TextEditIntent.MovePageDown;
            return mapped != null;
        }

        // Windows, Linux and Android (Flutter's common, clipboard and Windows shortcuts).
        private static TextEditIntent? KeyIntent(Key key, Modifiers held, bool shift) => (key, held, shift) switch
        {
            (Key.LeftArrow, Modifiers.None, _) => TextEditIntent.MoveLeft,
            (Key.LeftArrow, Modifiers.Ctrl, _) => TextEditIntent.MoveWordLeft,
            (Key.LeftArrow, Modifiers.Alt, _) => TextEditIntent.MoveLineStart,
            (Key.RightArrow, Modifiers.None, _) => TextEditIntent.MoveRight,
            (Key.RightArrow, Modifiers.Ctrl, _) => TextEditIntent.MoveWordRight,
            (Key.RightArrow, Modifiers.Alt, _) => TextEditIntent.MoveLineEnd,
            (Key.UpArrow, Modifiers.None, _) => TextEditIntent.MoveUp,
            (Key.UpArrow, Modifiers.Alt, _) => TextEditIntent.MoveDocumentStart,
            (Key.DownArrow, Modifiers.None, _) => TextEditIntent.MoveDown,
            (Key.DownArrow, Modifiers.Alt, _) => TextEditIntent.MoveDocumentEnd,
            (Key.Home, Modifiers.None, _) => TextEditIntent.MoveLineStart,
            (Key.Home, Modifiers.Ctrl, _) => TextEditIntent.MoveDocumentStart,
            (Key.End, Modifiers.None, _) => TextEditIntent.MoveLineEnd,
            (Key.End, Modifiers.Ctrl, _) => TextEditIntent.MoveDocumentEnd,
            (Key.PageUp, Modifiers.None, _) => TextEditIntent.MovePageUp,
            (Key.PageDown, Modifiers.None, _) => TextEditIntent.MovePageDown,
            (Key.Backspace, Modifiers.None, _) => TextEditIntent.DeleteBackward,
            (Key.Backspace, Modifiers.Ctrl, _) => TextEditIntent.DeleteWordBackward,
            (Key.Backspace, Modifiers.Alt, _) => TextEditIntent.DeleteToLineStart,
            (Key.Delete, Modifiers.None, false) => TextEditIntent.DeleteForward,
            (Key.Delete, Modifiers.Ctrl, _) => TextEditIntent.DeleteWordForward,
            (Key.Delete, Modifiers.Alt, _) => TextEditIntent.DeleteToLineEnd,
            // The IBM CUA clipboard keys.
            (Key.Delete, Modifiers.None, true) => TextEditIntent.Cut,
            (Key.Insert, Modifiers.Ctrl, false) => TextEditIntent.Copy,
            (Key.Insert, Modifiers.None, true) => TextEditIntent.Paste,
            _ => null,
        };

        private static TextEditIntent? LetterIntent(char letter, Modifiers held, bool shift) => (letter, held, shift) switch
        {
            ('a', Modifiers.Ctrl, false) => TextEditIntent.SelectAll,
            ('c', Modifiers.Ctrl, false) => TextEditIntent.Copy,
            ('x', Modifiers.Ctrl, false) => TextEditIntent.Cut,
            ('v', Modifiers.Ctrl, false) => TextEditIntent.Paste,
            ('z', Modifiers.Ctrl, false) => TextEditIntent.Undo,
            ('z', Modifiers.Ctrl, true) => TextEditIntent.Redo,
            ('y', Modifiers.Ctrl, false) => TextEditIntent.Redo,
            _ => null,
        };

        // macOS (Flutter's macOS shortcuts): Option moves by word, Command to the line's or the text's ends; Home, End,
        // Page Up and Page Down only scroll there, so alone they do nothing here, and with Shift they select.
        private static TextEditIntent? MacKeyIntent(Key key, Modifiers held, bool shift) => (key, held, shift) switch
        {
            (Key.LeftArrow, Modifiers.None, _) => TextEditIntent.MoveLeft,
            (Key.LeftArrow, Modifiers.Alt, _) => TextEditIntent.MoveWordLeft,
            (Key.LeftArrow, Modifiers.Meta, _) => TextEditIntent.MoveLineStart,
            (Key.RightArrow, Modifiers.None, _) => TextEditIntent.MoveRight,
            (Key.RightArrow, Modifiers.Alt, _) => TextEditIntent.MoveWordRight,
            (Key.RightArrow, Modifiers.Meta, _) => TextEditIntent.MoveLineEnd,
            (Key.UpArrow, Modifiers.None, _) => TextEditIntent.MoveUp,
            (Key.UpArrow, Modifiers.Alt, _) => TextEditIntent.MoveLineStart,
            (Key.UpArrow, Modifiers.Meta, _) => TextEditIntent.MoveDocumentStart,
            (Key.DownArrow, Modifiers.None, _) => TextEditIntent.MoveDown,
            (Key.DownArrow, Modifiers.Alt, _) => TextEditIntent.MoveLineEnd,
            (Key.DownArrow, Modifiers.Meta, _) => TextEditIntent.MoveDocumentEnd,
            (Key.Home, Modifiers.None, true) => TextEditIntent.MoveDocumentStart,
            (Key.End, Modifiers.None, true) => TextEditIntent.MoveDocumentEnd,
            (Key.PageUp, Modifiers.None, true) => TextEditIntent.MovePageUp,
            (Key.PageDown, Modifiers.None, true) => TextEditIntent.MovePageDown,
            (Key.Backspace, Modifiers.None, _) => TextEditIntent.DeleteBackward,
            (Key.Backspace, Modifiers.Alt, _) => TextEditIntent.DeleteWordBackward,
            (Key.Backspace, Modifiers.Meta, _) => TextEditIntent.DeleteToLineStart,
            (Key.Delete, Modifiers.None, _) => TextEditIntent.DeleteForward,
            (Key.Delete, Modifiers.Alt, _) => TextEditIntent.DeleteWordForward,
            (Key.Delete, Modifiers.Meta, _) => TextEditIntent.DeleteToLineEnd,
            _ => null,
        };

        // Command for the clipboard and undo; Ctrl for the Emacs keys every macOS text view takes (Cocoa's standard key
        // bindings).
        private static TextEditIntent? MacLetterIntent(char letter, Modifiers held, bool shift) => (letter, held, shift) switch
        {
            ('a', Modifiers.Meta, false) => TextEditIntent.SelectAll,
            ('c', Modifiers.Meta, false) => TextEditIntent.Copy,
            ('x', Modifiers.Meta, false) => TextEditIntent.Cut,
            ('v', Modifiers.Meta, false) => TextEditIntent.Paste,
            ('z', Modifiers.Meta, false) => TextEditIntent.Undo,
            ('z', Modifiers.Meta, true) => TextEditIntent.Redo,
            ('a', Modifiers.Ctrl, false) => TextEditIntent.MoveLineStart,
            ('e', Modifiers.Ctrl, false) => TextEditIntent.MoveLineEnd,
            ('b', Modifiers.Ctrl, false) => TextEditIntent.MoveLeft,
            ('f', Modifiers.Ctrl, false) => TextEditIntent.MoveRight,
            ('p', Modifiers.Ctrl, false) => TextEditIntent.MoveUp,
            ('n', Modifiers.Ctrl, false) => TextEditIntent.MoveDown,
            ('h', Modifiers.Ctrl, false) => TextEditIntent.DeleteBackward,
            ('d', Modifiers.Ctrl, false) => TextEditIntent.DeleteForward,
            ('k', Modifiers.Ctrl, false) => TextEditIntent.DeleteToLineEnd,
            _ => null,
        };

        // The Latin letter a key types in the keyboard's current layout (AZERTY types A where QWERTY has Q; Dvorak's
        // punctuation keys type none), or, where the layout types another script (Cyrillic), the letter printed on it on
        // a US keyboard, as the OS matches shortcuts. The Input System keeps a key's display name until the layout changes.
        private static char LetterOf(Keyboard keyboard, Key key)
        {
            var name = keyboard[key].displayName;
            if (name != null && name.Length == 1 && name[0] < 0x80)
            {
                char letter = char.ToLowerInvariant(name[0]);
                return letter >= 'a' && letter <= 'z' ? letter : '\0';
            }
            return key >= Key.A && key <= Key.Z ? (char)('a' + (key - Key.A)) : '\0';
        }
    }
}
