// The proxy view the keyboard types into: a UITextInput holding a mirror of the field's value, as Flutter's
// FlutterTextInputView does. Our engine draws the field (text, caret, selection, composition underline, handles); this
// view only answers the keyboard's questions from the mirror, applies the keyboard's edits to it and reports each as one
// edit of the whole value.
//
// Edits made by UIKit (typing, autocorrect, marked text, dictation, the edit menu) are never told to the inputDelegate:
// UIKit made them and already knows. Values pushed from C#, and the few hardware keys this view handles itself, are, in
// Flutter's order, and only when something changed (FlutterTextInputPlugin.mm setTextInputState; extra notifications
// break IME entry, flutter#133908).
//
// Indices are UTF-16 code units throughout, as UITextInput's offsets are (positionFromPosition:offset: and
// offsetFromPosition:toPosition: are exact inverses). Grapheme clusters are kept whole only where a person moves:
// setting the selection, directional moves and the tokenizer.

#import "TJTextInputInternal.h"

#if defined(__IPHONE_17_0) && __IPHONE_OS_VERSION_MAX_ALLOWED >= __IPHONE_17_0
#define TJTI_SDK_17 1
#else
#define TJTI_SDK_17 0
#endif

#if defined(__IPHONE_18_0) && __IPHONE_OS_VERSION_MAX_ALLOWED >= __IPHONE_18_0
#define TJTI_SDK_18 1
#else
#define TJTI_SDK_18 0
#endif

static const NSRange TJNoRange = {NSNotFound, 0};

// What firstRectForRange: answers when the composing text has no place yet: UIKit shows no candidate window for it
// (FlutterTextInputPlugin.mm kInvalidFirstRect).
static const CGRect TJInvalidFirstRect = {{-1, -1}, {9999, 9999}};

static inline BOOL TJHasRange(NSRange range)
{
    return range.location != NSNotFound;
}

static NSUInteger TJClampIndex(NSInteger index, NSUInteger length)
{
    if (index < 0)
        return 0;
    return MIN((NSUInteger)index, length);
}

// `range` kept within a text `length` long.
static NSRange TJClamp(NSRange range, NSUInteger length)
{
    NSUInteger start = MIN(range.location, length);
    NSUInteger end = range.length > length - start ? length : start + range.length;
    return NSMakeRange(start, end - start);
}

static NSInteger TJIndexOf(UITextPosition* position)
{
    // The Japanese keyboard sometimes passes UIKit's own positions (FlutterTextInputPlugin.mm selectionRectsForRange:).
    return [position isKindOfClass:TJTextPosition.class] ? (NSInteger)((TJTextPosition*)position).index : -1;
}

static NSRange TJRangeOf(UITextRange* range)
{
    return [range isKindOfClass:TJTextRange.class] ? ((TJTextRange*)range).range : TJNoRange;
}

#pragma mark - Grapheme clusters and lines

// The end of the cluster at `index`.
static NSUInteger TJNextStop(NSString* text, NSUInteger index)
{
    if (index >= text.length)
        return text.length;
    return NSMaxRange([text rangeOfComposedCharacterSequenceAtIndex:index]);
}

// The start of the cluster before `index`.
static NSUInteger TJPreviousStop(NSString* text, NSUInteger index)
{
    if (index == 0)
        return 0;
    return [text rangeOfComposedCharacterSequenceAtIndex:MIN(index, text.length) - 1].location;
}

// `range` widened to whole clusters: its start moved back and its end moved on to cluster boundaries.
static NSRange TJSnapToClusters(NSString* text, NSRange range)
{
    NSUInteger length = text.length;
    NSUInteger start = range.location < length ? [text rangeOfComposedCharacterSequenceAtIndex:range.location].location : length;
    NSUInteger end = NSMaxRange(range);
    if (range.length == 0)
        end = start;
    else if (end < length && end > 0)
    {
        NSRange cluster = [text rangeOfComposedCharacterSequenceAtIndex:end];
        if (cluster.location < end)
            end = NSMaxRange(cluster);
    }
    return NSMakeRange(start, end - start);
}

// The start of the line (between '\n's) `index` is on.
static NSUInteger TJLineStart(NSString* text, NSUInteger index)
{
    NSRange found = [text rangeOfString:@"\n" options:NSLiteralSearch | NSBackwardsSearch range:NSMakeRange(0, index)];
    return TJHasRange(found) ? found.location + 1 : 0;
}

// The end of the line `index` is on, before its '\n'.
static NSUInteger TJLineEnd(NSString* text, NSUInteger index)
{
    NSRange found = [text rangeOfString:@"\n" options:NSLiteralSearch range:NSMakeRange(index, text.length - index)];
    return TJHasRange(found) ? found.location : text.length;
}

static BOOL TJIsEmojiBlock(UTF32Char c)
{
    return (c >= 0x1F000 && c <= 0x1FAFF) || (c >= 0x2600 && c <= 0x27BF) || (c >= 0x2300 && c <= 0x23FF)
        || (c >= 0x2B00 && c <= 0x2BFF);
}

// Whether backspace takes the whole of a cluster, as TextBoundaries.DeleteBackwardStart has it on the C# side: CR LF, or
// an emoji or a sequence of them: it starts with an emoji, or holds an emoji presentation selector (U+FE0F), a keycap
// (U+20E3) or tag characters (subdivision flags). Flags (regional indicator pairs), skin tones and ZWJ sequences all
// start with an emoji. A letter with marks, Thai or Devanagari does not go whole.
static BOOL TJDeletesWhole(NSString* text, NSRange cluster)
{
    if ([text characterAtIndex:cluster.location] == '\r')
        return YES;
    NSUInteger end = NSMaxRange(cluster);
    for (NSUInteger i = cluster.location; i < end;)
    {
        UTF32Char c = [text characterAtIndex:i];
        NSUInteger units = 1;
        if (CFStringIsSurrogateHighCharacter((UniChar)c) && i + 1 < end)
        {
            UniChar low = [text characterAtIndex:i + 1];
            if (CFStringIsSurrogateLowCharacter(low))
            {
                c = CFStringGetLongCharacterForSurrogatePair((UniChar)c, low);
                units = 2;
            }
        }
        if (i == cluster.location && TJIsEmojiBlock(c))
            return YES;
        if (c == 0xFE0F || c == 0x20E3 || (c >= 0xE0020 && c <= 0xE007F))
            return YES;
        i += units;
    }
    return NO;
}

