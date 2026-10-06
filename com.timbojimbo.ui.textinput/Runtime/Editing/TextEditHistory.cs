using System;
using UnityEngine;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// Undo and redo for a text being edited, as text fields have them. Each step is the value before an edit and the value
    /// after it; undo puts back the one before, redo the one after.
    /// <para>
    /// Typing joins into one step, as it does in a native text field, so that undo takes back a run of typing rather than a
    /// letter. An edit that types one character (a grapheme cluster: an emoji counts as one) in place of the selection, or
    /// deletes one next to the caret (backspace, forward delete) or deletes the selection, starts a run; the next such
    /// edit joins it if it is of the same kind (typing or deleting) and made at the caret the run left. A run ends when
    /// the typing pauses for more than a second (of unscaled time), when the caret moves, when typing turns to deleting or
    /// back, when any other edit comes between, and when a space follows a word, so that a sentence undoes a word at a
    /// time.
    /// </para>
    /// <para>
    /// A composition (marked text, or the word a keyboard is still guessing at) is not recorded while it changes; the value
    /// it is committed to is, as one step from the value before it began. Undo during a composition takes back the
    /// composition first.
    /// </para>
    /// <para>
    /// The field records every change it makes or takes up, and applies what <see cref="TryUndo"/> and
    /// <see cref="TryRedo"/> give back without recording it. What they give back has no composition, so the keyboard is told
    /// of it as of a change made from code.
    /// </para>
    /// </summary>
    public sealed class TextEditHistory
    {
        // Typing that pauses longer than this, in seconds, starts a new step.
        private const float PauseTime = 1f;

        // What kind of typing an edit is, for joining it to a run of the same.
        private enum EditKind : byte
        {
            None,
            Insert,
            Delete,
        }

        private struct Step
        {
            public TextEditingValue Before;
            public TextEditingValue After;

            public Step(in TextEditingValue before, in TextEditingValue after)
            {
                Before = before;
                After = after;
            }
        }

        // The steps undo takes back, the oldest at _undoStart, wrapping round: when it is full, a new step pushes the
        // oldest out. The steps redo puts back, the next one last. Together they never hold more than the capacity, since
        // redo only takes what undo gave up and every new edit empties it.
        private readonly Step[] _undo;
        private int _undoStart;
        private int _undoCount;
        private readonly Step[] _redo;
        private int _redoCount;

        // The kind of typing the newest step is a run of, which the next edit may join (None when it is not a run, or
        // there is no newest step), and when it was last added to.
        private EditKind _runKind;
        private float _runTime;

        // While a composition is under way: the value before it began, and whether it has changed the text since.
        private bool _composing;
        private bool _composed;
        private TextEditingValue _beforeComposition;

        /// <summary>A history keeping up to <paramref name="capacity"/> steps to undo, dropping the oldest beyond that.</summary>
        public TextEditHistory(int capacity = 100)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "A history keeps at least one step.");
            _undo = new Step[capacity];
            _redo = new Step[capacity];
        }

        /// <summary>Whether there is an edit to undo (a composition under way counts).</summary>
        public bool CanUndo => _undoCount > 0 || _composed;

        /// <summary>Whether there is an undone edit to redo.</summary>
        public bool CanRedo => _redoCount > 0;

        /// <summary>
        /// Records an edit, from the value <paramref name="before"/> it to the value <paramref name="after"/> it. An edit
        /// that leaves the text as it was (the caret moved, a word underlined for composing) records nothing, nor does one
        /// that leaves a composition under way. <paramref name="coalesce"/>: the edit is typing, which may join the step
        /// before (see the class); false makes it a step of its own (a paste, a cut, text set from code), and the next
        /// edit starts another.
        /// </summary>
        public void Record(in TextEditingValue before, in TextEditingValue after, bool coalesce)
        {
            if (after.IsComposing)
            {
                if (!_composing)
                {
                    _composing = true;
                    _beforeComposition = before.CommitComposition();
                }
                _composed = !SameText(_beforeComposition, after);
                // What redo would put back was undone from a text the composition has now changed.
                if (_composed) ClearRedo();
                return;
            }

            if (_composing)
            {
                // The commit: one step from before the composition began.
                var from = _beforeComposition;
                EndComposition();
                Push(from, after, coalesce);
                return;
            }

            Push(before.CommitComposition(), after, coalesce);
        }

        /// <summary>
        /// Takes back the newest step (first a composition under way, if there is one): <paramref name="restored"/> is the
        /// value before it, for the field to apply. False, with <paramref name="restored"/> left as
        /// <paramref name="current"/>, when there is nothing to undo.
        /// </summary>
        public bool TryUndo(in TextEditingValue current, out TextEditingValue restored)
        {
            if (_composing)
            {
                var from = _beforeComposition;
                EndComposition();
                Push(from, current.CommitComposition(), false);
            }

            _runKind = EditKind.None;
            if (_undoCount == 0)
            {
                restored = current;
                return false;
            }

            int newest = (_undoStart + _undoCount - 1) % _undo.Length;
            var step = _undo[newest];
            _undo[newest] = default;
            _undoCount--;
            _redo[_redoCount++] = step;
            restored = step.Before;
            return true;
        }

        /// <summary>
        /// Puts back the step undone last: <paramref name="restored"/> is the value after it, for the field to apply. False,
        /// with <paramref name="restored"/> left as <paramref name="current"/>, when there is nothing to redo.
        /// </summary>
        public bool TryRedo(in TextEditingValue current, out TextEditingValue restored)
        {
            if (_redoCount == 0)
            {
                restored = current;
                return false;
            }

            // Redo is there only while the text is as undo left it, so a composition under way merely underlines part of
            // it, and ends with it.
            EndComposition();
            var step = _redo[--_redoCount];
            _redo[_redoCount] = default;
            AddUndo(step);
            _runKind = EditKind.None;
            restored = step.After;
            return true;
        }

        /// <summary>Forgets every step, as when the field is given a new text it should not undo back from.</summary>
        public void Clear()
        {
            Array.Clear(_undo, 0, _undo.Length);
            _undoStart = 0;
            _undoCount = 0;
            ClearRedo();
            _runKind = EditKind.None;
            EndComposition();
        }

        private ref Step Newest => ref _undo[(_undoStart + _undoCount - 1) % _undo.Length];

        private void Push(in TextEditingValue before, in TextEditingValue after, bool coalesce)
        {
            if (SameText(before, after)) return;

            float now = Time.unscaledTime;
            var kind = coalesce ? KindOf(before, after) : EditKind.None;
            if (kind != EditKind.None && kind == _runKind && now - _runTime <= PauseTime && Newest.After == before &&
                !(kind == EditKind.Insert && TypesSpaceAfterWord(before, after)))
                Newest.After = after;
            else
                AddUndo(new Step(before, after));

            _runKind = kind;
            _runTime = now;
            ClearRedo();
        }

        private void AddUndo(in Step step)
        {
            if (_undoCount == _undo.Length)
            {
                _undo[_undoStart] = default;
                _undoStart = (_undoStart + 1) % _undo.Length;
                _undoCount--;
            }
            _undo[(_undoStart + _undoCount) % _undo.Length] = step;
            _undoCount++;
        }

        private void ClearRedo()
        {
            Array.Clear(_redo, 0, _redoCount);
            _redoCount = 0;
        }

        private void EndComposition()
        {
            _composing = false;
            _composed = false;
            _beforeComposition = default;
        }

        private static bool SameText(in TextEditingValue a, in TextEditingValue b) =>
            string.Equals(a.Text, b.Text, StringComparison.Ordinal);

        // What kind of typing an edit is: one grapheme cluster typed in place of the selection (Insert); one cluster, or
        // a part of one (a Thai vowel mark), deleted either side of the caret, or the selection deleted (Delete); or
        // anything else (None).
        private static EditKind KindOf(in TextEditingValue before, in TextEditingValue after)
        {
            if (!after.Selection.IsCollapsed) return EditKind.None;
            string was = before.Text, now = after.Text;
            var selected = before.Selection.Range;
            int caret = after.Selection.Extent;
            int kept = was.Length - selected.Length;

            if (now.Length > kept)
            {
                // Typed: the selection replaced by what now stands before the caret, which holds no cluster boundary.
                int end = selected.Start + now.Length - kept;
                return caret == end && Unchanged(was, now, selected.Start, selected.End, end) &&
                       TextBoundaries.NextCaretStop(now, selected.Start) >= end
                    ? EditKind.Insert
                    : EditKind.None;
            }

            if (now.Length == kept)
            {
                return !selected.IsEmpty && caret == selected.Start && Unchanged(was, now, selected.Start, selected.End, selected.Start)
                    ? EditKind.Delete
                    : EditKind.None;
            }

            // Deleted next to a caret, from where the caret is now: forward delete leaves it where it was, backspace moves
            // it back over what it deleted, which holds no cluster boundary.
            int removed = was.Length - now.Length;
            if (!selected.IsEmpty || (caret != selected.Start && caret != selected.Start - removed)) return EditKind.None;
            return Unchanged(was, now, caret, caret + removed, caret) && TextBoundaries.NextCaretStop(was, caret) >= caret + removed
                ? EditKind.Delete
                : EditKind.None;
        }

        // Whether `was` and `now` agree before `start`, and from `wasEnd` and `nowEnd` on respectively, to their ends.
        private static bool Unchanged(string was, string now, int start, int wasEnd, int nowEnd) =>
            string.CompareOrdinal(was, 0, now, 0, start) == 0 &&
            string.CompareOrdinal(was, wasEnd, now, nowEnd, was.Length - wasEnd) == 0;

        // Whether an insertion is a space (or a line break) typed after a word, which starts a new step.
        private static bool TypesSpaceAfterWord(in TextEditingValue before, in TextEditingValue after)
        {
            int at = before.Selection.Start;
            string now = after.Text;
            return at > 0 && char.IsWhiteSpace(now[at]) && !char.IsWhiteSpace(now[at - 1]);
        }
    }
}
