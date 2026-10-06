using System.Collections.Generic;
using TimboJimbo.UI.Layout;
using UnityEngine;

namespace TimboJimbo.UI.TextInput
{
    // What the field draws over and under its text, and where it is on screen.
    //
    // The caret, the selection and the composition's underline are plain RectTransforms with Boxes, not layout nodes:
    // they move with every key, and layout has nothing to say about them. They are in two containers beside the text, kept
    // on its rect each frame (so their space is the text's own): the highlights before it, drawn under the glyphs, and the
    // overlay after it, drawn over them, which holds the caret and the selection handles. They are placed once a frame,
    // just before canvases are drawn, in a handler added as editing first begins (after the layout system's own, which by
    // then has run its first frame), so they go where the text is drawn in that frame, laid out (TextBlock.EnsureLayout)
    // with the text it has now. The same pass keeps the caret in view, scrolling the viewport, and, as the software
    // keyboard rises, brings the field into view in the scroll container above it.
    public sealed partial class TextField
    {
        private const float CaretWidth = 2f;
        private const float SelectionRadius = 2f;

        // The caret is solid while things change, and after this long still blinks, on and off each half second.
        private const float BlinkDelay = 0.5f;
        private const float BlinkHalfPeriod = 0.5f;

        private static readonly List<Rect> s_rects = new();

        private bool _hooked;
        private RectTransform _highlights;
        private RectTransform _overlay;
        private Box _caret;
        private readonly List<Box> _selectionBoxes = new();
        private readonly List<Box> _underlineBoxes = new();
        private TextFieldHandle _startHandle;
        private TextFieldHandle _endHandle;
        private float _blinkFrom;

        // The caret is to be brought into view (it moved, or the text changed), and the field into view in the scroll
        // container above it (editing began, or the keyboard rose).
        private bool _reveal;
        private bool _bringIntoView;
        private float _keyboardHeight;

        private void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            Canvas.preWillRenderCanvases += LatePass;
        }

        private void Unhook()
        {
            if (!_hooked) return;
            _hooked = false;
            Canvas.preWillRenderCanvases -= LatePass;
        }

        // Once a frame, after layout: a touch held long enough selects; editing ends if the field has been hidden or made
        // uninteractable; a selection being dragged outside the viewport carries on, the field scrolling after it; the
        // caret and the field are brought into view as asked; what the field draws is placed; and the field takes back
        // the selection a touch took.
        private void LatePass()
        {
            if (!Application.isPlaying) return;
            CheckLongPress();
            if (!_editing) return;
            if (_text == null || !IsInteractable() || IsHidden())
            {
                EndEditing();
                return;
            }

            _text.EnsureLayout();
            if ((_mouseSelecting || _touchSelecting) && _viewport != null
                && !RectTransformUtility.RectangleContainsScreenPoint((RectTransform)_viewport.transform, _pointer, _pointerCamera))
                DragSelectTo(_pointer, _pointerCamera);
            if (_reveal)
            {
                _reveal = false;
                Reveal();
            }
            if (_bringIntoView)
            {
                _bringIntoView = false;
                BringIntoView();
            }
            Draw();
            KeepSelection();
        }

        // Whether a node it is in is hidden (on its way out included), which ends editing, as a browser blurs a field
        // that stops being shown.
        private bool IsHidden()
        {
            for (var t = transform; t != null; t = t.parent)
            {
                if (t.TryGetComponent(out LayoutNode node) && node.isActiveAndEnabled && node.Display != DisplayMode.Visible)
                    return true;
            }
            return false;
        }

        // ── Drawing ──────────────────────────────────────────────────────────────