// Where backspace at `index` deletes from, as UIKit's own fields do (and Flutter, with flutter#24203): a whole emoji
// cluster, but otherwise only the last code point, so that Thai and Indic vowel marks go one at a time; never half a
// surrogate pair.
static NSUInteger TJDeleteBackwardStart(NSString* text, NSUInteger index)
{
    if (index == 0)
        return 0;
    NSRange cluster = [text rangeOfComposedCharacterSequenceAtIndex:index - 1];
    if (TJDeletesWhole(text, cluster))
        return cluster.location;
    if (index >= 2 && CFStringIsSurrogateLowCharacter([text characterAtIndex:index - 1])
        && CFStringIsSurrogateHighCharacter([text characterAtIndex:index - 2]))
        return index - 2;
    return index - 1;
}

static UICommand* TJFindCommand(NSArray<UIMenuElement*>* elements, SEL action)
{
    for (UIMenuElement* element in elements)
    {
        if ([element isKindOfClass:UICommand.class])
        {
            if (((UICommand*)element).action == action)
                return (UICommand*)element;
        }
        else if ([element isKindOfClass:UIMenu.class])
        {
            UICommand* found = TJFindCommand(((UIMenu*)element).children, action);
            if (found)
                return found;
        }
    }
    return nil;
}

#pragma mark - Config

static UIKeyboardType TJKeyboardTypeFor(int32_t content)
{
    UIKeyboardType type;
    switch (content)
    {
        case TJTIContentEmail: type = UIKeyboardTypeEmailAddress; break;
        case TJTIContentUrl: type = UIKeyboardTypeURL; break;
        case TJTIContentNumber:
        case TJTIContentOneTimeCode: type = UIKeyboardTypeNumberPad; break;
        case TJTIContentDecimal: type = UIKeyboardTypeDecimalPad; break;
        case TJTIContentPhone: type = UIKeyboardTypePhonePad; break;
        default: type = UIKeyboardTypeDefault; break;
    }
    // On iPadOS 26 the number and phone pads open as a small floating bubble in windowed apps, and its dismissal cannot
    // be told apart from editing going on; Unity's own keyboard swaps them for the numbers and punctuation keyboard
    // there (Trampoline Keyboard.mm setKeyboardParams:), and so does this.
    if (@available(iOS 26.0, *))
    {
        if (UIDevice.currentDevice.userInterfaceIdiom == UIUserInterfaceIdiomPad
            && (type == UIKeyboardTypeNumberPad || type == UIKeyboardTypeDecimalPad || type == UIKeyboardTypePhonePad))
            type = UIKeyboardTypeNumbersAndPunctuation;
    }
    return type;
}

static UIReturnKeyType TJReturnKeyTypeFor(int32_t returnKey, int32_t content)
{
    switch (returnKey)
    {
        case TJTIReturnDone: return UIReturnKeyDone;
        case TJTIReturnGo: return UIReturnKeyGo;
        case TJTIReturnNext: return UIReturnKeyNext;
        case TJTIReturnSearch: return UIReturnKeySearch;
        case TJTIReturnSend: return UIReturnKeySend;
        default: return content == TJTIContentSearch ? UIReturnKeySearch : UIReturnKeyDefault;
    }
}

static UITextAutocapitalizationType TJCapitalizationFor(int32_t capitalization, int32_t content, BOOL secure)
{
    // Addresses, user names, passwords and codes are never capitalised, whatever the field asks (Unity's keyboard
    // does the same for URL and email keyboards).
    switch (content)
    {
        case TJTIContentEmail:
        case TJTIContentUrl:
        case TJTIContentUsername:
        case TJTIContentPassword:
        case TJTIContentNewPassword:
        case TJTIContentOneTimeCode:
            return UITextAutocapitalizationTypeNone;
    }
    if (secure)
        return UITextAutocapitalizationTypeNone;
    switch (capitalization)
    {
        case TJTICapitalizeNone: return UITextAutocapitalizationTypeNone;
        case TJTICapitalizeWords: return UITextAutocapitalizationTypeWords;
        case TJTICapitalizeCharacters: return UITextAutocapitalizationTypeAllCharacters;
        default: return UITextAutocapitalizationTypeSentences;
    }
}

static UITextContentType TJContentTypeFor(int32_t content)
{
    switch (content)
    {
        case TJTIContentEmail: return UITextContentTypeEmailAddress;
        case TJTIContentUrl: return UITextContentTypeURL;
        case TJTIContentPhone: return UITextContentTypeTelephoneNumber;
        case TJTIContentName: return UITextContentTypeName;
        case TJTIContentUsername: return UITextContentTypeUsername;
        case TJTIContentPassword: return UITextContentTypePassword;
        case TJTIContentNewPassword: return UITextContentTypeNewPassword;
        case TJTIContentOneTimeCode: return UITextContentTypeOneTimeCode;
        default: return nil;
    }
}

#pragma mark - Positions and ranges

@implementation TJTextPosition

+ (instancetype)positionWithIndex:(NSUInteger)index affinity:(UITextStorageDirection)affinity
{
    TJTextPosition* position = [[TJTextPosition alloc] init];
    position->_index = index;
    position->_affinity = affinity;
    return position;
}

@end

@implementation TJTextRange

+ (instancetype)rangeWithNSRange:(NSRange)range
{
    TJTextRange* textRange = [[TJTextRange alloc] init];
    textRange->_range = range;
    return textRange;
}

- (UITextPosition*)start
{
    return [TJTextPosition positionWithIndex:_range.location affinity:UITextStorageDirectionForward];
}

- (UITextPosition*)end
{
    return [TJTextPosition positionWithIndex:NSMaxRange(_range) affinity:UITextStorageDirectionBackward];
}

- (BOOL)isEmpty
{
    return _range.length == 0;
}

- (id)copyWithZone:(NSZone*)zone
{
    return [TJTextRange rangeWithNSRange:_range];
}

@end

