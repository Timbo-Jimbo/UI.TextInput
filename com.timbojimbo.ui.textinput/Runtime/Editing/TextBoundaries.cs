using System;
using System.Globalization;

namespace TimboJimbo.UI.TextInput
{
    /// <summary>
    /// Where the caret may stop in a text, what backspace takes, and what a word and a paragraph are, so that the caret
    /// moves, deletes and selects by what the reader sees rather than by UTF-16 code unit. Indices are UTF-16 code units,
    /// as everywhere in text input; an index outside the text is taken as its nearer end. Nothing here allocates.
    /// <para>
    /// The caret stops between grapheme clusters, what a reader takes for one character: a letter with its accents
    /// (combining marks), an emoji with its skin tone, its variation selector or the emoji it is joined to by zero width
    /// joiners, a flag (a pair of regional indicators, or a black flag with tag characters), a keycap, a Korean syllable
    /// spelt in jamo, and CR LF. These are Unicode's extended grapheme clusters (UAX #29) short of its two rarest rules: a
    /// prepended concatenation mark (an Arabic number sign) stays apart from the digits after it, and an Indic conjunct
    /// across a virama is two clusters, as it was everywhere before Unicode 15.1. The character properties come from
    /// .NET's tables, with the few the rules need that those lack (emoji, and the marks that do or do not join) spelt out
    /// here.
    /// </para>
    /// </summary>
    public static class TextBoundaries
    {
        // A code point's part in grapheme clusters: UAX #29's Grapheme_Cluster_Break, short of Prepend.
        private enum Cluster : byte
        {
            Other,
            CR,
            LF,
            Control,
            Extend,
            ZWJ,
            SpacingMark,
            RegionalIndicator,
            Pictographic,
            L,
            V,
            T,
            LV,
            LVT,
        }

        // A grapheme cluster's part in words, by its first code point.
        private enum WordPart : byte
        {
            // Letters, marks, connector punctuation (_) and numbers other than digits: runs of them and digits are words.
            Letter,
            Digit,
            // Runs of katakana are words, apart from the letters around them.
            Katakana,
            // CJK ideographs, hiragana and emoji: each is a word on its own (there is no dictionary to group them by).
            Single,
            Space,
            // Each line break is a run on its own, so that nothing found here reaches across lines.
            LineBreak,
            // Punctuation and symbols: runs of them stand between words.
            Punctuation,
        }

        // Unicode's Extended_Pictographic code points (Unicode 16): what emoji, and the sequences built on them, start with.
        // First and last of each range, in order.
        private static readonly int[] s_pictographic =
        {
            0x00A9, 0x00A9, 0x00AE, 0x00AE, 0x203C, 0x203C, 0x2049, 0x2049, 0x2122, 0x2122, 0x2139, 0x2139,
            0x2194, 0x2199, 0x21A9, 0x21AA, 0x231A, 0x231B, 0x2328, 0x2328, 0x2388, 0x2388, 0x23CF, 0x23CF,
            0x23E9, 0x23F3, 0x23F8, 0x23FA, 0x24C2, 0x24C2, 0x25AA, 0x25AB, 0x25B6, 0x25B6, 0x25C0, 0x25C0,
            0x25FB, 0x25FE, 0x2600, 0x2605, 0x2607, 0x2612, 0x2614, 0x2685, 0x2690, 0x2705, 0x2708, 0x2712,
            0x2714, 0x2714, 0x2716, 0x2716, 0x271D, 0x271D, 0x2721, 0x2721, 0x2728, 0x2728, 0x2733, 0x2734,
            0x2744, 0x2744, 0x2747, 0x2747, 0x274C, 0x274C, 0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757,
            0x2763, 0x2767, 0x2795, 0x2797, 0x27A1, 0x27A1, 0x27B0, 0x27B0, 0x27BF, 0x27BF, 0x2934, 0x2935,
            0x2B05, 0x2B07, 0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55, 0x3030, 0x3030, 0x303D, 0x303D,
            0x3297, 0x3297, 0x3299, 0x3299, 0x1F000, 0x1F0FF, 0x1F10D, 0x1F10F, 0x1F12F, 0x1F12F, 0x1F16C, 0x1F171,
            0x1F17E, 0x1F17F, 0x1F18E, 0x1F18E, 0x1F191, 0x1F19A, 0x1F1AD, 0x1F1E5, 0x1F201, 0x1F20F, 0x1F21A, 0x1F21A,
            0x1F22F, 0x1F22F, 0x1F232, 0x1F23A, 0x1F23C, 0x1F23F, 0x1F249, 0x1F3FA, 0x1F400, 0x1F53D, 0x1F546, 0x1F64F,
            0x1F680, 0x1F6FF, 0x1F774, 0x1F77F, 0x1F7D5, 0x1F7FF, 0x1F80C, 0x1F80F, 0x1F848, 0x1F84F, 0x1F85A, 0x1F85F,
            0x1F888, 0x1F88F, 0x1F8AE, 0x1F8FF, 0x1F90C, 0x1F93A, 0x1F93C, 0x1F945, 0x1F947, 0x1FAFF, 0x1FC00, 0x1FFFD,
        };