        private void Draw()
        {
            EnsureGraphics();
            Match(_highlights);
            Match(_overlay);

            var value = _value;
            var selection = value.Selection;
            var composing = value.Composing;

            // The selection behind the text; inside a composition, the part selected is its thicker underline instead.
            int boxes = 0;
            if (!selection.IsCollapsed)
            {
                if (value.IsComposing)
                {
                    boxes = Highlight(selection.Start, Mathf.Min(selection.End, composing.Start), boxes);
                    boxes = Highlight(Mathf.Max(selection.Start, composing.End), selection.End, boxes);
                }
                else
                {
                    boxes = Highlight(selection.Start, selection.End, boxes);
                }
            }
            HideFrom(_selectionBoxes, boxes);

            // Text still being composed is underlined, thicker where a selection lies inside it (the clause a Japanese
            // keyboard is converting).
            int lines = 0;
            if (value.IsComposing)
            {
                float thickness = Mathf.Max(1f, _text.LineHeight * 0.05f);
                lines = Underline(composing.Start, composing.End, thickness, lines);
                int start = Mathf.Max(selection.Start, composing.Start), end = Mathf.Min(selection.End, composing.End);
                if (end > start)
                    lines = Underline(start, end, thickness * 2f, lines);
            }
            HideFrom(_underlineBoxes, lines);

            bool caret = selection.IsCollapsed;
            SetShown(_caret.gameObject, caret);
            if (caret)
            {
                _caret.color = _caretColour;
                Place(_caret.rectTransform, CaretLocal(selection.Extent));
                _caret.canvasRenderer.SetAlpha(BlinkOn() ? 1f : 0f);
            }

            bool handles = _touchMode && !selection.IsCollapsed && !value.IsComposing;
            if (handles && _startHandle == null)
            {
                _startHandle = TextFieldHandle.Create(this, _overlay, true, _caretColour);
                _endHandle = TextFieldHandle.Create(this, _overlay, false, _caretColour);
            }
            if (_startHandle != null)
            {
                SetShown(_startHandle.gameObject, handles);
                SetShown(_endHandle.gameObject, handles);
                if (handles)
                {
                    _startHandle.Place(HandleCaret(isStart: true), _caretColour);
                    _endHandle.Place(HandleCaret(isStart: false), _caretColour);
                }
            }
        }

        private bool BlinkOn()
        {
            float idle = Time.unscaledTime - _blinkFrom;
            return idle < BlinkDelay || Mathf.FloorToInt((idle - BlinkDelay) / BlinkHalfPeriod) % 2 == 1;
        }

        private int Highlight(int start, int end, int used)
        {
            if (end <= start) return used;
            _text.GetCharacterRects(start, end, s_rects);
            for (int i = 0; i < s_rects.Count; i++)
            {
                var box = Pooled(_selectionBoxes, used++, _highlights, "Selection", _selectionColour, SelectionRadius);
                Place(box.rectTransform, s_rects[i]);
            }
            return used;
        }

        private int Underline(int start, int end, float thickness, int used)
        {
            if (end <= start) return used;
            _text.GetCharacterRects(start, end, s_rects);
            for (int i = 0; i < s_rects.Count; i++)
            {
                var r = s_rects[i];
                var box = Pooled(_underlineBoxes, used++, _highlights, "Composition", _text.color, 0f);
                Place(box.rectTransform, new Rect(r.xMin, r.yMin, r.width, thickness));
            }
            return used;
        }

        // The drawn caret at `index`, in the text's space: CaretWidth wide, centred on the insertion point but kept inside
        // the text's rect, so the viewport never cuts it in half at either end; at the end of a wrapped line when it is the
        // caret standing upstream.
        private Rect CaretLocal(int index)
        {
            var caret = _text.GetCaretRect(index, UpstreamAt(index));
            var bounds = _text.rectTransform.rect;
            float x = Mathf.Clamp(caret.x - CaretWidth * 0.5f, bounds.xMin, Mathf.Max(bounds.xMin, bounds.xMax - CaretWidth));
            return new Rect(x, caret.y, CaretWidth, caret.height);
        }

