using System;
using TimboJimbo.UI.Layout;
using TimboJimbo.UI.Text;
using UnityEngine;
using UnityEngine.UI;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>Makes a <see cref="TextField"/> and the nodes it edits in, for demos and UI built in code.</summary>
    public static class TextFieldBuilder
    {
        /// <summary>
        /// Makes a text field under <paramref name="parent"/> and returns it, set up (<see cref="TextField.Setup"/>). The
        /// field's node is <paramref name="width"/> wide (growing to fill its parent's row by default) and fits its
        /// height, padded by its font size, with a rounded box behind it, faintly tinted and edged with
        /// <paramref name="textColour"/> so it reads on light and dark alike; its corners are as round as a single line's
        /// height allows, a pill at one line. Inside is the viewport, which clips the text and scrolls it, sideways for a
        /// single line and up and down past <paramref name="maxLines"/> for a multi-line field (as
        /// <paramref name="config"/> says), with the text in it (plain, in <paramref name="textColour"/> at
        /// <paramref name="fontSize"/>); over the viewport floats <paramref name="placeholder"/>, one line ending in an
        /// ellipsis, in <paramref name="placeholderColour"/>, shown while the field is empty. Recolour or reshape the box
        /// afterwards through the field's <see cref="Box"/>.
        /// </summary>
        public static TextField Create(LayoutNode parent, string name, TextInputConfig config, string placeholder,
            float fontSize, Color textColour, Color placeholderColour, Color caretColour, Color selectionColour,
            int maxLines = 1, Sizing? width = null)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            bool multiline = config.Multiline;

            var node = NewNode(parent.transform, name);
            node.Width = width ?? Sizing.Grow();
            node.Height = Sizing.Fit();
            node.Direction = LayoutDirection.TopToBottom;
            node.Padding = Insets.Symmetric(Mathf.Round(fontSize * 0.75f), Mathf.Round(fontSize * 0.5f));

            var viewport = NewNode(node.transform, "Viewport");
            viewport.Width = Sizing.Grow();
            viewport.Direction = multiline ? LayoutDirection.TopToBottom : LayoutDirection.LeftToRight;
            // A text view shows where it is scrolled to; a single line does not.
            viewport.ShowsScrollIndicators = multiline;

            var text = NewText(NewNode(viewport.transform, "Text"), string.Empty, fontSize, textColour);
            // What people type reads the way its first letter does, and starts at that side, as in a text view on iOS or
            // Android: Arabic at the right.
            text.Direction = TextBlockDirection.Auto;
            text.NaturalAlignment = true;

            var hintNode = NewNode(node.transform, "Placeholder");
            hintNode.Width = Sizing.Grow();
            hintNode.Height = Sizing.Fit();
            hintNode.Floating = new Floating
            {
                AttachTo = FloatingAttach.Element,
                Element = viewport,
                Point = AttachPoint.LeftTop,
                TargetPoint = AttachPoint.LeftTop,
            };
            var hint = NewText(hintNode, placeholder, fontSize, placeholderColour);
            // It too reads the way its first letter does, and starts at that side, as Android's hint and iOS's placeholder
            // do: a localised Arabic one at the right.
            hint.Direction = TextBlockDirection.Auto;
            hint.NaturalAlignment = true;
            // One line, as wide as the viewport, however long the placeholder.
            hint.WordWrap = true;
            hint.BreakWordsAnywhere = true;
            hint.MaxLines = 1;
            hint.Overflow = TextBlockOverflow.Ellipsis;

            // The box first: the field takes it as the graphic it is pressed through.
            var box = node.gameObject.AddComponent<Box>();
            box.raycastTarget = true;
            box.color = Color.white;
            box.Fill = new BoxLayer(new Color(textColour.r, textColour.g, textColour.b, 0.06f));
            box.Border = new BoxLayer(new Color(textColour.r, textColour.g, textColour.b, 0.14f)) { Stroke = 1f };

            var field = node.gameObject.AddComponent<TextField>();
            field.targetGraphic = box;
            field.transition = Selectable.Transition.None;
            field.Setup(text, hint, viewport, config, maxLines, caretColour, selectionColour);
            box.SetCornerRadius((text.LineHeight + node.Padding.Vertical) * 0.5f);
            return field;
        }

        private static LayoutNode NewNode(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = parent.gameObject.layer };
            go.transform.SetParent(parent, false);
            return go.AddComponent<LayoutNode>();
        }

        // A text that is its node's content, plain and taking no pointer, fitting its node's height.
        private static TextBlock NewText(LayoutNode node, string value, float fontSize, Color colour)
        {
            node.Width = Sizing.Grow();
            node.Height = Sizing.Fit();
            var text = node.gameObject.AddComponent<TextBlock>();
            text.RichText = false;
            text.raycastTarget = false;
            text.FontSize = fontSize;
            text.color = colour;
            text.Text = value ?? string.Empty;
            return text;
        }
    }
}
