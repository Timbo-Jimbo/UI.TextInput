package com.timbojimbo.textinput;

import android.content.ClipDescription;
import android.content.ClipboardManager;
import android.graphics.Rect;
import android.graphics.RectF;
import android.view.ActionMode;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;

/**
 * The edit menu: Android's floating text toolbar (a floating ActionMode, as TextView's), started on Unity's surface by
 * a target rect that comes from Unity, offering what Unity asks for (cut, copy, paste, select all) of what can be done:
 * paste only with text on the clipboard, read only as it is chosen. What it does is done on the mirror and goes to
 * Unity as an edit.
 */
final class EditMenu extends ActionMode.Callback2
{
    // TextEditActions' values.
    private static final int CUT = 1;
    private static final int COPY = 2;
    private static final int PASTE = 4;
    private static final int SELECT_ALL = 8;

    private final TextInputView mView;
    private ActionMode mMode;
    private TextInputSession mSession;
    // By what it shows: fractions of Unity's surface (y down).
    private final RectF mTarget = new RectF();
    private int mActions;

    EditMenu(TextInputView view)
    {
        mView = view;
    }

    void show(TextInputSession session, float left, float top, float right, float bottom, int actions)
    {
        mSession = session;
        mTarget.set(left, top, right, bottom);
        mActions = offered(actions);
        if (mActions == 0)
        {
            hide();
            return;
        }
        if (mMode != null)
        {
            mMode.invalidate();
            mMode.invalidateContentRect();
            return;
        }
        mMode = mView.surface().startActionMode(this, ActionMode.TYPE_FLOATING);
    }

    void hide()
    {
        if (mMode != null) mMode.finish();
    }

    // What of the actions asked for can be done now: paste only with text to paste.
    private int offered(int actions)
    {
        if ((actions & PASTE) == 0) return actions;
        ClipboardManager clipboard = mView.clipboard();
        ClipDescription description = clipboard.hasPrimaryClip() ? clipboard.getPrimaryClipDescription() : null;
        return description != null && description.hasMimeType("text/*") ? actions : actions & ~PASTE;
    }

    @Override
    public boolean onCreateActionMode(ActionMode mode, Menu menu)
    {
        fill(menu);
        return menu.size() > 0;
    }

    @Override
    public boolean onPrepareActionMode(ActionMode mode, Menu menu)
    {
        fill(menu);
        return true;
    }

    private void fill(Menu menu)
    {
        menu.clear();
        if ((mActions & CUT) != 0) add(menu, android.R.id.cut, 0, android.R.string.cut);
        if ((mActions & COPY) != 0) add(menu, android.R.id.copy, 1, android.R.string.copy);
        if ((mActions & PASTE) != 0) add(menu, android.R.id.paste, 2, android.R.string.paste);
        if ((mActions & SELECT_ALL) != 0) add(menu, android.R.id.selectAll, 3, android.R.string.selectAll);
    }

    private static void add(Menu menu, int id, int order, int title)
    {
        menu.add(Menu.NONE, id, order, title).setShowAsAction(MenuItem.SHOW_AS_ACTION_ALWAYS);
    }

    @Override
    public boolean onActionItemClicked(ActionMode mode, MenuItem item)
    {
        mView.performMenuAction(mSession, item.getItemId());
        mode.finish();
        return true;
    }

    @Override
    public void onDestroyActionMode(ActionMode mode)
    {
        if (mode == mMode) mMode = null;
    }

    @Override
    public void onGetContentRect(ActionMode mode, View view, Rect outRect)
    {
        int width = view.getWidth();
        int height = view.getHeight();
        outRect.set(Math.round(mTarget.left * width), Math.round(mTarget.top * height),
            Math.round(mTarget.right * width), Math.round(mTarget.bottom * height));
    }
}