        // The rect around a range's characters, in the text's space; false when none are laid out.
        private bool TryBounds(TextRange range, out Rect bounds)
        {
            bounds = default;
            if (!range.IsValid || range.IsEmpty) return false;
            _text.GetCharacterRects(range.Start, range.End, s_rects);
            if (s_rects.Count == 0) return false;
            bounds = s_rects[0];
            for (int i = 1; i < s_rects.Count; i++)
                bounds = Rect.MinMaxRect(Mathf.Min(bounds.xMin, s_rects[i].xMin), Mathf.Min(bounds.yMin, s_rects[i].yMin),
                    Mathf.Max(bounds.xMax, s_rects[i].xMax), Mathf.Max(bounds.yMax, s_rects[i].yMax));
            return true;
        }

        // ── The graphics ─────────────────────────────────────────────────────────

        private void EnsureGraphics()
        {
            var parent = _text.transform.parent;
            if (_highlights == null)
            {
                _highlights = NewRect(parent, "Text highlights");
                _highlights.SetSiblingIndex(_text.transform.GetSiblingIndex());
            }
            if (_overlay == null)
            {
                _overlay = NewRect(parent, "Text overlay");
                _overlay.SetSiblingIndex(_text.transform.GetSiblingIndex() + 1);
            }
            SetShown(_highlights.gameObject, true);
            SetShown(_overlay.gameObject, true);
            if (_caret == null)
                _caret = NewBox(_overlay, "Caret", _caretColour, CaretWidth * 0.5f);
        }

        private void HideGraphics()
        {
            if (_highlights != null) SetShown(_highlights.gameObject, false);
            if (_overlay != null) SetShown(_overlay.gameObject, false);
        }

        // The containers sit beside the text, not inside the field: one going with the field takes them with it.
        private void DestroyGraphics()
        {
            if (!Application.isPlaying) return;
            if (_highlights != null) Destroy(_highlights.gameObject);
            if (_overlay != null) Destroy(_overlay.gameObject);
        }

        // Puts a container on the text's rect: the same anchors, pivot, size, place, scale and turn, so its space is the
        // text's own, wherever layout, a scroll or a spring has the text.
        private void Match(RectTransform container)
        {
            var source = _text.rectTransform;
            if (container.anchorMin != source.anchorMin) container.anchorMin = source.anchorMin;
            if (container.anchorMax != source.anchorMax) container.anchorMax = source.anchorMax;
            if (container.pivot != source.pivot) container.pivot = source.pivot;
            if (container.sizeDelta != source.sizeDelta) container.sizeDelta = source.sizeDelta;
            if (container.anchoredPosition3D != source.anchoredPosition3D) container.anchoredPosition3D = source.anchoredPosition3D;
            if (container.localScale != source.localScale) container.localScale = source.localScale;
            if (container.localRotation != source.localRotation) container.localRotation = source.localRotation;
        }

        // Puts a child of a container on `rect`, given in the container's (the text's) space: anchored at the container's
        // pivot, where that space's origin is.
        internal static void Place(RectTransform child, Rect rect)
        {
            var origin = ((RectTransform)child.parent).pivot;
            if (child.anchorMin != origin) child.anchorMin = origin;
            if (child.anchorMax != origin) child.anchorMax = origin;
            if (child.pivot != Vector2.zero) child.pivot = Vector2.zero;
            if (child.anchoredPosition != rect.position) child.anchoredPosition = rect.position;
            if (child.sizeDelta != rect.size) child.sizeDelta = rect.size;
        }

        private Box Pooled(List<Box> pool, int index, RectTransform parent, string name, Color colour, float radius)
        {
            Box box;
            if (index < pool.Count)
            {
                box = pool[index];
            }
            else
            {
                box = NewBox(parent, name, colour, radius);
                pool.Add(box);
            }
            SetShown(box.gameObject, true);
            box.color = colour;
            return box;
        }

        private static void HideFrom(List<Box> pool, int count)
        {
            for (int i = count; i < pool.Count; i++)
                SetShown(pool[i].gameObject, false);
        }

        private static void SetShown(GameObject go, bool shown)
        {
            if (go.activeSelf != shown)
                go.SetActive(shown);
        }

