// The C side of IosTextInputBackend.cs: what C# calls, and what native code calls back. The structs and constants here
// are mirrored field for field in that file; TJTI_Init checks the struct sizes, so a mismatch is reported rather than
// read as garbage.
//
// Everything runs on the main thread: Unity's player loop runs there (from the display link), and so do UIKit's calls
// into the proxy view and its keyboard notifications. Text crosses as UTF-16 (a pointer and a length in code units), so
// indices mean the same as C#'s string indices and NSString's ranges. Rects are in the Unity view's points, origin
// top-left; C# converts from and to Unity's screen pixels (origin bottom-left).

#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// What the field holds (C#'s TextContentType).
enum
{
    TJTIContentStandard = 0,
    TJTIContentEmail = 1,
    TJTIContentUrl = 2,
    TJTIContentNumber = 3,
    TJTIContentDecimal = 4,
    TJTIContentPhone = 5,
    TJTIContentName = 6,
    TJTIContentUsername = 7,
    TJTIContentPassword = 8,
    TJTIContentNewPassword = 9,
    TJTIContentOneTimeCode = 10,
    TJTIContentSearch = 11,
};

// What the return key says (C#'s TextReturnKey).
enum
{
    TJTIReturnDefault = 0,
    TJTIReturnDone = 1,
    TJTIReturnGo = 2,
    TJTIReturnNext = 3,
    TJTIReturnSearch = 4,
    TJTIReturnSend = 5,
};

// Which letters start in capitals (C#'s TextCapitalization).
enum
{
    TJTICapitalizeSentences = 0,
    TJTICapitalizeNone = 1,
    TJTICapitalizeWords = 2,
    TJTICapitalizeCharacters = 3,
};

// What the edit menu offers (C#'s TextEditActions).
enum
{
    TJTIActionCut = 1,
    TJTIActionCopy = 2,
    TJTIActionPaste = 4,
    TJTIActionSelectAll = 8,
};

// What the proxy reports besides edits: the return key, and the hardware keyboard's keys that need the field's own
// layout (left and right as the text shows on screen, by a character, a word or to the line's end; a line up or down)
// or its own history (undo).
enum
{
    TJTIIntentReturn = 0,
    TJTIIntentMoveLeft = 1,
    TJTIIntentMoveRight = 2,
    TJTIIntentMoveUp = 3,
    TJTIIntentMoveDown = 4,
    TJTIIntentMoveWordLeft = 5,
    TJTIIntentMoveWordRight = 6,
    TJTIIntentMoveLineLeft = 7,
    TJTIIntentMoveLineRight = 8,
    TJTIIntentUndo = 9,
    TJTIIntentRedo = 10,
};

// How the keyboard moves to where a keyboard event says it is going.
enum
{
    // It is there at once.
    TJTICurveNone = 0,
    // A CASpringAnimation's damped spring: mass, stiffness, damping and initialVelocity.
    TJTICurveSpring = 1,
    // A cubic Bézier timing curve through (c1x, c1y) and (c2x, c2y), as CAMediaTimingFunction's.
    TJTICurveBezier = 2,
};

typedef struct TJTIRect
{
    float x, y, width, height;
} TJTIRect;

// What a field asks of the keyboard. The derived flags are worked out on the C# side (TextInputConfig), so both sides
// agree on what a config means.
typedef struct TJTIConfig
{
    int32_t contentType;
    int32_t returnKey;
    int32_t capitalization;
    int32_t multiline;
    int32_t secure;
    int32_t correctsWords;
    int32_t returnInsertsNewline;
    int32_t enablesReturnKeyAutomatically;
} TJTIConfig;

// The keyboard is moving (or has moved) to a new place. `inset` is how far up from the bottom of the Unity view a docked
// keyboard will cover it, in points; a floating or split keyboard covers nothing. The move starts at `startTime`
// (CACurrentMediaTime) and lasts `duration` seconds along `curve`.
typedef struct TJTIKeyboardEvent
{
    double startTime;
    double duration;
    float inset;
    float viewHeight;
    float mass;
    float stiffness;
    float damping;
    float initialVelocity;
    float c1x, c1y, c2x, c2y;
    int32_t curve;
    int32_t visible;
} TJTIKeyboardEvent;

// Native code calls these; each only queues what it is told on the C# side and returns. `text` is valid only during the
// call. A composing range of (-1, -1) is none.
typedef struct TJTICallbacks
{
    // The keyboard changed the value (typing, autocorrect, marked text, dictation, the edit menu), based on the value
    // pushed with serial `baseSerial`.
    void (*onEdit)(int32_t session, int32_t baseSerial, const uint16_t* text, int32_t length,
                   int32_t selectionBase, int32_t selectionExtent, int32_t composingStart, int32_t composingEnd);
    // A TJTIIntent; `extend`: shift is held.
    void (*onIntent)(int32_t session, int32_t intent, int32_t extend);
    // The session was ended by the system: the user dismissed the keyboard, or something else became first responder.
    void (*onEnded)(int32_t session);
    void (*onKeyboard)(const TJTIKeyboardEvent* keyboard);
    // Whether the keyboard in use writes right to left (Arabic, Hebrew): told when a session's proxy takes the keyboard,
    // when the user switches keyboards, and when UIKit sets the text's direction to the keyboard's.
    void (*onKeyboardDirection)(int32_t rightToLeft);
} TJTICallbacks;

// Registers the callbacks and starts following the keyboard. Returns 0, and does nothing, if the sizes C# passes for the
// structs differ from native's.
int32_t TJTI_Init(const TJTICallbacks* callbacks, int32_t callbacksSize, int32_t configSize, int32_t keyboardEventSize);

// A session starts: a new proxy view holding this value (pushed with serial `serial`) becomes first responder.
void TJTI_Attach(int32_t session, const TJTIConfig* config, const uint16_t* text, int32_t length,
                 int32_t selectionBase, int32_t selectionExtent, int32_t composingStart, int32_t composingEnd, int32_t serial);

// The session ends; the keyboard goes down at the end of this run loop turn, unless another session attaches first.
void TJTI_Detach(int32_t session);

// The session's config changed: the keyboard is reloaded.
void TJTI_SetConfig(int32_t session, const TJTIConfig* config);

// C# changed the value itself: the keyboard is told what changed.
void TJTI_SetValue(int32_t session, int32_t serial, const uint16_t* text, int32_t length,
                   int32_t selectionBase, int32_t selectionExtent, int32_t composingStart, int32_t composingEnd);

// Where the caret and the composing text are (points, top-left). `hasComposing` is 0 when there is no composing rect.
void TJTI_SetGeometry(int32_t session, TJTIRect caret, TJTIRect composing, int32_t hasComposing);

// Which way the session's text reads, as C# laid it out. `rightToLeft` is the base writing direction UIKit is told, and
// which way left and right go when UIKit moves by them itself. `caretRightToLeft`, the direction of the character at the
// caret, is not used: none of the proxy's answers carries a character's own direction.
void TJTI_SetDirection(int32_t session, int32_t rightToLeft, int32_t caretRightToLeft);

// Shows the system edit menu by `target` (points, top-left), offering the TJTIAction flags in `actions`.
void TJTI_ShowEditMenu(int32_t session, TJTIRect target, int32_t actions);

void TJTI_HideEditMenu(void);

// The media time (CACurrentMediaTime's clock) at which the frame being made now is expected on screen. Asked during
// Unity's frame, which runs in Unity's display link's callback.
double TJTI_PresentationTime(void);

// The Unity view's size, in points.
void TJTI_GetViewSize(float* width, float* height);

#ifdef __cplusplus
}
#endif