#pragma mark - Tokenizer

// UIKit's string tokenizer, but with lines as the text's own lines: UITextInputStringTokenizer does not know lines
// (Apple: subclasses handle layout-dependent granularities; FlutterTokenizer does the same). Lines wrapped by the
// field's layout are the field's business: the hardware keyboard's line keys come to it as intents.
@interface TJTokenizer : UITextInputStringTokenizer
- (instancetype)initWithView:(TJTextInputView*)view;
@end

@implementation TJTokenizer
{
    __weak TJTextInputView* _view;
}

- (instancetype)initWithView:(TJTextInputView*)view
{
    self = [super initWithTextInput:view];
    if (self)
        _view = view;
    return self;
}

- (UITextRange*)rangeEnclosingPosition:(UITextPosition*)position
                       withGranularity:(UITextGranularity)granularity
                           inDirection:(UITextDirection)direction
{
    if (granularity != UITextGranularityLine)
        return [super rangeEnclosingPosition:position withGranularity:granularity inDirection:direction];

    TJTextInputView* view = _view;
    NSInteger index = TJIndexOf(position);
    if (view == nil || index < 0)
        return nil;
    NSString* text = view.text;
    // A position at a boundary is enclosed only by the unit that follows it in the given direction.
    if ((NSUInteger)index > text.length || ((NSUInteger)index == text.length && direction == UITextStorageDirectionForward))
        return nil;
    NSUInteger start = TJLineStart(text, index);
    return [TJTextRange rangeWithNSRange:NSMakeRange(start, TJLineEnd(text, index) - start)];
}

@end

#pragma mark - The proxy view

@implementation TJTextInputView
{
    NSMutableString* _text;
    NSRange _selection;
    // The selection's base is its end rather than its start (a selection C# extended backwards); UIKit's ranges have
    // no direction, so it is kept here to report the selection back as C# had it.
    BOOL _selectionReversed;
    // The marked (composing) text, or TJNoRange: never an empty range (markedTextRange is nil without one).
    NSRange _marked;

    int32_t _appliedSerial;
    BOOL _multiline;
    BOOL _returnInsertsNewline;
    BOOL _endReported;

    // UIKit's calls into the mirror can nest (paste: inserts); each outermost one is reported once.
    int _editDepth;
    BOOL _edited;

    CGRect _caret;
    CGRect _composing;
    BOOL _hasGeometry;
    BOOL _hasComposing;

    CGRect _menuTarget;
    int32_t _menuActions;
    id _editMenu;

    UITextInteraction* _delegateInteraction;
    TJTokenizer* _tokenizer;
}

@synthesize inputDelegate = _inputDelegate;
@synthesize markedTextStyle = _markedTextStyle;
// UITextInputTraits' properties: declared by the protocol (UITextInput adopts it), so the class synthesizes them itself.
// Redeclared in a class extension, they would clash with the protocol's declarations in the primary interface.
@synthesize autocapitalizationType = _autocapitalizationType;
@synthesize autocorrectionType = _autocorrectionType;
@synthesize spellCheckingType = _spellCheckingType;
@synthesize smartQuotesType = _smartQuotesType;
@synthesize smartDashesType = _smartDashesType;
@synthesize smartInsertDeleteType = _smartInsertDeleteType;
@synthesize keyboardType = _keyboardType;
@synthesize keyboardAppearance = _keyboardAppearance;
@synthesize returnKeyType = _returnKeyType;
@synthesize enablesReturnKeyAutomatically = _enablesReturnKeyAutomatically;
@synthesize secureTextEntry = _secureTextEntry;
@synthesize textContentType = _textContentType;
#if TJTI_SDK_17
@synthesize inlinePredictionType = _inlinePredictionType;
#endif
#if TJTI_SDK_18
@synthesize writingToolsBehavior = _writingToolsBehavior;
#endif

- (instancetype)initWithSession:(int32_t)session config:(const TJTIConfig*)config
{
    self = [super initWithFrame:CGRectZero];
    if (self == nil)
        return nil;

    _session = session;
    _active = YES;
    _text = [[NSMutableString alloc] init];
    _selection = NSMakeRange(0, 0);
    _marked = TJNoRange;
    _keyboardAppearance = UIKeyboardAppearanceDefault;

    self.opaque = NO;
    self.backgroundColor = nil;
    self.isAccessibilityElement = NO;
    self.autoresizingMask = UIViewAutoresizingFlexibleWidth | UIViewAutoresizingFlexibleHeight;
    // The view covers the Unity view, so that its geometry answers in the Unity view's points. UIKit's own caret,
    // selection and autocorrection highlight follow the tint; ours are drawn by the engine.
    self.tintColor = UIColor.clearColor;
    // Apple Pencil's Scribble writes into a text input wherever the pencil writes over it, and this one covers the whole
    // Unity view: it is turned off, so that writing on the screen stays the app's.
    [self addInteraction:[[UIScribbleInteraction alloc] initWithDelegate:self]];

    [self applyConfig:config];
    return self;
}

- (NSString*)text
{
    return _text;
}

- (void)applyConfig:(const TJTIConfig*)config
{
    int32_t content = config->contentType;
    BOOL secure = config->secure != 0;
    BOOL corrects = config->correctsWords != 0 && !secure;
    BOOL standard = content == TJTIContentStandard;

    _multiline = config->multiline != 0;
    _returnInsertsNewline = config->returnInsertsNewline != 0;
    self.keyboardType = TJKeyboardTypeFor(content);
    self.returnKeyType = TJReturnKeyTypeFor(config->returnKey, content);
    self.enablesReturnKeyAutomatically = config->enablesReturnKeyAutomatically != 0;
    self.autocapitalizationType = TJCapitalizationFor(config->capitalization, content, secure);
    // Since iOS 15 the QuickType bar follows spell checking rather than autocorrection; they are kept together, as
    // Unity's keyboard keeps them.
    self.autocorrectionType = corrects ? UITextAutocorrectionTypeDefault : UITextAutocorrectionTypeNo;
    self.spellCheckingType = corrects ? UITextSpellCheckingTypeDefault : UITextSpellCheckingTypeNo;
    // Curly quotes, long dashes and spaces added around pasted words belong in prose, not in an address or a code.
    self.smartQuotesType = standard ? UITextSmartQuotesTypeDefault : UITextSmartQuotesTypeNo;
    self.smartDashesType = standard ? UITextSmartDashesTypeDefault : UITextSmartDashesTypeNo;
    self.smartInsertDeleteType = standard ? UITextSmartInsertDeleteTypeDefault : UITextSmartInsertDeleteTypeNo;
    self.secureTextEntry = secure;
    self.textContentType = TJContentTypeFor(content);
#if TJTI_SDK_17
    // Inline predictions arrive as marked text meant to be drawn as ghost text, which the field does not draw.
    if (@available(iOS 17.0, *))
        self.inlinePredictionType = UITextInlinePredictionTypeNo;
#endif
#if TJTI_SDK_18
    // Writing Tools on a custom text view needs a UIWritingToolsCoordinator, which this view does not have.
    if (@available(iOS 18.0, *))
        self.writingToolsBehavior = UIWritingToolsBehaviorNone;
#endif
}