        // Made in play mode only, and never saved.
        private RectTransform NewRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = gameObject.layer, hideFlags = HideFlags.DontSave };
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        internal Box NewBox(RectTransform parent, string name, Color colour, float radius)
        {
            var box = NewRect(parent, name).gameObject.AddComponent<Box>();
            box.raycastTarget = false;
            box.color = colour;
            box.SetCornerRadius(radius);
            return box;
        }

        // ── Keeping things in view ───────────────────────────────────────────────

        // Scrolls the viewport only as far as brings the caret (the end of the selection that moved) into view, as
        // UITextField and UITextView do. Laid out as drawn this frame, it is scrolled there from the next.
        private void Reveal()
        {
            if (_viewport == null || _viewport.Scroll == ScrollAxis.None) return;
            var view = (RectTransform)_viewport.transform;
            var caret = CaretLocal(_value.Selection.Extent);
            var space = _text.rectTransform;
            Vector2 min = view.InverseTransformPoint(space.TransformPoint(caret.min));
            Vector2 max = view.InverseTransformPoint(space.TransformPoint(caret.max));
            var bounds = view.rect;
            var offset = _viewport.ScrollOffset;
            var to = offset;
            // The offset runs x right and y down.
            if (max.x > bounds.xMax) to.x += max.x - bounds.xMax;
            else if (min.x < bounds.xMin) to.x -= bounds.xMin - min.x;
            if (max.y > bounds.yMax) to.y -= max.y - bounds.yMax;
            else if (min.y < bounds.yMin) to.y += bounds.yMin - min.y;
            if (to != offset)
                _viewport.ScrollOffset = to;
        }

        // Scrolls the nearest scroll container above the field to bring all of it into view, as the keyboard rises, each
        // frame it rises, as Flutter's EditableText does: laid out as drawn this frame, under this frame's keyboard. Outside
        // any change, so it follows the keyboard rather than chasing it on a spring.
        private void BringIntoView()
        {
            for (var t = transform.parent; t != null; t = t.parent)
            {
                if (!t.TryGetComponent(out LayoutNode scroller) || !scroller.isActiveAndEnabled || scroller.Scroll == ScrollAxis.None) continue;
                scroller.ScrollIntoView(Node);
                return;
            }
        }

        private void OnKeyboardChanged()
        {
            float height = TextInputSystem.KeyboardHeight;
            if (height > _keyboardHeight)
                _bringIntoView = true;
            _keyboardHeight = height;
        }

        // ── Where it is on screen ────────────────────────────────────────────────

        bool ITextInputClient.TryGetScreenGeometry(out Rect field, out Rect caret, out Rect composing)
        {
            field = caret = composing = default;
            if (_text == null || !isActiveAndEnabled || _text.canvas == null) return false;
            var own = (RectTransform)transform;
            var rect = own.rect;
            if (rect.width <= 0f || rect.height <= 0f) return false;
            var camera = CanvasCamera();
            _text.EnsureLayout();
            field = ScreenRect(own, rect, camera);
            int extent = _value.Selection.Extent;
            caret = ScreenRect(_text.rectTransform, _text.GetCaretRect(extent, UpstreamAt(extent)), camera);
            if (_value.IsComposing && TryBounds(_value.Composing, out var bounds))
                composing = ScreenRect(_text.rectTransform, bounds, camera);
            return true;
        }

        private Camera CanvasCamera()
        {
            var canvas = _text != null ? _text.canvas : null;
            if (canvas == null) return null;
            canvas = canvas.rootCanvas;
            return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        // A rect in `space` as it covers the screen: screen pixels, y up.
        private static Rect ScreenRect(RectTransform space, Rect local, Camera camera)
        {
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 4; i++)
            {
                var corner = new Vector3(i == 0 || i == 3 ? local.xMin : local.xMax, i < 2 ? local.yMin : local.yMax, 0f);
                var point = RectTransformUtility.WorldToScreenPoint(camera, space.TransformPoint(corner));
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }
    }
}
