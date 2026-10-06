using UnityEngine;
using UnityEngine.EventSystems;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// One of the two handles a <see cref="TextField"/> shows at the ends of a selection made by touch, as iOS's are: a
    /// bar the height of the line with a round knob past its end, above the start and below the end, dragged to move that
    /// end of the selection, the edit menu coming back as it is let go of. Made, placed and shown by its field, over its
    /// text; it takes the pointer over a finger-wide strip around the bar and the knob, from the first pixel it moves.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class TextFieldHandle : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IInitializePotentialDragHandler,
        IDragHandler
    {
        private const float BarWidth = 2f;
        private const float KnobSize = 10f;

        // How far past the bar and the knob it takes the pointer: a fingertip's width across, and some above and below.
        private const float ReachAcross = 22f;
        private const float ReachAlong = 8f;

        private TextField _field;
        private bool _isStart;
        private Box _bar;
        private Box _knob;

        /// <summary>Whether it stands for the start of the selection (its knob above), or the end (its knob below).</summary>
        internal bool IsStart => _isStart;

        // Made in play mode only, and never saved: a clear box taking the pointer, with the bar and the knob in it.
        internal static TextFieldHandle Create(TextField field, RectTransform parent, bool isStart, Color colour)
        {
            var hit = field.NewBox(parent, isStart ? "Selection start" : "Selection end", Color.clear, 0f);
            hit.raycastTarget = true;
            // A clear mesh still takes raycasts only while it is not culled.
            hit.canvasRenderer.cullTransparentMesh = false;
            var handle = hit.gameObject.AddComponent<TextFieldHandle>();
            handle._field = field;
            handle._isStart = isStart;
            handle._bar = field.NewBox(hit.rectTransform, "Bar", colour, BarWidth * 0.5f);
            handle._knob = field.NewBox(hit.rectTransform, "Knob", colour, KnobSize * 0.5f);
            return handle;
        }

        /// <summary>
        /// Stands it on <paramref name="caret"/>, a caret-shaped rect at the edge of the selection it stands for (beside
        /// the first or the last character selected), in the space of the container it is in (its text's), drawn in
        /// <paramref name="colour"/>.
        /// </summary>
        internal void Place(Rect caret, Color colour)
        {
            // The knob past the bar's top for the start, past its bottom for the end, overlapping the bar a little.
            float knobBottom = _isStart ? caret.yMax - 1f : caret.yMin - KnobSize + 1f;
            float bottom = (_isStart ? caret.yMin : caret.yMin - KnobSize) - ReachAlong;
            float height = caret.height + KnobSize + ReachAlong * 2f;

            TextField.Place((RectTransform)transform, new Rect(caret.x - ReachAcross, bottom, ReachAcross * 2f, height));
            // The bar and the knob are placed in its own space, whose origin is its pivot: its bottom-left corner.
            TextField.Place(_bar.rectTransform, new Rect(ReachAcross - BarWidth * 0.5f, caret.yMin - bottom, BarWidth, caret.height));
            TextField.Place(_knob.rectTransform, new Rect(ReachAcross - KnobSize * 0.5f, knobBottom - bottom, KnobSize, KnobSize));
            _bar.color = colour;
            _knob.color = colour;
        }

        void IInitializePotentialDragHandler.OnInitializePotentialDrag(PointerEventData eventData) => eventData.useDragThreshold = false;

        // Taking the press keeps it from the field, which would take it as a tap.
        void IPointerDownHandler.OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
                _field.OnHandlePressed(this, eventData);
        }

        void IDragHandler.OnDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
                _field.OnHandleDragged(this, eventData);
        }

        void IPointerUpHandler.OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
                _field.OnHandleReleased(this);
        }
    }
}
