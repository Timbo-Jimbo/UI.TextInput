using System;
using TimboJimbo.UI.Focus;
using TimboJimbo.UI.Layout;
using TimboJimbo.UI.Motion;
using TimboJimbo.UI.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>Raised with a <see cref="TextField"/>'s text.</summary>
    [Serializable]
    public sealed class TextFieldEvent : UnityEvent<string>
    {
    }

    /// <summary>
    /// A text field our own engine draws and the platform's own keyboard types into, as a browser's or Flutter's: the
    /// text is a <see cref="TextBlock"/> in a viewport that clips and scrolls it, and the field draws its caret, the
    /// selection, the underline under text still being composed and, after a touch selection, the handles at its ends,
    /// while <see cref="TextInputSystem"/> connects it to the keyboard, so autocorrect, suggestions, composition (Chinese,
    /// Japanese, Korean), dictation and the system's edit menu all work. On desktop and in the editor it takes the Input
    /// System's keys and the IME, with the shortcuts a desktop field has.
    /// <para>
    /// Editing begins on a tap or a click, on Submit (Enter or a gamepad's A on the selected field), on Tab bringing focus
    /// to it, as tabbing into a browser's field leaves it ready to type into, or on <see cref="BeginEditing"/>. It ends on
    /// <see cref="EndEditing"/>, Escape or a gamepad's B, the keyboard being dismissed, another field beginning, the field
    /// being hidden or disabled, and, with a mouse or a keyboard, as focus moves elsewhere, as a browser blurs a field. A
    /// touch elsewhere does not end it, as iOS keeps the keyboard up whatever is tapped (a chat's Send): the field keeps
    /// UGUI's selection while it is edited, as UIKit's first responder stays, so the keys are its own whatever was tapped.
    /// UGUI's navigation and the focus system leave them to it, bar Tab, which moves focus on.
    /// </para>
    /// <para>
    /// A single-line field is one line high and scrolls sideways to keep the caret in view; a multi-line one grows with
    /// its lines, on a quick spring, up to its most lines, then scrolls up and down. As the software keyboard rises, the
    /// nearest scroll container above the field brings it into view. <see cref="TextFieldBuilder"/> makes the nodes a
    /// field needs; a field built by hand is given them with <see cref="Setup"/>.
    /// </para>
    /// </summary>
    [AddComponentMenu("Timbo Jimbo/UI/Text Field")]
    [RequireComponent(typeof(LayoutNode))]
    public sealed partial class TextField : Selectable, ITextInputClient, ISubmitHandler, IUpdateSelectedHandler,
        IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IScrollHandler, IFocusMoveHandler, IFocusKeyHandler
    {
        // What a secure field shows for each UTF-16 unit of its text, so that indices into what is shown are the text's.
        private const char SecureBullet = '•';

        // The quick spring a multi-line field grows and shrinks on as its lines come and go, with what is around it.
        private static readonly MotionAnimation s_resize = new MotionAnimation(0.25f).Use(MotionAnimationPreset.Snappy);

        [Tooltip("The text being edited. The field sets it up for editing: plain text (no markup), taking no pointer, wrapped when multi-line.")]
        [SerializeField] private TextBlock _text;

        [Tooltip("The text shown while the field is empty.")]
        [SerializeField] private TextBlock _placeholder;

        [Tooltip("The node the text is in, which clips and scrolls it: sideways for a single line, up and down past Max Lines. The field sizes it by the text's line height.")]
        [SerializeField] private LayoutNode _viewport;

        [Tooltip("What the field asks of the platform's keyboard, and whether it is multi-line.")]
        [SerializeField] private TextInputConfig _config;

        [Tooltip("A multi-line field's: how many lines it grows to before it scrolls. 0: it grows with all its lines.")]
        [SerializeField, Min(0)] private int _maxLines = 1;

        [Tooltip("The most UTF-16 units typing and pasting can bring the text to. 0: no limit.")]
        [SerializeField, Min(0)] private int _maxLength;

        [Tooltip("The colour of the caret and of the selection handles.")]
        [SerializeField] private Color _caretColour = new(0.04f, 0.52f, 1f, 1f);

        [Tooltip("The colour drawn behind selected text.")]
        [SerializeField] private Color _selectionColour = new(0.04f, 0.52f, 1f, 0.25f);

        [Tooltip("The text it holds.")]
        [SerializeField, TextArea] private string _content = "";

        [SerializeField] private TextFieldEvent _textChanged = new();
        [SerializeField] private TextFieldEvent _submitted = new();
        [SerializeField] private UnityEvent _editingBegan = new();
        [SerializeField] private UnityEvent _editingEnded = new();

        private readonly TextEditHistory _history = new();
        private TextEditingValue _value;
        private bool _editing;

        // The frame editing last ended in. That frame's keys came to the field while it was being edited (they are handed
        // on early in the frame), and UGUI reads the same keys later in it, so it is kept from them for the rest of the
        // frame too: the Enter that submitted would otherwise begin editing again through OnSubmit.
        private int _endedFrame = -1;

        // The selection was placed (by a tap, a click or SelectAll) before editing began, so beginning keeps it rather
        // than putting the caret at the end.
        private bool _placed;

        // The caret's affinity, kept with the selection's extent, as UIKit's and Flutter's carets keep theirs: at an index
        // where a line wraps, which ends one line and starts the next, whether it stands at the end of the line before
        // (after End, or a tap or a move past a wrapped line's end) rather than at the start of the line after. Any other
        // move, and any edit of the text, puts it back.
        private bool _upstream;

        // The x the caret keeps to as it moves up and down through shorter lines, in the text's space; NaN when it is
        // to be taken from where the caret is.
        private float _goalX = float.NaN;

        // The line height the viewport was last sized by.
        private float _sizedFor = float.NaN;

        private LayoutNode _node;
        private LayoutNode _placeholderNode;

        /// <summary>
        /// Gives the field the nodes it edits in: <paramref name="text"/>, on a node inside <paramref name="viewport"/>,
        /// which clips and scrolls it, and <paramref name="placeholder"/> (optional), shown while the field is empty. The
        /// field sets them up for editing: the text plain (no markup), taking no pointer, wrapped and breaking long words
        /// anywhere when multi-line, growing to fill the viewport; the viewport scrolling sideways for a single line and
        /// up and down for several, kept at its end as the text grows while the caret is at the end of the text being
        /// edited, and sized by the text's line height, one line for a single line, otherwise growing from one line up to
        /// <paramref name="maxLines"/> (0: with all its lines). The caret and its handles are drawn in
        /// <paramref name="caretColour"/>, the selection behind the text in <paramref name="selectionColour"/>. Builders
        /// call it once (<see cref="TextFieldBuilder"/>); a field put together by hand, in the inspector, is set up the same
        /// way as it wakes in play mode.
        /// </summary>
        public void Setup(TextBlock text, TextBlock placeholder, LayoutNode viewport, TextInputConfig config, int maxLines,
            Color caretColour, Color selectionColour)
        {
            _text = text;
            _placeholder = placeholder;
            _placeholderNode = null;
            _viewport = viewport;
            _config = config;
            _maxLines = Mathf.Max(0, maxLines);
            _caretColour = caretColour;
            _selectionColour = selectionColour;
            Prepare();
            ShowValue(animate: false);
        }

        /// <summary>
        /// The text. Set from code, it replaces the whole text (committing any composition first), the caret at its end,
        /// as one step undo can take back; a single-line field takes its line breaks out. The keyboard is told, and
        /// <see cref="TextChanged"/> raised.
        /// </summary>
        public string Text
        {
            get => _value.Text;
            set
            {
                var text = Clean(value);
                if (string.Equals(text, _value.Text, StringComparison.Ordinal)) return;
                CommitComposition();
                Change(TextEditingValue.FromText(text), record: true, coalesce: false, notify: true);
            }
        }

        /// <summary>The text, what is selected and what is still being composed.</summary>
        public TextEditingValue Value => _value;

        /// <summary>
        /// What the field asks of the keyboard, and whether it is multi-line. Changed while editing, the keyboard is
        /// reloaded for it; changed to a single line, the text's line breaks are taken out.
        /// </summary>
        public TextInputConfig Config
        {
            get => _config;
            set
            {
                if (_config.Equals(value)) return;
                bool lines = value.Multiline != _config.Multiline;
                _config = value;
                if (lines)
                {
                    ApplyStructure();
                    var flat = Flatten(_value.CommitComposition());
                    if (!string.Equals(flat.Text, _value.Text, StringComparison.Ordinal))
                        Change(flat, record: true, coalesce: false, notify: true);
                }
                ShowValue(animate: false);
                TextInputSystem.NotifyConfigChanged(this);
            }
        }

        /// <summary>
        /// The most UTF-16 units typing, pasting and the keyboard can bring the text to (0, the default: no limit), as a
        /// browser's maxlength: what would go past it is cut off, at a whole character. Text still being composed is
        /// left alone until it is committed, as the keyboard is still working on it, and cut then, however it is
        /// committed. Text set from code is not cut.
        /// </summary>
        public int MaxLength
        {
            get => _maxLength;
            set => _maxLength = Mathf.Max(0, value);
        }

        /// <summary>Whether it is being edited: it has the keyboard, and draws its caret.</summary>
        public bool IsEditing => _editing;

        /// <summary>The text's rect, for placing something over the text (a copy of it flying away as it is sent).</summary>
        public RectTransform TextRect => _text != null ? _text.rectTransform : null;

        /// <summary>Raised with the text whenever it changes, however it changes.</summary>
        public TextFieldEvent TextChanged => _textChanged;

        /// <summary>
        /// Raised with the text when the return key submits it rather than starting a new line. Editing then ends, as
        /// SwiftUI's TextField gives up focus on submit, unless the return key says Send (a chat's composer, typed on
        /// after each message, as in Messages) or a handler began editing another field.
        /// </summary>
        public TextFieldEvent Submitted => _submitted;

        /// <summary>Raised as editing begins.</summary>
        public UnityEvent EditingBegan => _editingBegan;

        /// <summary>Raised as editing ends.</summary>
        public UnityEvent EditingEnded => _editingEnded;

        private bool Multiline => _config.Multiline;

        private LayoutNode Node => _node != null ? _node : (_node = GetComponent<LayoutNode>());

        private LayoutNode PlaceholderNode
        {
            get
            {
                if (_placeholderNode == null && _placeholder != null)
                    _placeholder.TryGetComponent(out _placeholderNode);
                return _placeholderNode;
            }
        }

        // ── Editing ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Begins editing: the field takes the keyboard (<see cref="TextInputSystem.Begin"/>), and focus, with the caret
        /// at the end of the text unless a tap, a click or <see cref="SelectAll"/> placed the selection first. Another
        /// field being edited stops. Only in play mode, while the field is enabled and interactable.
        /// </summary>
        public void BeginEditing()
        {
            if (_editing || !Application.isPlaying || !IsActive() || !IsInteractable() || _text == null) return;
            if (!_placed)
                Change(_value.WithSelection(TextSelection.Collapsed(_value.Text.Length)), record: false, coalesce: false, notify: false);
            _editing = true;
            AnchorViewport();
            _blinkFrom = Time.unscaledTime;
            _reveal = true;
            _bringIntoView = true;
            _keyboardHeight = TextInputSystem.KeyboardHeight;
            TextInputSystem.KeyboardChanged += OnKeyboardChanged;
            // Before the system first hooks its own: the field's drawing pass then runs before the geometry is sent.
            Hook();

            // Focus comes to it, as focusing a browser's field does; a field focus leaves stops being edited.
            var events = EventSystem.current;
            if (events != null && !events.alreadySelecting && events.currentSelectedGameObject != gameObject)
                events.SetSelectedGameObject(gameObject);

            TextInputSystem.Begin(this);
            _editingBegan.Invoke();
        }

        /// <summary>
        /// Ends editing: any composition is committed as it stands, the keyboard is given back and the caret, selection
        /// and handles go. Focus stays on the field, as a browser's field keeps it after Escape. A single-line field
        /// scrolls back to its start, as UITextField does.
        /// </summary>
        public void EndEditing()
        {
            if (!_editing) return;
            CommitComposition();
            _editing = false;
            _endedFrame = Time.frameCount;
            _placed = false;
            _mouseSelecting = false;
            _touchSelecting = false;
            TextInputSystem.KeyboardChanged -= OnKeyboardChanged;
            HideMenu();
            TextInputSystem.End(this);
            HideGraphics();
            AnchorViewport();
            if (!Multiline && _viewport != null)
                _viewport.ScrollOffset = Vector2.zero;
            _editingEnded.Invoke();
        }

        /// <summary>Selects all the text. Before editing begins, beginning keeps it selected.</summary>
        public void SelectAll()
        {
            if (!_editing) _placed = true;
            SetSelection(new TextSelection(0, _value.Text.Length));
        }

        /// <summary>
        /// Puts <paramref name="text"/> in place of the selection, the caret after it, as typing it would (a suggestion
        /// chip, an emoji picker): any composition is committed first, a single-line field takes its line breaks out,
        /// and <see cref="MaxLength"/> cuts it short. One step for undo.
        /// </summary>
        public void Insert(string text) => Put(Clean(text), coalesce: false);

        // ── Lifecycle ────────────────────────────────────────────────────────────

        protected override void Awake()
        {
            base.Awake();
            // A field put together by hand, in the inspector, is set up for editing as it wakes, as Setup sets up one
            // built in code; in edit mode its nodes are left as they were made.
            if (Application.isPlaying)
                Prepare();
            else
                _value = TextEditingValue.FromText(_content);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (Application.isPlaying)
                ShowValue(animate: false);
        }

        protected override void OnDisable()
        {
            EndEditing();
            LetGoOfPointer();
            Unhook();
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            Unhook();
            DestroyGraphics();
            base.OnDestroy();
        }

        // ── The value ────────────────────────────────────────────────────────────

        // Takes `after` up as the field's value: shown, recorded for undo (when `record`: everything but what undo and redo
        // give back; the history keeps a composition out until it is committed, and a typing run as one step when
        // `coalesce`), the keyboard told when `notify`, and TextChanged raised when the text changed. The caret stops
        // blinking for a moment and is brought into view, and the edit menu goes. An edit or a move puts the caret's
        // affinity back downstream and lets go of the column it kept to; a composing range marked out alone leaves both,
        // as nothing moved (Android's keyboards mark out the word the caret lands on, after each move they did not make).
        private void Change(in TextEditingValue after, bool record, bool coalesce, bool notify)
        {
            var before = _value;
            if (after == before) return;
            if (record)
                _history.Record(before, after, coalesce);

            bool textChanged = !string.Equals(before.Text, after.Text, StringComparison.Ordinal);
            _value = after;
            _content = after.Text;
            if (textChanged || after.Selection != before.Selection)
            {
                _goalX = float.NaN;
                _upstream = false;
            }
            if (_editing)
            {
                _blinkFrom = Time.unscaledTime;
                _reveal = true;
            }
            AnchorViewport();
            HideMenu();
            ShowValue(animate: true);
            if (notify)
                TextInputSystem.NotifyValueChanged(this);
            if (textChanged)
                _textChanged.Invoke(after.Text);
        }

        // Moves the selection, committing any composition first, as tapping elsewhere in a composition commits it.
        // `upstream`: the caret's affinity at the selection's extent (see _upstream), for as long as the commit leaves the
        // extent where it was asked for.
        private void SetSelection(TextSelection selection, bool upstream = false)
        {
            Change(Committed(selection), record: true, coalesce: false, notify: true);
            // The affinity is set after the change, which puts it back downstream as the caret moves. Changing on its own,
            // it takes the caret to the other end of the wrap, as a move would.
            upstream &= _value.Selection.Extent == selection.Extent;
            if (upstream == _upstream) return;
            _upstream = upstream;
            if (_editing)
            {
                _blinkFrom = Time.unscaledTime;
                _reveal = true;
            }
        }

        // Commits the composition under way, as it stands bar what MaxLength cuts, as one step for undo. The keyboard is not
        // told: what commits it tells it what follows.
        private void CommitComposition()
        {
            if (_value.IsComposing)
                Change(Committed(_value.Selection), record: true, coalesce: false, notify: false);
        }

        // The value with its composition committed and `selection` (given in the text as it is) in place: what was being
        // composed, let through uncut while it was under way, is cut to fit MaxLength now, the selection kept on the same
        // characters.
        private TextEditingValue Committed(TextSelection selection)
        {
            var committed = _value.CommitComposition().WithSelection(selection);
            return _value.IsComposing ? Limit(_value, committed) : committed;
        }

        // Puts `text` in place of the selection, committing any composition first, cut to fit MaxLength.
        private void Put(string text, bool coalesce)
        {
            var value = Committed(_value.Selection);
            var range = value.Selection.Range;
            text = Fit(value, range, text);
            if (text.Length == 0) return;
            Change(value.Replace(range, text), record: true, coalesce: coalesce, notify: true);
        }

        // Shows the value: the text (bullets for a secure field), and the placeholder while it is empty. A multi-line
        // field whose lines shown change grows or shrinks on a quick spring (`animate`), as a chat's composer does; the
        // text is laid out first to know.
        private void ShowValue(bool animate)
        {
            if (_text == null) return;
            SizeViewport();
            ShowPlaceholder();
            var display = DisplayOf(_value.Text);
            if (string.Equals(_text.Text, display, StringComparison.Ordinal)) return;
            if (!animate || !Multiline || !Application.isPlaying || !_text.isActiveAndEnabled || _text.rectTransform.rect.width <= 0f)
            {
                _text.Text = display;
                return;
            }

            int before = LinesShown();
            var previous = _text.Text;
            _text.Text = display;
            if (LinesShown() == before) return;
            _text.Text = previous;
            MotionSystem.Animate(s_resize, () => _text.Text = display);
        }

        // How many lines the viewport shows of the text as it is laid out now: all of them, up to the most it grows to.
        private int LinesShown()
        {
            _text.EnsureLayout();
            int lines = _text.LineCount;
            return _maxLines > 0 ? Mathf.Min(lines, _maxLines) : lines;
        }

        private string DisplayOf(string text) =>
            _config.IsSecure && text.Length > 0 ? new string(SecureBullet, text.Length) : text;

        private void ShowPlaceholder()
        {
            if (_placeholder == null) return;
            bool shown = _value.Text.Length == 0;
            var node = PlaceholderNode;
            if (node != null)
            {
                var display = shown ? DisplayMode.Visible : DisplayMode.Hidden;
                if (node.Display != display)
                    node.Display = display;
            }
            else if (_placeholder.enabled != shown)
            {
                _placeholder.enabled = shown;
            }
        }

        // Takes up the text it holds as its value (a single line's line breaks taken out), and sets its nodes up for
        // editing (see Setup).
        private void Prepare()
        {
            _value = Flatten(TextEditingValue.FromText(_content));
            _content = _value.Text;
            ApplyStructure();
        }

        // Sets the text and the viewport up for editing (see Setup).
        private void ApplyStructure()
        {
            if (_text == null) return;
            bool multiline = Multiline;
            _text.RichText = false;
            _text.raycastTarget = false;
            _text.WordWrap = multiline;
            _text.BreakWordsAnywhere = multiline;
            _text.Overflow = TextBlockOverflow.Overflow;
            _text.MaxLines = 0;
            if (_text.TryGetComponent(out LayoutNode textNode))
            {
                // At least as wide as the viewport, so the text's alignment is within it; wider, a single line scrolls.
                textNode.Width = Sizing.Grow();
                textNode.Height = Sizing.Fit();
            }
            if (_viewport != null)
                _viewport.Scroll = multiline ? ScrollAxis.Vertical : ScrollAxis.Horizontal;
            AnchorViewport();
            _sizedFor = float.NaN;
            SizeViewport();
        }

        // While the caret is at the end of the text being edited, the viewport keeps to its end as the text grows, so
        // typing there stays in view in the same frame. Otherwise its offset stays as it is as the text changes, and the
        // caret is brought into view from the next frame (Reveal): typing earlier in the text does not throw it to the end
        // for a frame, and a field not being edited shows the start of its text, as UITextField and UITextView do.
        private void AnchorViewport()
        {
            if (_viewport == null) return;
            var selection = _value.Selection;
            var anchor = _editing && selection.IsCollapsed && selection.Extent == _value.Text.Length ? ScrollAnchor.End : ScrollAnchor.Start;
            if (_viewport.ScrollAnchor != anchor)
                _viewport.ScrollAnchor = anchor;
        }

        // The viewport is one line high for a single line, and for several grows from one line up to the most it shows,
        // by the text's line height; sized again only when that changes (a new font size).
        private void SizeViewport()
        {
            if (_viewport == null || _text == null) return;
            float line = _text.LineHeight;
            if (line == _sizedFor) return;
            _sizedFor = line;
            _viewport.Height = Multiline ? Sizing.Fit(line, _maxLines > 0 ? line * _maxLines : 0f) : Sizing.Fixed(line);
        }

        // ── ITextInputClient ─────────────────────────────────────────────────────

        void ITextInputClient.ApplyPlatformValue(in TextEditingValue value)
        {
            var after = Limit(_value, Flatten(value));
            // Filtered, the keyboard is told what the field kept.
            Change(after, record: true, coalesce: true, notify: after != value);
        }

        void ITextInputClient.InsertText(string text)
        {
            text = Clean(text);
            var range = _value.IsComposing ? _value.Composing : _value.Selection.Range;
            text = Fit(_value, range, text);
            if (text.Length == 0) return;
            Change(_value.Replace(range, text), record: true, coalesce: true, notify: true);
        }

        void ITextInputClient.SetComposingText(string text)
        {
            text ??= string.Empty;
            var value = _value;
            var range = value.IsComposing ? value.Composing : value.Selection.Range;
            if (text.Length == 0)
            {
                // The composition gone: its text goes with it.
                if (value.IsComposing)
                    Change(value.Replace(range, string.Empty), record: true, coalesce: true, notify: true);
                return;
            }
            // The composition (or what was selected, as it starts) becomes the text, underlined, the caret at its end.
            var composed = value.Replace(range, text).WithComposing(new TextRange(range.Start, range.Start + text.Length));
            Change(composed, record: true, coalesce: true, notify: true);
        }

        void ITextInputClient.OnSessionEnded() => EndEditing();

        // ── Focus ────────────────────────────────────────────────────────────────

        // While it is edited UGUI's navigation leaves its keys alone: no Move, Submit or Cancel from the input module. So
        // it does for the rest of the frame editing ended in, whose keys were the field's (see _endedFrame). A gamepad's
        // B ends editing here, as Escape does: nothing else on a gamepad would, the field keeping Cancel from UGUI and from
        // focus while it is edited.
        void IUpdateSelectedHandler.OnUpdateSelected(BaseEventData eventData)
        {
            if (_editing && CancelPressedOffKeyboard(eventData.currentInputModule as InputSystemUIInputModule))
                EndEditing();
            if (_editing || _endedFrame == Time.frameCount)
                eventData.Use();
        }

        // Whether the input module's Cancel was pressed this frame on something other than a keyboard (a gamepad's B). A
        // keyboard's Escape comes to the field as the Cancel intent instead, and is the IME's while it composes.
        private static bool CancelPressedOffKeyboard(InputSystemUIInputModule module)
        {
            var cancel = module != null && module.cancel != null ? module.cancel.action : null;
            if (cancel == null || !cancel.WasPressedThisFrame()) return false;
            var controls = cancel.controls;
            for (int i = 0; i < controls.Count; i++)
            {
                if (controls[i] is ButtonControl button && button.device is not Keyboard && button.wasPressedThisFrame)
                    return true;
            }
            return false;
        }

        // Enter or a gamepad's A on the field begins editing it.
        void ISubmitHandler.OnSubmit(BaseEventData eventData)
        {
            if (!_editing)
                BeginEditing();
        }

        // Every direction is its own while it is edited: the arrows move the caret, and focus stays.
        bool IFocusMoveHandler.HandlesMove(MoveDirection direction) => _editing;

        // Enter and Escape are its own while it is edited; Tab moves focus on, as a browser's does.
        bool IFocusKeyHandler.HandlesKey(FocusKey key) => _editing && key != FocusKey.Tab;

        /// <summary>
        /// Focus brought to the field by Tab (or Shift-Tab) begins editing, as tabbing into a browser's field leaves it
        /// ready to type into; focus brought by the arrows or a gamepad only focuses it, and Submit begins editing.
        /// </summary>
        public override void OnSelect(BaseEventData eventData)
        {
            base.OnSelect(eventData);
            var keyboard = Keyboard.current;
            if (!_editing && keyboard != null && keyboard.tabKey.wasPressedThisFrame)
                BeginEditing();
        }

        /// <summary>
        /// Focus moving elsewhere with a mouse or a keyboard ends editing, as a browser blurs a field; a touch elsewhere
        /// (a chat's Send button) does not, as iOS keeps the keyboard up whatever is tapped, but it closes the edit menu,
        /// as it does on iOS and Android, and the field takes the selection back as the frame ends.
        /// </summary>
        public override void OnDeselect(BaseEventData eventData)
        {
            base.OnDeselect(eventData);
            if (!_editing) return;
            if (IsTouch(eventData))
                HideMenu();
            else
                EndEditing();
        }

        // While it is edited the field holds UGUI's selection, as UIKit's first responder stays whatever a tap presses. A
        // touch elsewhere takes it (UGUI selects what is pressed, or nothing on the page behind) without ending the
        // editing, and it is taken back here, once a frame after layout: before focus goes by it at the end of the frame,
        // and before the next frame's keys do, so a hardware keyboard's keys reach the field alone rather than what was
        // tapped too (an Enter sending twice, through the field and through the Send button tapped). What was tapped still
        // acts as it is let go of: a button clicks on release, selected or not. Any other change of selection has ended
        // the editing (OnDeselect).
        private void KeepSelection()
        {
            var events = EventSystem.current;
            if (events != null && !events.alreadySelecting && events.currentSelectedGameObject != gameObject)
                events.SetSelectedGameObject(gameObject);
        }
    }
}
