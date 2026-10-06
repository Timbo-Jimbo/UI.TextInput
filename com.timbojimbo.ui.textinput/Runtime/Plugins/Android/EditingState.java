package com.timbojimbo.textinput;

import android.icu.text.BreakIterator;
import android.text.Editable;
import android.text.Selection;
import android.text.SpannableStringBuilder;
import android.view.View;
import android.view.inputmethod.BaseInputConnection;

/**
 * The mirror of the field's value that the keyboard reads and edits: its text, its selection (Android's Selection
 * spans, the anchor as base and the moving end as extent, as C#'s TextSelection) and its composing range (the
 * composing span), as Flutter's ListenableEditingState. Unity's side owns the value; this copy answers the keyboard's
 * questions at once, on the UI thread, while C# hears of each change a frame later.
 *
 * <p>Edits come in batch edits, which nest; as the outermost ends, the listener hears whether anything changed in it.
 * A value pushed from Unity goes in through {@link #apply} instead, outside any batch, and is never reported back.</p>
 */
final class EditingState extends SpannableStringBuilder
{
    /** Hears as each outermost batch edit ends. */
    interface Listener
    {
        void onBatchEnded(EditingState state, boolean changed);
    }

    private final Listener mListener;
    // Sets composing regions, as only BaseInputConnection can (its composing span is private to it).
    private final BaseInputConnection mComposer;

    private int mBatchDepth;
    // Rises with each change to the text (not to its spans), so a batch tells whether it changed it without a copy.
    private int mTextVersion;
    private int mVersionAtBegin;
    private int mBaseAtBegin;
    private int mExtentAtBegin;
    private int mComposingStartAtBegin;
    private int mComposingEndAtBegin;

    EditingState(View view, Listener listener)
    {
        mListener = listener;
        final EditingState self = this;
        mComposer = new BaseInputConnection(view, true)
        {
            @Override
            public Editable getEditable()
            {
                return self;
            }
        };
        Selection.setSelection(this, 0);
    }

    @Override
    public SpannableStringBuilder replace(int start, int end, CharSequence text, int textStart, int textEnd)
    {
        if (changesText(start, end, text, textStart, textEnd)) mTextVersion++;
        return super.replace(start, end, text, textStart, textEnd);
    }

    private boolean changesText(int start, int end, CharSequence text, int textStart, int textEnd)
    {
        if (end - start != textEnd - textStart) return true;
        for (int i = 0; i < end - start; i++)
        {
            if (charAt(start + i) != text.charAt(textStart + i)) return true;
        }
        return false;
    }

    // ── Batch edits ─────────────────────────────────────────────────────────

    void beginBatchEdit()
    {
        if (mBatchDepth++ > 0) return;
        mVersionAtBegin = mTextVersion;
        mBaseAtBegin = selectionBase();
        mExtentAtBegin = selectionExtent();
        mComposingStartAtBegin = composingStart();
        mComposingEndAtBegin = composingEnd();
    }

    void endBatchEdit()
    {
        if (--mBatchDepth > 0) return;
        boolean changed = mTextVersion != mVersionAtBegin || selectionBase() != mBaseAtBegin
            || selectionExtent() != mExtentAtBegin || composingStart() != mComposingStartAtBegin
            || composingEnd() != mComposingEndAtBegin;
        mListener.onBatchEnded(this, changed);
    }

    boolean inBatchEdit()
    {
        return mBatchDepth > 0;
    }

    /** What {@link #apply} changed: nothing. */
    static final int APPLIED_NOTHING = 0;
    /** What {@link #apply} changed: the selection or the composing range, and perhaps the text. */
    static final int APPLIED_MOVED = 1;
    /**
     * What {@link #apply} changed: the text alone, the selection and composing range where they were, which
     * {@code updateSelection} cannot tell the keyboard (it compares those offsets alone).
     */
    static final int APPLIED_TEXT_ONLY = 2;

    /**
     * Puts in a value pushed from Unity, outside any batch edit and without telling the listener: only the part of the
     * text that changed is replaced, so the keyboard's spans on the rest (its suggestions) survive. Returns what changed
     * (APPLIED_NOTHING, APPLIED_MOVED or APPLIED_TEXT_ONLY).
     */
    int apply(String text, int base, int extent, int composingStart, int composingEnd)
    {
        int version = mTextVersion;
        int oldBase = selectionBase();
        int oldExtent = selectionExtent();
        int oldComposingStart = composingStart();
        int oldComposingEnd = composingEnd();

        String old = toString();
        if (!old.equals(text))
        {
            int shorter = Math.min(old.length(), text.length());
            int prefix = 0;
            while (prefix < shorter && old.charAt(prefix) == text.charAt(prefix)) prefix++;
            int suffix = 0;
            while (suffix < shorter - prefix
                && old.charAt(old.length() - 1 - suffix) == text.charAt(text.length() - 1 - suffix))
                suffix++;
            replace(prefix, old.length() - suffix, text, prefix, text.length() - suffix);
        }

        int length = length();
        Selection.setSelection(this, clamp(base, length), clamp(extent, length));
        if (composingStart >= 0 && composingEnd > composingStart && composingEnd <= length)
            mComposer.setComposingRegion(composingStart, composingEnd);
        else
            BaseInputConnection.removeComposingSpans(this);

        if (selectionBase() != oldBase || selectionExtent() != oldExtent || composingStart() != oldComposingStart
            || composingEnd() != oldComposingEnd)
            return APPLIED_MOVED;
        return mTextVersion != version ? APPLIED_TEXT_ONLY : APPLIED_NOTHING;
    }