- (void)pushText:(NSString*)text
   selectionBase:(NSInteger)selectionBase
 selectionExtent:(NSInteger)selectionExtent
  composingStart:(NSInteger)composingStart
    composingEnd:(NSInteger)composingEnd
          serial:(int32_t)serial
          notify:(BOOL)notify
{
    _appliedSerial = serial;

    NSUInteger length = text.length;
    NSUInteger base = TJClampIndex(selectionBase, length);
    NSUInteger extent = TJClampIndex(selectionExtent, length);
    NSRange selection = NSMakeRange(MIN(base, extent), base > extent ? base - extent : extent - base);
    BOOL reversed = base > extent;
    NSRange marked = TJNoRange;
    if (composingStart >= 0 && composingEnd > composingStart)
    {
        marked = TJClamp(NSMakeRange((NSUInteger)composingStart, (NSUInteger)(composingEnd - composingStart)), length);
        if (marked.length == 0)
            marked = TJNoRange;
    }

    BOOL textChanged = ![_text isEqualToString:text];
    BOOL markedChanged = !NSEqualRanges(_marked, marked);
    BOOL selectionChanged = !NSEqualRanges(_selection, selection);
    if (!notify || !(textChanged || markedChanged || selectionChanged))
    {
        if (textChanged)
            [_text setString:text];
        _marked = marked;
        _selection = selection;
        _selectionReversed = reversed;
        return;
    }

    // UIKit sometimes leaves inputDelegate nil; a UITextInteraction added for the moment sets it, so that the keyboard
    // still hears of the change (flutter/engine#32881).
    BOOL borrowed = NO;
    if (self.inputDelegate == nil && self.isFirstResponder)
    {
        if (_delegateInteraction == nil)
        {
            _delegateInteraction = [UITextInteraction textInteractionForMode:UITextInteractionModeEditable];
            _delegateInteraction.textInput = self;
        }
        [self addInteraction:_delegateInteraction];
        borrowed = YES;
    }
    id<UITextInputDelegate> delegate = self.inputDelegate;

    // The text and the marked text change between textWillChange and textDidChange, the selection between its own
    // pair inside them (FlutterTextInputPlugin.mm setTextInputState:). A composition changed or cleared from our side
    // is a text change too, so that the keyboard drops what it was composing.
    BOOL textScope = textChanged || markedChanged;
    if (textScope)
        [delegate textWillChange:self];
    if (textChanged)
        [_text setString:text];
    _marked = marked;
    if (selectionChanged)
    {
        [delegate selectionWillChange:self];
        _selection = selection;
        _selectionReversed = reversed;
        [delegate selectionDidChange:self];
    }
    else
        _selectionReversed = reversed;
    if (textScope)
        [delegate textDidChange:self];

    if (borrowed)
        [self removeInteraction:_delegateInteraction];
}

- (void)setCaret:(CGRect)caret composing:(CGRect)composing hasComposing:(BOOL)hasComposing
{
    _caret = caret;
    _composing = composing;
    _hasComposing = hasComposing;
    _hasGeometry = YES;
}

- (void)deactivate
{
    if (!_active)
        return;
    [self hideEditMenu];
    _active = NO;
}

#pragma mark Reporting

- (void)tj_beginEdit
{
    _editDepth++;
}

- (void)tj_endEdit
{
    if (--_editDepth > 0 || !_edited)
        return;
    _edited = NO;
    if (!_active)
        return;
    NSInteger start = _selection.location;
    NSInteger end = NSMaxRange(_selection);
    TJTIReportEdit(_session, _appliedSerial, _text, _selectionReversed ? end : start, _selectionReversed ? start : end, _marked);
}

// `range` replaced by `text` in the mirror, as UIKit's fields do it: the caret after what was put in, nothing marked.
- (void)tj_replace:(NSRange)range with:(NSString*)text
{
    range = TJClamp(range, _text.length);
    [_text replaceCharactersInRange:range withString:text];
    _selection = NSMakeRange(range.location + text.length, 0);
    _selectionReversed = NO;
    _marked = TJNoRange;
    _edited = YES;
}

- (void)tj_returnPressed
{
    if (_active)
        TJTIReportIntent(_session, TJTIIntentReturn, NO);
}

// A change the keyboard did not make (a hardware key this view handles itself): the keyboard hears of it as it hears of
// a value from C#, and C# is told as of the keyboard's edits. Nothing is composed while it happens.
- (void)tj_change:(NSString*)text selectionBase:(NSUInteger)base selectionExtent:(NSUInteger)extent
{
    NSRange selection = NSMakeRange(MIN(base, extent), base > extent ? base - extent : extent - base);
    if ([_text isEqualToString:text] && NSEqualRanges(selection, _selection) && (base > extent) == _selectionReversed)
        return;
    [self tj_beginEdit];
    [self pushText:text
     selectionBase:(NSInteger)base
   selectionExtent:(NSInteger)extent
    composingStart:-1
      composingEnd:-1
            serial:_appliedSerial
            notify:YES];
    _edited = YES;
    [self tj_endEdit];
}

#pragma mark UIKeyInput

- (BOOL)hasText
{
    return _text.length > 0;
}

