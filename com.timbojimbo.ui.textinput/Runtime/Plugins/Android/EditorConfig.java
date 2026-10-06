package com.timbojimbo.textinput;

import android.text.InputType;
import android.view.inputmethod.EditorInfo;

/**
 * What a field asks of the keyboard (C#'s TextInputConfig, as the enums' values and a few flags C# works out), and the
 * EditorInfo it makes: the input type, which picks the keyboard and what it suggests, and the IME options, which pick
 * what the return key says.
 */
final class EditorConfig
{
    // TextContentType's values (Username, 7, is plain text with no suggestions).
    static final int CONTENT_STANDARD = 0;
    static final int CONTENT_EMAIL = 1;
    static final int CONTENT_URL = 2;
    static final int CONTENT_NUMBER = 3;
    static final int CONTENT_DECIMAL = 4;
    static final int CONTENT_PHONE = 5;
    static final int CONTENT_NAME = 6;
    static final int CONTENT_PASSWORD = 8;
    static final int CONTENT_NEW_PASSWORD = 9;
    static final int CONTENT_ONE_TIME_CODE = 10;
    static final int CONTENT_SEARCH = 11;

    // TextReturnKey's values (Default, 0, is worked out from the content type).
    static final int RETURN_DONE = 1;
    static final int RETURN_GO = 2;
    static final int RETURN_NEXT = 3;
    static final int RETURN_SEARCH = 4;
    static final int RETURN_SEND = 5;

    // TextCapitalization's values (None, 1, adds nothing).
    static final int CAPS_SENTENCES = 0;
    static final int CAPS_WORDS = 2;
    static final int CAPS_CHARACTERS = 3;

    // What C# works out from TextInputConfig (Multiline, CorrectsWords, ReturnInsertsNewline, IsSecure).
    static final int FLAG_MULTILINE = 1;
    static final int FLAG_CORRECTS_WORDS = 2;
    static final int FLAG_RETURN_INSERTS_NEWLINE = 4;
    static final int FLAG_SECURE = 8;

    final int contentType;
    final int returnKey;
    final int capitalization;
    final int flags;

    EditorConfig(int contentType, int returnKey, int capitalization, int flags)
    {
        this.contentType = contentType;
        this.returnKey = returnKey;
        this.capitalization = capitalization;
        this.flags = flags;
    }

    /** Whether the text can run over several lines (a newline can be typed or pasted). */
    boolean multiline()
    {
        return (flags & FLAG_MULTILINE) != 0;
    }

    /** Whether the return key types a new line rather than submitting. */
    boolean returnInsertsNewline()
    {
        return (flags & FLAG_RETURN_INSERTS_NEWLINE) != 0;
    }

    /** Whether what is typed is a password: nothing suggested, learnt, copied or cut. */
    boolean secure()
    {
        return (flags & FLAG_SECURE) != 0;
    }

    /** Fills in the input type and IME options. */
    void fill(EditorInfo info)
    {
        info.inputType = inputType();
        info.imeOptions = imeOptions();
    }

    private int inputType()
    {
        switch (contentType)
        {
            case CONTENT_NUMBER:
            case CONTENT_ONE_TIME_CODE:
                return InputType.TYPE_CLASS_NUMBER;
            case CONTENT_DECIMAL:
                return InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL;
            case CONTENT_PHONE:
                return InputType.TYPE_CLASS_PHONE;
            default:
                break;
        }

        int type = InputType.TYPE_CLASS_TEXT;
        switch (contentType)
        {
            case CONTENT_EMAIL:
                type |= InputType.TYPE_TEXT_VARIATION_EMAIL_ADDRESS;
                break;
            case CONTENT_URL:
                type |= InputType.TYPE_TEXT_VARIATION_URI;
                break;
            case CONTENT_NAME:
                type |= InputType.TYPE_TEXT_VARIATION_PERSON_NAME;
                break;
            case CONTENT_PASSWORD:
            case CONTENT_NEW_PASSWORD:
                type |= InputType.TYPE_TEXT_VARIATION_PASSWORD;
                break;
            default:
                break;
        }

        type |= (flags & FLAG_CORRECTS_WORDS) != 0 && !secure()
            ? InputType.TYPE_TEXT_FLAG_AUTO_CORRECT
            : InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS;
        if (multiline()) type |= InputType.TYPE_TEXT_FLAG_MULTI_LINE;

        // Capitals only where the text is prose (or a name): an address, a user name or a password typed with the
        // shift key held at its start would be wrong, whatever the config's default says.
        if (contentType == CONTENT_STANDARD || contentType == CONTENT_NAME || contentType == CONTENT_SEARCH)
        {
            switch (capitalization)
            {
                case CAPS_SENTENCES:
                    type |= InputType.TYPE_TEXT_FLAG_CAP_SENTENCES;
                    break;
                case CAPS_WORDS:
                    type |= InputType.TYPE_TEXT_FLAG_CAP_WORDS;
                    break;
                case CAPS_CHARACTERS:
                    type |= InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS;
                    break;
                default:
                    break;
            }
        }
        return type;
    }

    private int imeOptions()
    {
        // Never the full-screen editor a landscape keyboard would otherwise put over the game.
        int options = EditorInfo.IME_FLAG_NO_FULLSCREEN | EditorInfo.IME_FLAG_NO_EXTRACT_UI;
        if (secure()) options |= EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING;

        // A multi-line field whose return key types a new line shows the newline key, as TextView does; one with an
        // action (a chat composer's Send) shows the action.
        if (returnInsertsNewline())
            return options | EditorInfo.IME_ACTION_UNSPECIFIED | EditorInfo.IME_FLAG_NO_ENTER_ACTION;

        switch (returnKey)
        {
            case RETURN_DONE:
                return options | EditorInfo.IME_ACTION_DONE;
            case RETURN_GO:
                return options | EditorInfo.IME_ACTION_GO;
            case RETURN_NEXT:
                return options | EditorInfo.IME_ACTION_NEXT;
            case RETURN_SEARCH:
                return options | EditorInfo.IME_ACTION_SEARCH;
            case RETURN_SEND:
                return options | EditorInfo.IME_ACTION_SEND;
            default:
                return options | (contentType == CONTENT_SEARCH ? EditorInfo.IME_ACTION_SEARCH : EditorInfo.IME_ACTION_DONE);
        }
    }
}