    private static int clamp(int index, int length)
    {
        return Math.max(0, Math.min(index, length));
    }

    // ── What it holds ───────────────────────────────────────────────────────

    /** Where the selection was started (Android's selection start). */
    int selectionBase()
    {
        return Selection.getSelectionStart(this);
    }

    /** The end of the selection that moves, the caret (Android's selection end). */
    int selectionExtent()
    {
        return Selection.getSelectionEnd(this);
    }

    int selectionMin()
    {
        return Math.max(0, Math.min(selectionBase(), selectionExtent()));
    }

    int selectionMax()
    {
        return Math.max(0, Math.max(selectionBase(), selectionExtent()));
    }

    /** Where the composing range starts, or -1 with none. */
    int composingStart()
    {
        return Math.min(BaseInputConnection.getComposingSpanStart(this), BaseInputConnection.getComposingSpanEnd(this));
    }

    /** Where the composing range ends, or -1 with none. */
    int composingEnd()
    {
        return Math.max(BaseInputConnection.getComposingSpanStart(this), BaseInputConnection.getComposingSpanEnd(this));
    }

    String selectedText()
    {
        return toString().substring(selectionMin(), selectionMax());
    }

    // ── Edits of its own (keys the keyboard sends, the edit menu); callers wrap them in a batch edit ──

    /** The composition is committed: the text stays, no longer composing. */
    void finishComposing()
    {
        BaseInputConnection.removeComposingSpans(this);
    }

    /** Typing or pasting {@code text}: it replaces the selection, the composition committed first; the caret after it. */
    void replaceSelection(CharSequence text)
    {
        finishComposing();
        int start = selectionMin();
        replace(start, selectionMax(), text);
        Selection.setSelection(this, start + text.length());
    }

    void selectAll()
    {
        Selection.setSelection(this, 0, length());
    }

    /**
     * Backspace, as the field's own (TextBoundaries.DeleteBackwardStart): the selection if there is one; else the whole
     * character before the caret if it is an emoji (a flag, a keycap, a skin tone or a ZWJ sequence), and otherwise only
     * its last code point, so that an accent or a Thai or Indic vowel sign goes on its own, as UIKit and Flutter do.
     */
    void deleteBackward()
    {
        int start = selectionMin();
        int end = selectionMax();
        if (start == end)
        {
            if (start == 0) return;
            int cluster = previousCluster(start);
            start = isEmoji(cluster, start) ? cluster : start - Character.charCount(Character.codePointBefore(this, start));
        }
        delete(start, end);
        Selection.setSelection(this, start);
    }

    /** Forward delete: the selection if there is one, else the whole character after the caret. */
    void deleteForward()
    {
        int start = selectionMin();
        int end = selectionMax();
        if (start == end)
        {
            if (end == length()) return;
            end = nextCluster(end);
        }
        delete(start, end);
        Selection.setSelection(this, start);
    }

    // Grapheme clusters: what a user sees as one character.
    private int previousCluster(int index)
    {
        if (index <= 0) return 0;
        int previous = clusters().preceding(index);
        return previous == BreakIterator.DONE ? 0 : previous;
    }

    private int nextCluster(int index)
    {
        if (index >= length()) return length();
        int next = clusters().following(index);
        return next == BreakIterator.DONE ? length() : next;
    }

    private BreakIterator clusters()
    {
        BreakIterator clusters = BreakIterator.getCharacterInstance();
        clusters.setText(toString());
        return clusters;
    }

    // Whether the cluster from start to end is an emoji of more than one code point: what joins or modifies emoji (a
    // ZWJ, the emoji presentation selector, a keycap, a skin tone, a tag) or pairs into a flag (regional indicators).
    private boolean isEmoji(int start, int end)
    {
        for (int i = start; i < end; )
        {
            int codePoint = Character.codePointAt(this, i);
            if (codePoint == 0x200D || codePoint == 0xFE0F || codePoint == 0x20E3
                || (codePoint >= 0x1F3FB && codePoint <= 0x1F3FF)
                || (codePoint >= 0x1F1E6 && codePoint <= 0x1F1FF)
                || (codePoint >= 0xE0020 && codePoint <= 0xE007F))
                return true;
            i += Character.charCount(codePoint);
        }
        return false;
    }

    /** {@code text} without its line breaks, for a field of one line; {@code text} itself (and its spans) when it has none. */
    static CharSequence withoutNewlines(CharSequence text)
    {
        boolean any = false;
        for (int i = 0; i < text.length() && !any; i++)
        {
            char c = text.charAt(i);
            any = c == '\n' || c == '\r';
        }
        if (!any) return text;
        StringBuilder kept = new StringBuilder(text.length());
        for (int i = 0; i < text.length(); i++)
        {
            char c = text.charAt(i);
            if (c != '\n' && c != '\r') kept.append(c);
        }
        return kept.toString();
    }
}