- (void)insertText:(NSString*)text
{
    // The return key: a new line where the field takes them, otherwise the field's own action. Usually caught in
    // shouldChangeTextInRange:replacementText:, which UIKit asks first.
    if ([text isEqualToString:@"\n"] && !_returnInsertsNewline)
    {
        [self tj_returnPressed];
        return;
    }
    [self tj_beginEdit];
    // Typing replaces the whole marked text, not just the selection inside it (flutter#59541).
    [self tj_replace:TJHasRange(_marked) ? _marked : _selection with:text ?: @""];
    [self tj_endEdit];
}

- (void)deleteBackward
{
    [self tj_beginEdit];
    NSRange range = _selection;
    if (range.length == 0 && range.location > 0)
    {
        NSUInteger start = TJDeleteBackwardStart(_text, range.location);
        range = NSMakeRange(start, range.location - start);
    }
    if (range.length > 0)
        [self tj_replace:range with:@""];
    [self tj_endEdit];
}

#pragma mark UITextInput: text

- (NSString*)textInRange:(UITextRange*)range
{
    NSRange textRange = TJRangeOf(range);
    if (!TJHasRange(textRange))
        return nil;
    return [_text substringWithRange:TJClamp(textRange, _text.length)];
}

- (void)replaceRange:(UITextRange*)range withText:(NSString*)text
{
    NSRange textRange = TJRangeOf(range);
    if (!TJHasRange(textRange))
        return;
    [self tj_beginEdit];
    [self tj_replace:textRange with:text ?: @""];
    [self tj_endEdit];
}

- (BOOL)shouldChangeTextInRange:(UITextRange*)range replacementText:(NSString*)text
{
    if ([text isEqualToString:@"\n"] && !_returnInsertsNewline)
    {
        [self tj_returnPressed];
        return NO;
    }
    return YES;
}

- (UITextRange*)selectedTextRange
{
    return [TJTextRange rangeWithNSRange:_selection];
}

- (void)setSelectedTextRange:(UITextRange*)selectedTextRange
{
    NSRange range = TJRangeOf(selectedTextRange);
    if (!TJHasRange(range))
        return;
    range = TJSnapToClusters(_text, TJClamp(range, _text.length));
    if (NSEqualRanges(range, _selection))
        return;
    [self tj_beginEdit];
    _selection = range;
    _selectionReversed = NO;
    _edited = YES;
    [self tj_endEdit];
}

- (UITextRange*)markedTextRange
{
    return TJHasRange(_marked) ? [TJTextRange rangeWithNSRange:_marked] : nil;
}

- (void)setMarkedText:(NSString*)markedText selectedRange:(NSRange)selectedRange
{
    markedText = markedText ?: @"";
    [self tj_beginEdit];
    NSRange replaced = TJClamp(TJHasRange(_marked) ? _marked : _selection, _text.length);
    [_text replaceCharactersInRange:replaced withString:markedText];
    _marked = markedText.length > 0 ? NSMakeRange(replaced.location, markedText.length) : TJNoRange;
    // The selected range is within the marked text (Apple: "always relative to markedText").
    NSRange inMarked = TJHasRange(selectedRange) ? TJClamp(selectedRange, markedText.length) : NSMakeRange(markedText.length, 0);
    _selection = NSMakeRange(replaced.location + inMarked.location, inMarked.length);
    _selectionReversed = NO;
    _edited = YES;
    [self tj_endEdit];
}

- (void)setAttributedMarkedText:(NSAttributedString*)markedText selectedRange:(NSRange)selectedRange
{
    [self setMarkedText:markedText.string selectedRange:selectedRange];
}

- (void)unmarkText
{
    if (!TJHasRange(_marked))
        return;
    [self tj_beginEdit];
    _marked = TJNoRange;
    _edited = YES;
    [self tj_endEdit];
}

#pragma mark UITextInput: positions

- (UITextPosition*)beginningOfDocument
{
    return [TJTextPosition positionWithIndex:0 affinity:UITextStorageDirectionForward];
}

- (UITextPosition*)endOfDocument
{
    return [TJTextPosition positionWithIndex:_text.length affinity:UITextStorageDirectionBackward];
}

- (UITextRange*)textRangeFromPosition:(UITextPosition*)fromPosition toPosition:(UITextPosition*)toPosition
{
    NSInteger from = TJIndexOf(fromPosition);
    NSInteger to = TJIndexOf(toPosition);
    if (from < 0 || to < 0)
        return nil;
    // The string tokenizer can hand the ends over swapped with CJK text (flutter#58750); UITextView swaps them back.
    NSUInteger start = TJClampIndex(MIN(from, to), _text.length);
    NSUInteger end = TJClampIndex(MAX(from, to), _text.length);
    return [TJTextRange rangeWithNSRange:NSMakeRange(start, end - start)];
}

- (UITextPosition*)positionFromPosition:(UITextPosition*)position offset:(NSInteger)offset
{
    NSInteger index = TJIndexOf(position);
    if (index < 0)
        return nil;
    NSInteger moved = index + offset;
    if (moved < 0 || moved > (NSInteger)_text.length)
        return nil;
    return [TJTextPosition positionWithIndex:(NSUInteger)moved affinity:UITextStorageDirectionForward];
}

- (UITextPosition*)positionFromPosition:(UITextPosition*)position
                            inDirection:(UITextLayoutDirection)direction
                                 offset:(NSInteger)offset
{
    NSInteger index = TJIndexOf(position);
    if (index < 0)
        return nil;
    NSUInteger moved = TJClampIndex(index, _text.length);
    switch (direction)
    {
        case UITextLayoutDirectionLeft: moved = [self tj_index:moved movedByClusters:-offset]; break;
        case UITextLayoutDirectionRight: moved = [self tj_index:moved movedByClusters:offset]; break;
        case UITextLayoutDirectionUp: moved = [self tj_index:moved movedByLines:-offset]; break;
        case UITextLayoutDirectionDown: moved = [self tj_index:moved movedByLines:offset]; break;
    }
    return [TJTextPosition positionWithIndex:moved affinity:UITextStorageDirectionForward];
}