        /// <summary>
        /// The end of the grapheme cluster at <paramref name="index"/>: where the caret goes when moved one character on
        /// (the right arrow in left-to-right text). The text's length at its end.
        /// </summary>
        public static int NextCaretStop(string text, int index)
        {
            int length = text.Length;
            int i = Math.Max(index, 0);
            if (i >= length) return length;
            i += CodePointLength(text, i);
            while (i < length && !IsClusterBoundary(text, i)) i += CodePointLength(text, i);
            return i;
        }

        /// <summary>
        /// The start of the grapheme cluster before <paramref name="index"/> (the one it is inside, if it is inside one):
        /// where the caret goes when moved one character back. 0 at the start.
        /// </summary>
        public static int PreviousCaretStop(string text, int index)
        {
            index = Math.Min(index, text.Length);
            if (index <= 0) return 0;
            int i = CodePointStartBefore(text, index);
            while (i > 0 && !IsClusterBoundary(text, i)) i = CodePointStartBefore(text, i);
            return i;
        }

        /// <summary>
        /// <paramref name="index"/>, moved back to the start of the grapheme cluster it is inside if it is inside one: a
        /// caret the platform placed exactly (in UTF-16 units) where ours may stand.
        /// </summary>
        public static int SnapToCaretStop(string text, int index)
        {
            if (index <= 0) return 0;
            if (index >= text.Length) return text.Length;
            return IsClusterBoundary(text, index) ? index : PreviousCaretStop(text, index);
        }

        /// <summary>
        /// Where backspace at a caret at <paramref name="index"/> deletes from, by the rule UIKit's text views and Flutter
        /// follow: the whole cluster before the caret if it is an emoji (a flag, a keycap, a sequence of emoji joined by
        /// zero width joiners, an emoji with a skin tone or a variation selector), and CR LF together; otherwise only its
        /// last code point, so that a Thai or Indic vowel mark goes on its own, leaving the letter it was on, and a Korean
        /// syllable spelt in jamo loses its last jamo. A surrogate pair is never split.
        /// </summary>
        public static int DeleteBackwardStart(string text, int index)
        {
            index = Math.Min(index, text.Length);
            if (index <= 0) return 0;
            int start = PreviousCaretStop(text, index);
            return DeletesWhole(text, start, index) ? start : CodePointStartBefore(text, index);
        }

