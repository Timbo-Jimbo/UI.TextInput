using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace TimboJimbo.UI.TextInput
{
    // The pointer, told apart by what it is (ExtendedPointerEventData.pointerType), never by the platform, so a touch
    // screen on a laptop and a mouse on an iPad each get their own.
    //
    // A mouse or a pen selects as on a desktop: a press places the caret (shift extends the selection to it), a drag
    // selects from there with no drag threshold, a double click selects a word and a triple click a paragraph, a drag
    // after either going a word or a paragraph at a time; held outside the viewport, the selection carries on and the
    // field scrolls after it. Clicks are counted here: the input module counts them only on release.
    //
    // Touch works as on a phone: a tap places the caret (beginning editing), a tap on the caret or inside the selection
    // shows the platform's edit menu, a double tap or a long press selects a word and shows it, and dragging after a long
    // press selects on a word at a time. Other drags are not the field's: they go to the nearest drag handler above it, so
    // the page under it still scrolls and swipes back (a multi-line field that has scrolled takes them first, and hands
    // on those it cannot take). A selection made by touch has handles at its ends (TextFieldHandle), dragged to move them.
    public sealed partial class TextField
    {
        private enum Granularity
        {
            Character,
            Word,
            Paragraph,
        }

        // Presses this close in time, and this close together (in the text's units: a few pixels for a mouse, about a
        // fingertip for touch), count as one more click or tap.
        private const float MultiClickTime = 0.5f;
        private const float MultiClickDistance = 4f;
        private const float MultiTapDistance = 24f;

        // How long a touch held still takes to select a word, as on Android and in iOS's text views.
        private const float LongPressTime = 0.5f;

        private int _clicks;
        private float _clickTime = float.NegativeInfinity;
        private Vector2 _clickPoint;
        private float _tapTime = float.NegativeInfinity;
        private Vector2 _tapPoint;

        // A touch held on the field, until it lets go, drags, or something else takes it.
        private PointerEventData _press;
        private float _pressTime;
        private bool _longPressed;

        // A mouse press, or a touch after a long press, selecting as it drags: by what (characters, words, paragraphs),
        // from what was first taken, and where the pointer is, for carrying on while it is held outside the viewport.
        private bool _mouseSelecting;
        private bool _touchSelecting;
        private Granularity _granularity;
        private TextRange _anchor;
        private Vector2 _pointer;
        private Camera _pointerCamera;

        // The last selection was made by touch: it has handles.
        private bool _touchMode;

        private bool _menuShown;

        // The wheel is being handed to the viewport, which hands back what it does not take.
        private bool _wheeling;

        // Where a dragged handle was taken hold of, from the selection end it stands for, so it does not jump to the pointer.
        private Vector2 _grab;

        private static bool IsTouch(BaseEventData eventData) =>
            eventData is ExtendedPointerEventData pointer && pointer.pointerType == UIPointerType.Touch;

        public override void OnPointerDown(PointerEventData eventData)
        {
            base.OnPointerDown(eventData);
            if (eventData.button != PointerEventData.InputButton.Left || !IsActive() || !IsInteractable() || _text == null) return;
            if (IsTouch(eventData))
            {
                // Acted on as it lets go (a tap) or once it has been held (a long press), and dropped if it drags.
                _press = eventData;
                _pressTime = Time.unscaledTime;
                _longPressed = false;
                Hook();
                return;
            }
            PressMouse(eventData);
        }

        public override void OnPointerUp(PointerEventData eventData)
        {
            base.OnPointerUp(eventData);
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (!IsTouch(eventData))
            {
                _mouseSelecting = false;
                return;
            }
            if (eventData != _press) return;
            // Not a tap once it has dragged (handed on to the page, or to nothing, which lets the field go as it sets off)
            // or stopped something gliding under it. A drag takes the edit menu away, as the page scrolls the field.
            bool tap = !_longPressed && eventData.eligibleForClick && !eventData.dragging;
            bool selected = _longPressed;
            _press = null;
            _longPressed = false;
            _touchSelecting = false;
            if (selected)
                ShowMenu();
            else if (tap)
                Tap(eventData);
            else if (eventData.dragging)
                HideMenu();
        }

        void IInitializePotentialDragHandler.OnInitializePotentialDrag(PointerEventData eventData)
        {
            if (!IsTouch(eventData))
            {
                // A mouse selects from the first pixel it moves.
                eventData.useDragThreshold = false;
                return;
            }
            // A touch's drags are not the field's: they go to what scrolls, as if the field were not there. With nothing
            // to take them, the field keeps the drag, so a finger that moves past the drag threshold still stops being a
            // tap or a long press: setting off, the drag is handed on to nothing (OnBeginDrag) and the press let go of.
            var target = TouchDragTarget();
            if (target == null) return;
            eventData.pointerDrag = target;
            ExecuteEvents.Execute(target, eventData, ExecuteEvents.initializePotentialDrag);
        }

        void IBeginDragHandler.OnBeginDrag(PointerEventData eventData)
        {
            if (!IsTouch(eventData) || _touchSelecting) return;
            // A drag the viewport handed back, not taking it the way it set off, or one the field kept for want of anything
            // to take it: on to what is outside the field, if anything is.
            HideMenu();
            var outer = OuterDragHandler();
            eventData.pointerDrag = outer;
            if (outer != null)
                ExecuteEvents.Execute(outer, eventData, ExecuteEvents.beginDragHandler);
        }

        void IDragHandler.OnDrag(PointerEventData eventData)
        {
            if (!(IsTouch(eventData) ? _touchSelecting : _mouseSelecting)) return;
            _pointer = eventData.position;
            _pointerCamera = eventData.pressEventCamera;
            DragSelectTo(_pointer, _pointerCamera);
        }

        // The wheel over a multi-line field scrolls its text first (the viewport is inside the field, so the wheel would
        // never reach it); what the text does not take, coming back here, and the wheel over a single line go on to what
        // is above the field.
        void IScrollHandler.OnScroll(PointerEventData eventData)
        {
            if (!_wheeling && Multiline && _viewport != null)
            {
                var inner = ExecuteEvents.GetEventHandler<IScrollHandler>(_viewport.gameObject);
                if (inner != null && inner != gameObject)
                {
                    _wheeling = true;
                    ExecuteEvents.Execute(inner, eventData, ExecuteEvents.scrollHandler);
                    _wheeling = false;
                    return;
                }
            }
            var parent = transform.parent;
            if (parent != null)
                ExecuteEvents.ExecuteHierarchy(parent.gameObject, eventData, ExecuteEvents.scrollHandler);
        }

        // A multi-line field that has scrolled takes a touch's drags up and down itself; anything else goes to the nearest
        // drag handler above the field.
        private GameObject TouchDragTarget()
        {
            if (Multiline && _viewport != null && _viewport.ScrollRange.y > 0f)
            {
                var inner = ExecuteEvents.GetEventHandler<IDragHandler>(_viewport.gameObject);
                if (inner != null && inner != gameObject)
                    return inner;
            }
            return OuterDragHandler();
        }

        private GameObject OuterDragHandler()
        {
            var parent = transform.parent;
            return parent != null ? ExecuteEvents.GetEventHandler<IDragHandler>(parent.gameObject) : null;
        }

        private void LetGoOfPointer()
        {
            _press = null;
            _longPressed = false;
            _mouseSelecting = false;
            _touchSelecting = false;
        }

        // ── Mouse ────────────────────────────────────────────────────────────────

        private void PressMouse(PointerEventData eventData)
        {
            var camera = eventData.pressEventCamera;
            if (!ToTextLocal(eventData.position, camera, out var local)) return;
            float now = Time.unscaledTime;
            bool again = now - _clickTime <= MultiClickTime && (local - _clickPoint).sqrMagnitude <= MultiClickDistance * MultiClickDistance;
            _clicks = again ? Mathf.Min(_clicks + 1, 3) : 1;
            _clickTime = now;
            _clickPoint = local;
            _touchMode = false;
            _mouseSelecting = true;
            _pointer = eventData.position;
            _pointerCamera = camera;

            int index = IndexAt(local, out bool upstream);
            var keyboard = Keyboard.current;
            if (_clicks == 1 && _editing && keyboard != null && keyboard.shiftKey.isPressed)
            {
                // Shift extends the selection to the press, from where it started.
                var from = _value.Selection.Base;
                _granularity = Granularity.Character;
                _anchor = TextRange.Collapsed(from);
                SetSelection(new TextSelection(from, index), upstream);
            }
            else
            {
                _granularity = _clicks == 1 ? Granularity.Character : _clicks == 2 ? Granularity.Word : Granularity.Paragraph;
                _anchor = Reach(index);
                SetSelection(new TextSelection(_anchor.Start, _anchor.End), upstream && _anchor.End == index);
            }

            if (!_editing)
            {
                _placed = true;
                BeginEditing();
            }
        }

        // The selection from the far end of what was first taken (a word, a paragraph) to the far end of what the pointer
        // is over, the way it went.
        private void DragSelectTo(Vector2 screen, Camera camera)
        {
            if (_text == null || !ToTextLocal(screen, camera, out var local)) return;
            int index = IndexAt(local, out bool upstream);
            var reach = Reach(index);
            var selection = reach.Start < _anchor.Start
                ? new TextSelection(_anchor.End, reach.Start)
                : new TextSelection(_anchor.Start, Mathf.Max(reach.End, _anchor.End));
            SetSelection(selection, upstream && selection.Extent == index);
        }

        // What a press at `index` takes, by the granularity selecting goes by. A secure field has no words to show, so a
        // word is the whole text.
        private TextRange Reach(int index)
        {
            var text = _value.Text;
            switch (_granularity)
            {
                case Granularity.Word:
                    return _config.IsSecure ? new TextRange(0, text.Length) : TextBoundaries.WordAt(text, index);
                case Granularity.Paragraph:
                    return TextBoundaries.ParagraphAt(text, index);
                default:
                    return TextRange.Collapsed(index);
            }
        }

        // ── Touch ────────────────────────────────────────────────────────────────

        private void Tap(PointerEventData eventData)
        {
            if (!IsActive() || !IsInteractable() || !ToTextLocal(eventData.position, eventData.pressEventCamera, out var local)) return;
            _touchMode = true;
            float now = Time.unscaledTime;
            bool second = _editing && now - _tapTime <= MultiClickTime && (local - _tapPoint).sqrMagnitude <= MultiTapDistance * MultiTapDistance;
            // A double tap starts over: a third tap is a first again.
            _tapTime = second ? float.NegativeInfinity : now;
            _tapPoint = local;
            int index = IndexAt(local, out bool upstream);

            if (second)
            {
                SelectWordAt(index);
                ShowMenu();
                return;
            }
            if (!_editing)
            {
                SetSelection(TextSelection.Collapsed(index), upstream);
                _placed = true;
                BeginEditing();
                return;
            }
            var selection = _value.Selection;
            // On the caret: at its index, and on the same side of a wrap there, if a line wraps there. On a selection: on
            // what is highlighted, level with the line the tap is on, as iOS has it, so a tap on any letter selected counts
            // (in text that reads both ways, the far half of a right-to-left letter is at the index after it).
            bool onSelection = selection.IsCollapsed
                ? index == selection.Extent && (upstream == _upstream || !WrapsAt(index))
                : OnHighlight(new Vector2(local.x, _text.GetCaretRect(index, upstream).center.y));
            if (!onSelection)
                SetSelection(TextSelection.Collapsed(index), upstream);
            else if (_menuShown)
                HideMenu();
            else
                ShowMenu();
        }

        // A touch held still long enough selects the word under it, beginning editing, and the drag that follows is the
        // field's: it selects on a word at a time, rather than scrolling the page. Checked once a frame while it is held.
        private void CheckLongPress()
        {
            var press = _press;
            if (press == null || _longPressed || press.dragging || !press.eligibleForClick) return;
            if (Time.unscaledTime - _pressTime < LongPressTime) return;
            if (!IsActive() || !IsInteractable() || !ToTextLocal(press.position, press.pressEventCamera, out var local)) return;
            _longPressed = true;
            _touchSelecting = true;
            _touchMode = true;
            press.pointerDrag = gameObject;
            _pointer = press.position;
            _pointerCamera = press.pressEventCamera;
            var word = SelectWordAt(IndexAt(local));
            _granularity = Granularity.Word;
            _anchor = word;
            if (!_editing)
            {
                _placed = true;
                BeginEditing();
            }
        }

        private TextRange SelectWordAt(int index)
        {
            var text = _value.Text;
            var word = _config.IsSecure ? new TextRange(0, text.Length) : TextBoundaries.WordAt(text, index);
            SetSelection(new TextSelection(word.Start, word.End));
            return word;
        }

        // Whether a point in the text's space is on one of the rects the selection is highlighted with.
        private bool OnHighlight(Vector2 local)
        {
            var selection = _value.Selection;
            _text.GetCharacterRects(selection.Start, selection.End, s_rects);
            for (int i = 0; i < s_rects.Count; i++)
            {
                if (s_rects[i].Contains(local))
                    return true;
            }
            return false;
        }

        // ── Handles ──────────────────────────────────────────────────────────────

        internal void OnHandlePressed(TextFieldHandle handle, PointerEventData eventData)
        {
            HideMenu();
            _touchMode = true;
            _grab = Vector2.zero;
            if (_text == null || !ToTextLocal(eventData.position, eventData.pressEventCamera, out var local)) return;
            _text.EnsureLayout();
            _grab = local - HandleCaret(handle.IsStart).center;
        }

        // The end the handle stands for follows the pointer, to the edge nearest it of the kind the handle stands at (see
        // HandleCaret), on a whole character; the two ends never cross, a character staying between them.
        internal void OnHandleDragged(TextFieldHandle handle, PointerEventData eventData)
        {
            if (_text == null || !ToTextLocal(eventData.position, eventData.pressEventCamera, out var local)) return;
            _text.EnsureLayout();
            int index = Snap(_text.GetIndexAtEdge(local - _grab, handle.IsStart, out bool upstream));
            var text = _value.Text;
            var selection = _value.Selection;
            // The end being dragged is the extent, so the field scrolls after it.
            var dragged = handle.IsStart
                ? new TextSelection(selection.End, Mathf.Min(index, TextBoundaries.PreviousCaretStop(text, selection.End)))
                : new TextSelection(selection.Start, Mathf.Max(index, TextBoundaries.NextCaretStop(text, selection.Start)));
            SetSelection(dragged, upstream && dragged.Extent == index);
        }

        // Where a handle stands, in the text's space: beside the characters selected, as Android stands its handles and
        // iOS its selection's ends, the start handle at the leading edge of the first and the end handle at the trailing
        // edge of the last, each in that character's own direction, so each stays beside the text it selects however the
        // text around it reads, and on the line that character is on where a line wraps. The last character is the last
        // one shown: a secure field shows a bullet for each UTF-16 unit.
        private Rect HandleCaret(bool isStart)
        {
            var selection = _value.Selection;
            return isStart
                ? _text.GetCharacterEdgeRect(selection.Start, leading: true)
                : _text.GetCharacterEdgeRect(TextBoundaries.PreviousCaretStop(_text.Text, selection.End), leading: false);
        }

        internal void OnHandleReleased(TextFieldHandle handle) => ShowMenu();

        // ── The edit menu ────────────────────────────────────────────────────────

        // The platform's edit menu, over the selection (or by the caret), offering what can be done with it: cut and copy
        // with something selected (not in a secure field), paste (the platform greys it out with nothing to paste), and
        // select all unless all is selected. Only where the platform has one: on a desktop, the shortcuts do it.
        private void ShowMenu()
        {
            if (!_editing || _text == null || !TextInputSystem.SupportsEditMenu) return;
            var selection = _value.Selection;
            var actions = TextEditActions.Paste;
            if (!selection.IsCollapsed && !_config.IsSecure)
                actions |= TextEditActions.Cut | TextEditActions.Copy;
            if (selection.Range.Length < _value.Text.Length)
                actions |= TextEditActions.SelectAll;
            _text.EnsureLayout();
            var target = selection.IsCollapsed || !TryBounds(selection.Range, out var bounds) ? CaretLocal(selection.Extent) : bounds;
            TextInputSystem.ShowEditMenu(this, ScreenRect(_text.rectTransform, target, CanvasCamera()), actions);
            _menuShown = true;
        }

        private void HideMenu()
        {
            if (!_menuShown) return;
            _menuShown = false;
            TextInputSystem.HideEditMenu();
        }

        // ── Where the pointer is ─────────────────────────────────────────────────

        private bool ToTextLocal(Vector2 screen, Camera camera, out Vector2 local) =>
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_text.rectTransform, screen, camera, out local);

        // The caret position nearest a point in the text's space, on a whole character, and whether the caret takes it
        // upstream: the point is past the end of a wrapped line, whose end is where the next line starts.
        private int IndexAt(Vector2 local) => IndexAt(local, out _);

        private int IndexAt(Vector2 local, out bool upstream)
        {
            _text.EnsureLayout();
            return Snap(_text.GetIndexAt(local, out upstream));
        }

        private int Snap(int index)
        {
            var text = _value.Text;
            return TextBoundaries.SnapToCaretStop(text, Mathf.Clamp(index, 0, text.Length));
        }
    }
}