- (NSUInteger)tj_index:(NSUInteger)index movedByClusters:(NSInteger)count
{
    for (; count > 0 && index < _text.length; count--)
        index = TJNextStop(_text, index);
    for (; count < 0 && index > 0; count++)
        index = TJPreviousStop(_text, index);
    return index;
}

// `index` moved up (negative `count`) or down by lines of the text, keeping its column in clusters, and to the start or
// end of the text past the first or last line.
- (NSUInteger)tj_index:(NSUInteger)index movedByLines:(NSInteger)count
{
    NSString* text = _text;
    for (; count != 0; count += count > 0 ? -1 : 1)
    {
        NSUInteger start = TJLineStart(text, index);
        NSUInteger column = 0;
        for (NSUInteger i = start; i < index; i = TJNextStop(text, i))
            column++;

        NSUInteger lineStart, lineEnd;
        if (count < 0)
        {
            if (start == 0)
                return 0;
            lineEnd = start - 1;
            lineStart = TJLineStart(text, lineEnd);
        }
        else
        {
            NSUInteger end = TJLineEnd(text, index);
            if (end == text.length)
                return text.length;
            lineStart = end + 1;
            lineEnd = TJLineEnd(text, lineStart);
        }
        index = lineStart;
        for (; column > 0 && index < lineEnd; column--)
            index = MIN(TJNextStop(text, index), lineEnd);
    }
    return index;
}

- (NSComparisonResult)comparePosition:(UITextPosition*)position toPosition:(UITextPosition*)other
{
    NSInteger a = TJIndexOf(position);
    NSInteger b = TJIndexOf(other);
    if (a < b)
        return NSOrderedAscending;
    if (a > b)
        return NSOrderedDescending;
    if (a < 0)
        return NSOrderedSame;
    UITextStorageDirection affinityA = ((TJTextPosition*)position).affinity;
    UITextStorageDirection affinityB = ((TJTextPosition*)other).affinity;
    if (affinityA == affinityB)
        return NSOrderedSame;
    // At the same index, the end of one line comes before the start of the next.
    return affinityA == UITextStorageDirectionBackward ? NSOrderedAscending : NSOrderedDescending;
}

- (NSInteger)offsetFromPosition:(UITextPosition*)from toPosition:(UITextPosition*)toPosition
{
    NSInteger a = TJIndexOf(from);
    NSInteger b = TJIndexOf(toPosition);
    if (a < 0 || b < 0)
        return 0;
    return b - a;
}

- (id<UITextInputTokenizer>)tokenizer
{
    if (_tokenizer == nil)
        _tokenizer = [[TJTokenizer alloc] initWithView:self];
    return _tokenizer;
}

- (UITextPosition*)positionWithinRange:(UITextRange*)range farthestInDirection:(UITextLayoutDirection)direction
{
    NSRange textRange = TJRangeOf(range);
    if (!TJHasRange(textRange))
        return nil;
    BOOL backward = direction == UITextLayoutDirectionLeft || direction == UITextLayoutDirectionUp;
    return backward ? [TJTextPosition positionWithIndex:textRange.location affinity:UITextStorageDirectionForward]
                    : [TJTextPosition positionWithIndex:NSMaxRange(textRange) affinity:UITextStorageDirectionBackward];
}

- (UITextRange*)characterRangeByExtendingPosition:(UITextPosition*)position inDirection:(UITextLayoutDirection)direction
{
    NSInteger index = TJIndexOf(position);
    if (index < 0)
        return nil;
    NSUInteger at = TJClampIndex(index, _text.length);
    if (direction == UITextLayoutDirectionLeft || direction == UITextLayoutDirectionUp)
    {
        NSUInteger start = TJPreviousStop(_text, at);
        return [TJTextRange rangeWithNSRange:NSMakeRange(start, at - start)];
    }
    return [TJTextRange rangeWithNSRange:NSMakeRange(at, TJNextStop(_text, at) - at)];
}

- (NSWritingDirection)baseWritingDirectionForPosition:(UITextPosition*)position inDirection:(UITextStorageDirection)direction
{
    return NSWritingDirectionNatural;
}

- (void)setBaseWritingDirection:(NSWritingDirection)writingDirection forRange:(UITextRange*)range
{
}

#pragma mark UITextInput: geometry

// Only the caret's and the composing text's places are known (from C#, after each layout), so that is what is answered:
// the composing text for the IME's candidate window, the caret for everything else. A zero-width rect at the caret keeps
// UIKit's autocorrection highlight out of sight, as Flutter hides it where it cannot place it.

- (UIView*)textInputView
{
    return self;
}

- (CGRect)firstRectForRange:(UITextRange*)range
{
    if (TJHasRange(_marked) && _hasComposing)
        return _composing.size.width > 0 ? _composing : CGRectInset(_composing, -0.1, 0);
    if (!_hasGeometry)
        return TJHasRange(_marked) ? TJInvalidFirstRect : CGRectZero;
    return CGRectMake(CGRectGetMinX(_caret), CGRectGetMinY(_caret), 0, CGRectGetHeight(_caret));
}

- (CGRect)caretRectForPosition:(UITextPosition*)position
{
    return _hasGeometry ? _caret : CGRectZero;
}

- (NSArray<UITextSelectionRect*>*)selectionRectsForRange:(UITextRange*)range
{
    return @[];
}

- (UITextPosition*)closestPositionToPoint:(CGPoint)point
{
    return [TJTextPosition positionWithIndex:_selection.location affinity:UITextStorageDirectionForward];
}

- (UITextPosition*)closestPositionToPoint:(CGPoint)point withinRange:(UITextRange*)range
{
    NSUInteger index = _selection.location;
    NSRange within = TJRangeOf(range);
    if (TJHasRange(within))
        index = MIN(MAX(index, within.location), NSMaxRange(within));
    return [TJTextPosition positionWithIndex:index affinity:UITextStorageDirectionForward];
}

- (UITextRange*)characterRangeAtPoint:(CGPoint)point
{
    NSUInteger index = _selection.location;
    if (index >= _text.length)
        return [TJTextRange rangeWithNSRange:NSMakeRange(_text.length, 0)];
    return [TJTextRange rangeWithNSRange:[_text rangeOfComposedCharacterSequenceAtIndex:index]];
}