        /// <summary>
        /// The word at <paramref name="index"/>, or just before it (when the index is at the end of a word, of a line or of
        /// the text): what a double tap or a double click selects. A word is a run of letters, digits and connector
        /// punctuation (with an apostrophe or full stop between letters, as in don't and example.com, or a full stop or
        /// comma between digits, as in 3.14 and 1,000), a run of katakana, or a single CJK ideograph, hiragana or emoji.
        /// Away from a word it is the run of spaces or of punctuation there, or the line break. Empty for an empty text.
        /// </summary>
        public static TextRange WordAt(string text, int index)
        {
            int length = text.Length;
            if (length == 0) return TextRange.Collapsed(0);
            index = SnapToCaretStop(text, index);

            // The cluster whose run it is: the one at the index, or the one before it when the index ends the text, a word
            // or the last thing on a line.
            int at = index;
            if (index == length)
            {
                at = PreviousCaretStop(text, index);
            }
            else if (index > 0)
            {
                int before = PreviousCaretStop(text, index);
                var here = WordPartOf(text, index);
                var previous = WordPartOf(text, before);
                if ((!IsInWord(here) && IsInWord(previous)) || (here == WordPart.LineBreak && previous != WordPart.LineBreak))
                    at = before;
            }

            return new TextRange(RunStart(text, at), RunEnd(text, at));
        }

        /// <summary>
        /// The end of the word at or after <paramref name="index"/>, past any spaces and punctuation before it (the text's
        /// length when no word follows): where ctrl and the right arrow (option on a Mac) take the caret, and what
        /// deleting a word forwards deletes up to.
        /// </summary>
        public static int NextWordEnd(string text, int index)
        {
            int length = text.Length;
            index = SnapToCaretStop(text, index);
            while (index < length && !IsInWord(WordPartOf(text, index))) index = NextCaretStop(text, index);
            return index < length ? RunEnd(text, index) : length;
        }

        /// <summary>
        /// The start of the word at or before <paramref name="index"/>, back past any spaces and punctuation after it (0
        /// when no word comes before): where ctrl and the left arrow (option on a Mac) take the caret, and what deleting a
        /// word backwards deletes from.
        /// </summary>
        public static int PreviousWordStart(string text, int index)
        {
            index = SnapToCaretStop(text, index);
            while (index > 0)
            {
                int before = PreviousCaretStop(text, index);
                if (IsInWord(WordPartOf(text, before))) return RunStart(text, before);
                index = before;
            }
            return 0;
        }

        /// <summary>
        /// The paragraph <paramref name="index"/> is in: the text between the line feeds either side of it (a CR before
        /// the line feed left out too), what a triple click selects. A caret just before a line feed is in the paragraph
        /// the line feed ends; one just after it, in the next.
        /// </summary>
        public static TextRange ParagraphAt(string text, int index)
        {
            index = SnapToCaretStop(text, index);
            int start = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
            int end = text.IndexOf('\n', index);
            if (end < 0) end = text.Length;
            else if (end > start && text[end - 1] == '\r') end--;
            return new TextRange(start, end);
        }

        // Whether a grapheme cluster ends at `index`, which is inside the text (not at either end): UAX #29's rules in order.
        private static bool IsClusterBoundary(string text, int index)
        {
            // Never between the halves of a surrogate pair.
            if (char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1])) return false;

            int beforeStart = CodePointStartBefore(text, index);
            var before = ClusterOf(text, beforeStart);
            var after = ClusterOf(text, index);

            // GB3-GB5: CR LF stays together; any other control stands alone.
            if (before == Cluster.CR) return after != Cluster.LF;
            if (before is Cluster.LF or Cluster.Control) return true;
            if (after is Cluster.CR or Cluster.LF or Cluster.Control) return true;

            // GB6-GB8: Korean syllables spelt in jamo (a leading consonant, a vowel, a trailing consonant).
            if (before == Cluster.L && after is Cluster.L or Cluster.V or Cluster.LV or Cluster.LVT) return false;
            if (before is Cluster.LV or Cluster.V && after is Cluster.V or Cluster.T) return false;
            if (before is Cluster.LVT or Cluster.T && after == Cluster.T) return false;

