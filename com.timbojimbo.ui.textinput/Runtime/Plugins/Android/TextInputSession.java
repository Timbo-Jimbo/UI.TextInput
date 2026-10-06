package com.timbojimbo.textinput;

import android.graphics.RectF;

/**
 * One stretch of editing one field, numbered by Unity's side: the mirror of its value, its config, the serial of the
 * last value Unity pushed into it, and what waits for the keyboard's batch edit to end.
 */
final class TextInputSession
{
    final int id;
    final EditingState state;
    EditorConfig config;
    int appliedSerial;

    // Where the field and its caret are, as fractions of Unity's view (y down), once Unity has said.
    final RectF field = new RectF();
    final RectF caret = new RectF();
    boolean hasGeometry;
    // Whether the character at the caret reads right to left, as Unity last said.
    boolean caretRightToLeft;

    // A value pushed while the keyboard was in the middle of a batch edit, put in as the batch ends (only the latest
    // matters, told to the keyboard the strongest way any of those pushed asked for).
    private boolean mHasPending;
    private int mPendingSerial;
    private String mPendingText;
    private int mPendingBase;
    private int mPendingExtent;
    private int mPendingComposingStart;
    private int mPendingComposingEnd;
    private int mPendingKind;

    // Editing keys sent during a batch edit, sent after its edit as the batch ends: each intent, then 1 if extending.
    private int[] mIntents = new int[8];
    private int mIntentCount;

    TextInputSession(int id, EditorConfig config, EditingState state)
    {
        this.id = id;
        this.config = config;
        this.state = state;
    }

    void deferValue(int serial, String text, int base, int extent, int composingStart, int composingEnd, int kind)
    {
        mPendingKind = mHasPending ? Math.max(mPendingKind, kind) : kind;
        mHasPending = true;
        mPendingSerial = serial;
        mPendingText = text;
        mPendingBase = base;
        mPendingExtent = extent;
        mPendingComposingStart = composingStart;
        mPendingComposingEnd = composingEnd;
    }

    void deferIntent(int intent, boolean extend)
    {
        if (mIntentCount + 2 > mIntents.length)
        {
            int[] grown = new int[mIntents.length * 2];
            System.arraycopy(mIntents, 0, grown, 0, mIntentCount);
            mIntents = grown;
        }
        mIntents[mIntentCount++] = intent;
        mIntents[mIntentCount++] = extend ? 1 : 0;
    }

    /** Queues the editing keys sent during the batch edit that just ended. */
    void flushIntents()
    {
        for (int i = 0; i < mIntentCount; i += 2)
            TextInputBridge.queueIntent(id, mIntents[i], mIntents[i + 1] != 0);
        mIntentCount = 0;
    }

    /** Puts in the value pushed during the batch edit that just ended, if one was, through {@code view}. */
    void applyPending(TextInputView view)
    {
        if (!mHasPending) return;
        mHasPending = false;
        String text = mPendingText;
        mPendingText = null;
        view.applyValue(this, mPendingSerial, text, mPendingBase, mPendingExtent, mPendingComposingStart,
            mPendingComposingEnd, mPendingKind);
    }
}