#pragma mark UITextInput: dictation

- (void)dictationRecordingDidEnd
{
    TJTIDictationRecordingDidEnd();
}

// UIKit asks for a placeholder to stand in the text while dictated speech is being recognised; there is none to show
// (the field draws the text), and the result comes through insertText:, as with Flutter.
- (id)insertDictationResultPlaceholder
{
    return @"";
}

- (void)removeDictationResultPlaceholder:(id)placeholder willInsertResult:(BOOL)willInsertResult
{
}

#pragma mark UIResponder

- (BOOL)canBecomeFirstResponder
{
    // Only while its session is under way, so that UIKit cannot hand focus back to a proxy whose field is done.
    return _active;
}

- (BOOL)resignFirstResponder
{
    BOOL resigned = [super resignFirstResponder];
    // Our own resigns come after the session is detached; any other is the user (the keyboard's dismiss key) or the
    // system (another responder, a presented controller) ending it.
    if (resigned && _active && !_endReported)
    {
        _endReported = YES;
        TJTIReportEnded(_session);
    }
    return resigned;
}

- (UIView*)hitTest:(CGPoint)point withEvent:(UIEvent*)event
{
    // Touches belong to the Unity view underneath, where the field handles them.
    return nil;
}

- (BOOL)scribbleInteraction:(UIScribbleInteraction*)interaction shouldBeginAtLocation:(CGPoint)location
{
    return NO;
}

- (void)didMoveToWindow
{
    [super didMoveToWindow];
    // The tokenizer holds this view; let it go with the view.
    if (self.window == nil)
        _tokenizer = nil;
}

// UIKit's private colours for its own caret and selection, which a text view with a real frame would otherwise show
// (FlutterTextInputPlugin.mm). On iOS 17 the insertion point colour also tints the IME's selected candidate, so there
// it is not overridden (flutter#132548).
- (UIColor*)insertionPointColor
{
    return UIColor.clearColor;
}

- (UIColor*)selectionBarColor
{
    return UIColor.clearColor;
}

- (UIColor*)selectionHighlightColor
{
    return UIColor.clearColor;
}

- (BOOL)respondsToSelector:(SEL)selector
{
    if (@available(iOS 17.0, *))
    {
        if (selector == @selector(insertionPointColor))
            return NO;
    }
    return [super respondsToSelector:selector];
}

#pragma mark Hardware keyboard

// Hardware keys taken ahead of UIKit's text system. Those that need the field's own layout or history go to C# as
// intents: a line up or down (keeping the field's goal x across wrapped lines), the start or end of a line as it wraps,
// undo and redo. A key typed in the same frame as one of these still goes into the mirror as it was, and is lost when
// C#'s answer to the intent replaces it. So what the mirror can do itself is done here: Cmd and Up or Down (the start or
// end of the text), and Shift and Return (a new line in a multi-line field whatever the return key does, as a chat
// composer wants; in a single line, the return key, as in UITextField). The rest (typing, left and right, option-arrows
// by word, the clipboard shortcuts) stay with UIKit's text system, which works them on the mirror. While something is
// being composed the IME has these keys.
- (NSArray<UIKeyCommand*>*)keyCommands
{
    if (!_active || TJHasRange(_marked))
        return nil;
    static NSArray<UIKeyCommand*>* commands;
    if (commands == nil)
    {
        NSMutableArray<UIKeyCommand*>* list = [NSMutableArray array];
        void (^add)(NSString*, UIKeyModifierFlags) = ^(NSString* input, UIKeyModifierFlags flags) {
            UIKeyCommand* command = [UIKeyCommand keyCommandWithInput:input modifierFlags:flags action:@selector(tj_keyCommand:)];
            // From iOS 15 the text system sees keys before key commands unless they ask for priority.
            command.wantsPriorityOverSystemBehavior = YES;
            [list addObject:command];
        };
        for (NSString* input in @[UIKeyInputUpArrow, UIKeyInputDownArrow])
        {
            add(input, 0);
            add(input, UIKeyModifierShift);
            add(input, UIKeyModifierCommand);
            add(input, UIKeyModifierCommand | UIKeyModifierShift);
        }
        for (NSString* input in @[UIKeyInputLeftArrow, UIKeyInputRightArrow])
        {
            add(input, UIKeyModifierCommand);
            add(input, UIKeyModifierCommand | UIKeyModifierShift);
        }
        add(@"z", UIKeyModifierCommand);
        add(@"z", UIKeyModifierCommand | UIKeyModifierShift);
        add(@"\r", UIKeyModifierShift);
        commands = [list copy];
    }
    return commands;
}

- (void)tj_keyCommand:(UIKeyCommand*)command
{
    if (!_active)
        return;
    NSString* input = command.input;
    BOOL shift = (command.modifierFlags & UIKeyModifierShift) != 0;
    BOOL commandKey = (command.modifierFlags & UIKeyModifierCommand) != 0;
    BOOL up = [input isEqualToString:UIKeyInputUpArrow];
    if (up || [input isEqualToString:UIKeyInputDownArrow])
    {
        if (!commandKey)
        {
            TJTIReportIntent(_session, up ? TJTIIntentMoveUp : TJTIIntentMoveDown, shift);
            return;
        }
        // The start or the end of the text; with shift, the selection reaches there from where it was made.
        NSUInteger to = up ? 0 : _text.length;
        NSUInteger base = !shift ? to : _selectionReversed ? NSMaxRange(_selection) : _selection.location;
        [self tj_change:_text selectionBase:base selectionExtent:to];
    }
    else if ([input isEqualToString:UIKeyInputLeftArrow])
        TJTIReportIntent(_session, TJTIIntentMoveLineStart, shift);
    else if ([input isEqualToString:UIKeyInputRightArrow])
        TJTIReportIntent(_session, TJTIIntentMoveLineEnd, shift);
    else if ([input isEqualToString:@"z"])
        TJTIReportIntent(_session, shift ? TJTIIntentRedo : TJTIIntentUndo, NO);
    else if ([input isEqualToString:@"\r"])
    {
        if (!_multiline)
        {
            [self tj_returnPressed];
            return;
        }
        NSUInteger caret = _selection.location + 1;
        [self tj_change:[_text stringByReplacingCharactersInRange:_selection withString:@"\n"]
          selectionBase:caret
        selectionExtent:caret];
    }
}