            // GB9, GB9a: marks and joiners stay with what they follow.
            if (after is Cluster.Extend or Cluster.ZWJ or Cluster.SpacingMark) return false;

            // GB11: an emoji joined by a zero width joiner to the emoji (with its marks) before it.
            if (before == Cluster.ZWJ && after == Cluster.Pictographic) return !FollowsPictograph(text, beforeStart);

            // GB12, GB13: regional indicators pair up into flags, from the first of a run.
            if (before == Cluster.RegionalIndicator && after == Cluster.RegionalIndicator)
                return RegionalIndicatorsBefore(text, index) % 2 == 0;

            // GB999.
            return true;
        }

        // Whether the zero width joiner at `joiner` follows an emoji, with only marks (skin tones, variation selectors)
        // between them.
        private static bool FollowsPictograph(string text, int joiner)
        {
            for (int i = joiner; i > 0;)
            {
                i = CodePointStartBefore(text, i);
                var cluster = ClusterOf(text, i);
                if (cluster == Cluster.Pictographic) return true;
                if (cluster != Cluster.Extend) return false;
            }
            return false;
        }

        // How many regional indicators run back from `index`.
        private static int RegionalIndicatorsBefore(string text, int index)
        {
            int count = 0;
            while (index > 0)
            {
                index = CodePointStartBefore(text, index);
                if (ClusterOf(text, index) != Cluster.RegionalIndicator) break;
                count++;
            }
            return count;
        }

        // Whether backspace takes the whole of the cluster from `start` up to `end` (the caret): an emoji or CR LF.
        private static bool DeletesWhole(string text, int start, int end)
        {
            if (ClusterOf(text, start) is Cluster.Pictographic or Cluster.RegionalIndicator or Cluster.CR) return true;
            for (int i = start; i < end; i += CodePointLength(text, i))
            {
                // A keycap (a digit, # or * with U+20E3), an emoji presentation selector, a skin tone.
                int c = CodePointAt(text, i);
                if (c == 0x20E3 || c == 0xFE0F || IsEmojiModifier(c)) return true;
            }
            return false;
        }

        private static Cluster ClusterOf(string text, int index)
        {
            int c = CodePointAt(text, index);
            if (c < 0x7F) return c == '\r' ? Cluster.CR : c == '\n' ? Cluster.LF : c < 0x20 ? Cluster.Control : Cluster.Other;

            switch (c)
            {
                case 0x200C: // zero width non-joiner
                    return Cluster.Extend;
                case 0x200D:
                    return Cluster.ZWJ;
                case 0x0E33: // Thai and Lao sara am: letters by category, but they combine
                case 0x0EB3:
                    return Cluster.SpacingMark;
            }

            if (c >= 0x1100 && c <= 0x11FF) return c < 0x1160 ? Cluster.L : c < 0x11A8 ? Cluster.V : Cluster.T;
            if (c >= 0xA960 && c <= 0xA97C) return Cluster.L;
            if (c >= 0xD7B0 && c <= 0xD7C6) return Cluster.V;
            if (c >= 0xD7CB && c <= 0xD7FB) return Cluster.T;
            if (c >= 0xAC00 && c <= 0xD7A3) return (c - 0xAC00) % 28 == 0 ? Cluster.LV : Cluster.LVT;
            if (c >= 0x1F1E6 && c <= 0x1F1FF) return Cluster.RegionalIndicator;

            // Marks .NET's categories leave out or file elsewhere: variation selectors, skin tones, tag characters (which
            // spell out subdivision flags) and the halfwidth katakana sound marks.
            if (IsVariationSelector(c) || IsEmojiModifier(c) || (c >= 0xE0020 && c <= 0xE007F) || c == 0xFF9E || c == 0xFF9F)
                return Cluster.Extend;
            if (IsExtendedPictographic(c)) return Cluster.Pictographic;

            switch (CharUnicodeInfo.GetUnicodeCategory(text, index))
            {
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                    return Cluster.Extend;
                case UnicodeCategory.SpacingCombiningMark:
                    return IsSeparateVowelSign(c) ? Cluster.Other : Cluster.SpacingMark;
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    return Cluster.Control;
                default:
                    return Cluster.Other;
            }
        }

        // The spacing marks UAX #29 keeps apart from the letter before them (Myanmar, Tai Tham, Tai Viet and Ahom vowel
        // signs), which are clusters of their own.
        private static bool IsSeparateVowelSign(int c) =>
            (c >= 0x102B && c <= 0x102C) || c == 0x1038 || (c >= 0x1062 && c <= 0x1064) || (c >= 0x1067 && c <= 0x106D) ||
            c == 0x1083 || (c >= 0x1087 && c <= 0x108C) || c == 0x108F || (c >= 0x109A && c <= 0x109C) ||
            c == 0x1A61 || (c >= 0x1A63 && c <= 0x1A64) || c == 0xAA7B || c == 0xAA7D || (c >= 0x11720 && c <= 0x11721);

        private static bool IsVariationSelector(int c) => (c >= 0xFE00 && c <= 0xFE0F) || (c >= 0xE0100 && c <= 0xE01EF);

        private static bool IsEmojiModifier(int c) => c >= 0x1F3FB && c <= 0x1F3FF;

        private static bool IsExtendedPictographic(int c)
        {
            if (c < 0x00A9 || c > 0x1FFFD) return false;
            int low = 0, high = s_pictographic.Length / 2 - 1;
            while (low <= high)
            {
                int middle = (low + high) >> 1;
                if (c < s_pictographic[middle * 2]) high = middle - 1;
                else if (c > s_pictographic[middle * 2 + 1]) low = middle + 1;
                else return true;
            }
            return false;
        }

        private static bool IsInWord(WordPart part) =>
            part is WordPart.Letter or WordPart.Digit or WordPart.Katakana or WordPart.Single;

        // The start of the run the cluster at `index` belongs to: its word, or its run of spaces or punctuation.
        private static int RunStart(string text, int index)
        {
            var part = WordPartOf(text, index);
            if (part is WordPart.Single or WordPart.LineBreak) return index;
            while (index > 0)
            {
                int before = PreviousCaretStop(text, index);
                if (!ContinuesRun(part, text, before)) break;
                index = before;
            }
            return index;
        }

        // The end of the run the cluster at `index` belongs to.
        private static int RunEnd(string text, int index)
        {
            var part = WordPartOf(text, index);
            index = NextCaretStop(text, index);
            if (part is WordPart.Single or WordPart.LineBreak) return index;
            while (index < text.Length && ContinuesRun(part, text, index)) index = NextCaretStop(text, index);
            return index;
        }

        // Whether the cluster at `index`, next to a run of `part`, is part of the same run.
        private static bool ContinuesRun(WordPart part, string text, int index)
        {
            var other = WordPartOf(text, index);
            if (part is WordPart.Letter or WordPart.Digit)
                return other is WordPart.Letter or WordPart.Digit || (other == WordPart.Punctuation && JoinsWord(text, index));
            return other == part;
        }

        // Whether the punctuation at `index` joins the word characters either side of it into one word, as UAX #29's
        // MidLetter, MidNumLet and MidNum do: an apostrophe or full stop between letters (don't, example.com) or digits
        // (3.14), a middle dot between letters (Catalan l·l), a comma between digits (1,000).
        private static bool JoinsWord(string text, int index)
        {
            if (index == 0) return false;
            int after = NextCaretStop(text, index);
            if (after >= text.Length) return false;

            bool betweenLetters, betweenDigits;
            switch (CodePointAt(text, index))
            {
                case '\'':
                case '.':
                case 0x2018: // single quotation marks, the right one being the typographic apostrophe
                case 0x2019:
                case 0x2024: // one dot leader
                case 0xFE52: // small and fullwidth forms
                case 0xFF07:
                case 0xFF0E:
                    betweenLetters = betweenDigits = true;
                    break;
                case 0x00B7: // middle dots
                case 0x0387:
                case 0x05F4: // Hebrew gershayim
                case 0x2027: // hyphenation point
                    betweenLetters = true;
                    betweenDigits = false;
                    break;
                case ',':
                case ';':
                case 0x037E: // Greek question mark
                case 0x060C: // Arabic comma, date separator and thousands separator
                case 0x060D:
                case 0x066C:
                case 0xFE50: // small and fullwidth forms
                case 0xFE54:
                case 0xFF0C:
                case 0xFF1B:
                    betweenLetters = false;
                    betweenDigits = true;
                    break;
                default:
                    return false;
            }

            var left = WordPartOf(text, PreviousCaretStop(text, index));
            var right = WordPartOf(text, after);
            if (left != right) return false;
            return left == WordPart.Letter ? betweenLetters : left == WordPart.Digit && betweenDigits;
        }

        private static WordPart WordPartOf(string text, int index)
        {
            int c = CodePointAt(text, index);
            switch (c)
            {
                case '\n':
                case '\r':
                case 0x0B:
                case 0x0C:
                case 0x85:
                case 0x2028:
                case 0x2029:
                    return WordPart.LineBreak;
                case '\t':
                    return WordPart.Space;
            }

            if (IsExtendedPictographic(c) || (c >= 0x1F1E6 && c <= 0x1F1FF) || IsIdeographOrHiragana(c)) return WordPart.Single;
            if (IsKatakana(c)) return WordPart.Katakana;

            switch (CharUnicodeInfo.GetUnicodeCategory(text, index))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.ConnectorPunctuation:
                case UnicodeCategory.LetterNumber:
                case UnicodeCategory.OtherNumber:
                    return WordPart.Letter;
                case UnicodeCategory.DecimalDigitNumber:
                    return WordPart.Digit;
                case UnicodeCategory.SpaceSeparator:
                    return WordPart.Space;
                default:
                    return WordPart.Punctuation;
            }
        }

        // CJK ideographs (with the iteration and numeral marks that go with them) and hiragana: UAX #29 makes each a word
        // of its own, as there is no dictionary here to group them by.
        private static bool IsIdeographOrHiragana(int c) =>
            (c >= 0x3005 && c <= 0x3007) || (c >= 0x3021 && c <= 0x3029) || (c >= 0x3038 && c <= 0x303B) ||
            (c >= 0x3041 && c <= 0x3096) || (c >= 0x309D && c <= 0x309F) || (c >= 0x3400 && c <= 0x4DBF) ||
            (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0x20000 && c <= 0x3FFFF);

        private static bool IsKatakana(int c) =>
            (c >= 0x3031 && c <= 0x3035) || (c >= 0x309B && c <= 0x309C) || (c >= 0x30A0 && c <= 0x30FA) ||
            (c >= 0x30FC && c <= 0x30FF) || (c >= 0x31F0 && c <= 0x31FF) || (c >= 0x32D0 && c <= 0x32FE) ||
            (c >= 0x3300 && c <= 0x3357) || (c >= 0xFF66 && c <= 0xFF9F);

        // The code point starting at `index`; a surrogate without its other half stands for itself.
        private static int CodePointAt(string text, int index)
        {
            char c = text[index];
            return char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])
                ? char.ConvertToUtf32(c, text[index + 1])
                : c;
        }

        private static int CodePointLength(string text, int index) =>
            char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;

        // Where the code point ending at `index` (above 0) starts.
        private static int CodePointStartBefore(string text, int index) =>
            index >= 2 && char.IsLowSurrogate(text[index - 1]) && char.IsHighSurrogate(text[index - 2]) ? index - 2 : index - 1;
    }
}
