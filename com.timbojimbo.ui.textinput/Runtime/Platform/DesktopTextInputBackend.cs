using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// Typing on desktop players and in the editor (the Device Simulator too): there is no software keyboard and no
    /// native input client, so nothing mirrors the value. Keys and text come from the Input System
    /// (<see cref="InputSystemKeyInput"/>) as editing intents and insertions the field carries out itself, and the IME
    /// (Chinese, Japanese, Korean) through the switch, position and composition string Unity's own text fields use
    /// (uGUI's, TextMesh Pro's, UI Toolkit's), which work with the Input System alone.
    /// <para>
    /// The IME is turned on while a session is attached (off for a password), and its candidate window placed below the
    /// caret. Its composition is read each frame (<see cref="Input.compositionString"/>) and reported as it changes; the
    /// Input System's own composition events are listened to as well, and where they fire, they place a change in order
    /// with the text typed around it. Text arriving while something is being composed is the composition committed: the
    /// composing text is dropped first, then the text goes in, whichever order the IME reported the two in. While a
    /// composition is under way, editing keys, return and escape are left to the IME.
    /// </para>
    /// <para>
    /// When our side ends a composition the IME still holds (the field committed it: a click elsewhere, text set from
    /// code, editing ended), the IME is turned off for a frame so that it drops its own copy rather than carry on with it.
    /// A composition under way as the application loses focus is committed as it stands, and the IME's copy dropped the
    /// same way: what the IME does with it meanwhile is not seen (a player that does not run in the background drops
    /// what is typed while it is away), and a composition the IME no longer reports would otherwise take its text with it.
    /// </para>
    /// </summary>
    internal sealed class DesktopTextInputBackend : ITextInputBackend
    {
        private readonly InputSystemKeyInput _keyInput = new(takesText: true);
        private readonly List<TextInputEvent> _events = new();
        private readonly List<TextInputEvent> _typed = new();
        private readonly Action<IMECompositionString> _onImeComposition;
        private readonly Action<InputDevice, InputDeviceChange> _onDeviceChange;
        private readonly Action<bool> _onFocusChanged;

        private int _session;
        private bool _attached;
        private bool _secure;

        // The composition last reported (empty: none), and what it was when the events were last handed over: what the
        // client has been told, once it has taken them all in.
        private string _composition = string.Empty;
        private string _handed = string.Empty;

        // Whether the client has pushed a value since the last update, and whether the last one it pushed was composing.
        // The client takes in what was handed over one event at a time, pushing as it goes, so whether a value without a
        // composition is its own doing is known only once it has taken them all in: at the next update.
        private bool _pushed;
        private bool _pushedComposing;

        // The IME switch is set at the next update: after a session detached (unless another attaches within the frame,
        // as switching fields does, which leaves the IME as it is), or after the IME was turned off to drop a composition.
        private bool _imeModeStale;

        // The IME was turned off to drop a composition; until the next update turns it back on, what it reports belongs
        // to the composition dropped.
        private bool _imeDropping;

        // The application lost focus since the last update. A composition still under way at the update is committed
        // there, after the Input System has handed over what was typed before (the IME's own commit, where it made one).
        private bool _focusLost;

        // Which of the two composition sources have been seen working (reporting a composition) on this platform. The
        // string read each frame has no length limit (the Input System's events carry 64 UTF-16 units at most) and has
        // the last word each frame; the events alone are used only where it never shows one.
        private bool _pollWorks;
        private bool _eventsWork;

        public DesktopTextInputBackend()
        {
            _onImeComposition = OnImeComposition;
            _onDeviceChange = OnDeviceChange;
            _onFocusChanged = OnFocusChanged;
            Application.quitting += OnQuitting;
        }

        public bool SupportsEditMenu => false;

        public float KeyboardHeight => 0f;

        public bool KeyboardVisible => false;

        // A desktop keyboard's layout is the system's business: text with no letter yet reads left to right.
        public bool KeyboardRightToLeft => false;

        public void Attach(int session, in TextInputConfig config, in TextEditingValue value, int serial)
        {
            if (!_attached)
                Listen(true);
            _session = session;
            _attached = true;
            _secure = config.IsSecure;
            _composition = string.Empty;
            _handed = string.Empty;
            _pushed = false;
            _focusLost = false;
            _events.Clear();
            _keyInput.Composing = false;
            _keyInput.Enabled = true;
            if (!_imeDropping)
            {
                _imeModeStale = false;
                ApplyImeMode();
            }
        }

        public void Detach(int session)
        {
            if (!_attached || session != _session) return;
            _attached = false;
            _keyInput.Enabled = false;
            Listen(false);
            _events.Clear();
            _handed = string.Empty;
            _pushed = false;
            if (_composition.Length > 0)
            {
                _composition = string.Empty;
                ResetIme();
            }
            else
            {
                _imeModeStale = true;
            }
        }

        public void SetConfig(int session, in TextInputConfig config)
        {
            if (!_attached || session != _session) return;
            bool secure = config.IsSecure;
            if (secure == _secure) return;
            _secure = secure;
            // A field turned into a password field mid-composition: the IME goes off, and its composition with it.
            if (secure)
                SetComposition(string.Empty);
            if (!_imeDropping)
                ApplyImeMode();
        }

        public void SetValue(int session, int serial, in TextEditingValue value, TextChangeKind kind)
        {
            if (!_attached || session != _session) return;
            _pushed = true;
            _pushedComposing = value.IsComposing;
        }

        // Nothing on desktop asks which way the text reads.
        public void SetDirection(int session, bool rightToLeft, bool caretRightToLeft)
        {
        }

        public void SetGeometry(int session, Rect field, Rect caret, Rect composing)
        {
            if (!_attached || session != _session) return;
            // The IME opens its candidate window below this point, given in pixels down from the top of the screen.
            Input.compositionCursorPos = new Vector2(caret.xMin, Screen.height - caret.yMin);
        }

        public void ShowEditMenu(int session, Rect target, TextEditActions actions)
        {
            // There is none on desktop; the field's shortcuts do what it would.
        }

        public void HideEditMenu()
        {
        }

        public void Update()
        {
            bool dropping = _imeDropping;
            _imeDropping = false;
            if (_imeModeStale)
            {
                _imeModeStale = false;
                ApplyImeMode();
            }
            if (!_attached) return;

            // The client has taken in everything handed over: if it was handed a composition and its last value has none,
            // it ended the composition itself.
            if (_pushed)
            {
                _pushed = false;
                if (!_pushedComposing && _handed.Length > 0 && _composition.Length > 0)
                {
                    DropComposition();
                    dropping = true;
                }
            }

            _keyInput.Update();
            TakeTyped();
            // The application lost focus mid-composition, and what was typed before it has not committed it.
            if (_focusLost)
            {
                _focusLost = false;
                if (_composition.Length > 0)
                {
                    CommitComposition();
                    dropping = true;
                }
            }
            // The composition is the IME's while the application has focus; in the editor, while the Game view has it
            // (otherwise it may be an editor text field's).
            if (!dropping && !_secure && Application.isFocused)
                PollComposition();
            // A composition that ended this frame keeps the keys to the IME until now, so the return that committed it
            // is not also taken as the return key, whichever order the IME reported the two in.
            _keyInput.Composing = _composition.Length > 0;
        }

        public void DrainEvents(List<TextInputEvent> into)
        {
            for (int i = 0; i < _events.Count; i++)
                into.Add(_events[i]);
            _events.Clear();
            _handed = _composition;
        }

        // Moves what was typed (keys and text) into the events, the composing text dropped before text that commits it.
        private void TakeTyped()
        {
            _keyInput.Drain(_typed, _session);
            for (int i = 0; i < _typed.Count; i++)
            {
                var e = _typed[i];
                if (e.Kind == TextInputEventKind.InsertText)
                    SetComposition(string.Empty);
                _events.Add(e);
            }
            _typed.Clear();
        }

        private void PollComposition()
        {
            var composition = Input.compositionString ?? string.Empty;
            if (composition.Length > 0)
                _pollWorks = true;
            if (_eventsWork && !_pollWorks) return;
            SetComposition(composition);
        }

        private void SetComposition(string composition)
        {
            if (string.Equals(composition, _composition, StringComparison.Ordinal)) return;
            _composition = composition;
            if (composition.Length > 0)
                _keyInput.Composing = true;
            _events.Add(TextInputEvent.Compose(_session, composition));
        }

        // ── The IME ──────────────────────────────────────────────────────────────

        // The client ended the composition it was handed (it committed it) while the IME still holds one: the IME is made
        // to drop it. What the client has not been handed yet (the IME carried on this frame) is taken back from it.
        private void DropComposition()
        {
            if (string.Equals(_composition, _handed, StringComparison.Ordinal))
                _composition = string.Empty;
            else
                SetComposition(string.Empty);
            ResetIme();
        }

        // Our side commits the composition as it stands, as the IME commits one (the composing text dropped, then the text
        // inserted), and the IME is made to drop its own copy.
        private void CommitComposition()
        {
            var committed = _composition;
            SetComposition(string.Empty);
            _events.Add(TextInputEvent.Insert(_session, committed));
            ResetIme();
        }

        // Unity's IME switch: on for typing (the IME composes), off for a password (keys type as they are), and back to
        // Unity's own choice (on in its IMGUI text fields) with nothing attached. Set only when it changes.
        private void ApplyImeMode()
        {
            var mode = !_attached ? IMECompositionMode.Auto : _secure ? IMECompositionMode.Off : IMECompositionMode.On;
            if (Input.imeCompositionMode != mode)
                Input.imeCompositionMode = mode;
        }

        // Turns the IME off until the next update, which drops the composition it holds.
        private void ResetIme()
        {
            Input.imeCompositionMode = IMECompositionMode.Off;
            _imeDropping = true;
            _imeModeStale = true;
        }

        // While a session is attached: the keyboards' composition events (keyboards added later too) and the
        // application's focus.
        private void Listen(bool listen)
        {
            foreach (var device in InputSystem.devices)
            {
                if (device is Keyboard keyboard)
                    ListenToIme(keyboard, listen);
            }
            if (listen)
            {
                InputSystem.onDeviceChange += _onDeviceChange;
                Application.focusChanged += _onFocusChanged;
            }
            else
            {
                InputSystem.onDeviceChange -= _onDeviceChange;
                Application.focusChanged -= _onFocusChanged;
            }
        }

        private void ListenToIme(Keyboard keyboard, bool listen)
        {
            if (listen)
                keyboard.onIMECompositionChange += _onImeComposition;
            else
                keyboard.onIMECompositionChange -= _onImeComposition;
        }

        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device is not Keyboard keyboard) return;
            if (change == InputDeviceChange.Added || change == InputDeviceChange.Reconnected)
                ListenToIme(keyboard, true);
            else if (change == InputDeviceChange.Removed)
                ListenToIme(keyboard, false);
        }

        // During the Input System's update, in order with the keys and text around it.
        private void OnImeComposition(IMECompositionString composition)
        {
            if (!_attached || _secure || _imeDropping) return;
            // In the editor, what is typed while the Game view does not have focus is the editor's.
            if (InputState.currentUpdateType == InputUpdateType.Editor) return;
            // What was typed before it goes first.
            TakeTyped();
            var text = composition.ToString();
            if (text.Length > 0)
                _eventsWork = true;
            SetComposition(text);
        }

        // The composition is dealt with at the next update, which comes after the Input System's: a player that does not
        // run in the background pauses until it has focus again, and hands over then what was typed before it lost it.
        private void OnFocusChanged(bool focused)
        {
            if (!focused)
                _focusLost = true;
        }

        // Leaving play mode in the editor (statics, and so this backend, outlive it with domain reload off): the keyboard
        // and focus listeners go and Unity's IME switch is given back.
        private void OnQuitting()
        {
            Application.quitting -= OnQuitting;
            if (_attached)
                Detach(_session);
            _imeDropping = false;
            _imeModeStale = false;
            Input.imeCompositionMode = IMECompositionMode.Auto;
        }
    }
}