#pragma mark Edit menu

- (BOOL)tj_isMenuSender:(id)sender
{
    // iOS 15's menu controller asks what it can offer; iOS 16's edit menu is filtered in its delegate instead.
    if (@available(iOS 16.0, *))
        return NO;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    return [sender isKindOfClass:UIMenuController.class];
#pragma clang diagnostic pop
}

- (BOOL)tj_menuAllows:(int32_t)action sender:(id)sender
{
    return ![self tj_isMenuSender:sender] || (_menuActions & action) != 0;
}

- (BOOL)canPerformAction:(SEL)action withSender:(id)sender
{
    // Nothing leaves a secure field (Apple: secure entry disables copying).
    if (action == @selector(cut:))
        return _active && _selection.length > 0 && !self.isSecureTextEntry && [self tj_menuAllows:TJTIActionCut sender:sender];
    if (action == @selector(copy:))
        return _active && _selection.length > 0 && !self.isSecureTextEntry && [self tj_menuAllows:TJTIActionCopy sender:sender];
    if (action == @selector(paste:))
        return _active && [self tj_menuAllows:TJTIActionPaste sender:sender] && UIPasteboard.generalPasteboard.hasStrings;
    if (action == @selector(selectAll:))
        return _active && _text.length > 0 && _selection.length < _text.length && [self tj_menuAllows:TJTIActionSelectAll sender:sender];
    if ([self tj_isMenuSender:sender])
        return NO;
    return [super canPerformAction:action withSender:sender];
}

- (void)showEditMenuAt:(CGRect)target actions:(int32_t)actions
{
    if (!_active || !self.isFirstResponder)
        return;
    _menuTarget = target;
    _menuActions = actions;
    if (!(((actions & TJTIActionCut) && [self canPerformAction:@selector(cut:) withSender:nil])
          || ((actions & TJTIActionCopy) && [self canPerformAction:@selector(copy:) withSender:nil])
          || ((actions & TJTIActionPaste) && [self canPerformAction:@selector(paste:) withSender:nil])
          || ((actions & TJTIActionSelectAll) && [self canPerformAction:@selector(selectAll:) withSender:nil])))
        return;

    if (@available(iOS 16.0, *))
    {
        UIEditMenuInteraction* menu = _editMenu;
        if (menu == nil)
        {
            menu = [[UIEditMenuInteraction alloc] initWithDelegate:self];
            [self addInteraction:menu];
            _editMenu = menu;
        }
        CGPoint source = CGPointMake(CGRectGetMidX(target), CGRectGetMinY(target));
        [menu presentEditMenuWithConfiguration:[UIEditMenuConfiguration configurationWithIdentifier:nil sourcePoint:source]];
    }
    else
    {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        [UIMenuController.sharedMenuController showMenuFromView:self rect:target];
#pragma clang diagnostic pop
    }
}

- (void)hideEditMenu
{
    if (@available(iOS 16.0, *))
        [(UIEditMenuInteraction*)_editMenu dismissMenu];
    else
    {
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        [UIMenuController.sharedMenuController hideMenuFromView:self];
#pragma clang diagnostic pop
    }
}

// The menu is made of the system's own suggested commands for what the field asked for, never commands of our own:
// the system's Paste reads the pasteboard without the "Allow Paste" prompt (FlutterTextInputPlugin.mm).
- (UIMenu*)editMenuInteraction:(UIEditMenuInteraction*)interaction
          menuForConfiguration:(UIEditMenuConfiguration*)configuration
              suggestedActions:(NSArray<UIMenuElement*>*)suggestedActions API_AVAILABLE(ios(16.0))
{
    NSMutableArray<UIMenuElement*>* items = [NSMutableArray array];
    UICommand* command;
    if ((_menuActions & TJTIActionCut) && (command = TJFindCommand(suggestedActions, @selector(cut:))))
        [items addObject:command];
    if ((_menuActions & TJTIActionCopy) && (command = TJFindCommand(suggestedActions, @selector(copy:))))
        [items addObject:command];
    if ((_menuActions & TJTIActionPaste) && (command = TJFindCommand(suggestedActions, @selector(paste:))))
        [items addObject:command];
    if ((_menuActions & TJTIActionSelectAll) && (command = TJFindCommand(suggestedActions, @selector(selectAll:))))
        [items addObject:command];
    return [UIMenu menuWithChildren:items];
}

- (CGRect)editMenuInteraction:(UIEditMenuInteraction*)interaction
   targetRectForConfiguration:(UIEditMenuConfiguration*)configuration API_AVAILABLE(ios(16.0))
{
    return _menuTarget;
}

// The edit menu's commands, and the hardware keyboard's clipboard shortcuts, worked on the mirror and reported as one
// edit. The pasteboard is read only here, when the user asked to paste.

- (void)cut:(id)sender
{
    if (!_active || _selection.length == 0 || self.isSecureTextEntry)
        return;
    UIPasteboard.generalPasteboard.string = [_text substringWithRange:_selection];
    [self tj_beginEdit];
    [self tj_replace:_selection with:@""];
    [self tj_endEdit];
}

- (void)copy:(id)sender
{
    if (!_active || _selection.length == 0 || self.isSecureTextEntry)
        return;
    UIPasteboard.generalPasteboard.string = [_text substringWithRange:_selection];
}

- (void)paste:(id)sender
{
    if (!_active)
        return;
    NSString* pasted = UIPasteboard.generalPasteboard.string;
    if (pasted.length == 0)
        return;
    [self tj_beginEdit];
    // The paste replaces the selection, and what was being composed around it is committed.
    [self tj_replace:_selection with:pasted];
    [self tj_endEdit];
}

- (void)selectAll:(id)sender
{
    if (!_active || _text.length == 0)
        return;
    NSRange all = NSMakeRange(0, _text.length);
    if (NSEqualRanges(all, _selection))
        return;
    [self tj_beginEdit];
    _selection = all;
    _selectionReversed = NO;
    _edited = YES;
    [self tj_endEdit];
}

@end
