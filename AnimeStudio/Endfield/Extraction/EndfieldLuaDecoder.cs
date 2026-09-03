using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AnimeStudio.Endfield
{
    internal enum EndfieldLuaWrapperVariant
    {
        Base64Xxtea,
        PlainUtf8,
    }

    internal sealed class EndfieldLuaDecodeResult
    {
        public EndfieldLuaWrapperVariant WrapperVariant { get; init; }
        public byte[] SourceBytes { get; init; } = Array.Empty<byte>();
        public byte[] CipherBytes { get; init; } = Array.Empty<byte>();
        public byte[] DecodedBytes { get; init; } = Array.Empty<byte>();
        public string SourceSha256 { get; init; } = string.Empty;
        public string CipherSha256 { get; init; } = string.Empty;
        public string DecodedSha256 { get; init; } = string.Empty;
        public EndfieldLuaLexicalIndex LexicalIndex { get; init; }
        public string TerminalStatus { get; init; } = string.Empty;
    }

    internal sealed class EndfieldLuaLexicalIndex
    {
        public bool IsValid { get; init; }
        public string DiagnosticCode { get; init; } = string.Empty;
        public int DiagnosticOffset { get; init; } = -1;
        public int TokenCount { get; init; }
        public int IdentifierCount { get; init; }
        public int CallCount { get; init; }
        public int StringCount { get; init; }
        public IReadOnlyList<string> Strings { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Calls { get; init; } = Array.Empty<string>();
    }

    internal sealed class EndfieldLuaDecodeException : FormatException
    {
        public EndfieldLuaDecodeException(string code, int offset, string detail)
            : base($"{code} at offset {offset}: {detail}")
        {
            Code = code;
            Offset = offset;
        }

        public string Code { get; }
        public int Offset { get; }
    }

    /// <summary>
    /// Strictly recognizes the currently observed Lua VFS wrappers.  It does
    /// not infer runtime behaviour from source text; its lexical pass only
    /// indexes static strings/calls and reports bounded lexical failures.
    /// </summary>
    internal static class EndfieldLuaDecoder
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public static EndfieldLuaDecodeResult Decode(byte[] source, string fileName)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Lua VFS file name is required.", nameof(fileName));
            }

            var sourceText = DecodeUtf8(source, "lua.source.invalid_utf8");
            var sourceSha256 = Convert.ToHexString(SHA256.HashData(source));
            var isLuaName = fileName.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".lua.enc", StringComparison.OrdinalIgnoreCase);

            if (!TryDecodeStrictBase64(sourceText, out var cipher, out var base64Code, out var base64Offset, out var base64Detail))
            {
                // The only non-Lua wrapper observed in the current Lua VFS
                // block is a UTF-8 Markdown file.  A .lua path must never be
                // silently reclassified as plaintext after a bad wrapper.
                if (isLuaName)
                {
                    throw new EndfieldLuaDecodeException(base64Code, base64Offset, base64Detail);
                }

                if (!IsPlainText(source))
                {
                    throw new EndfieldLuaDecodeException("lua.plain.invalid_text", 0, "plain wrapper contains a control byte");
                }
                return new EndfieldLuaDecodeResult
                {
                    WrapperVariant = EndfieldLuaWrapperVariant.PlainUtf8,
                    SourceBytes = (byte[])source.Clone(),
                    DecodedBytes = (byte[])source.Clone(),
                    SourceSha256 = sourceSha256,
                    DecodedSha256 = sourceSha256,
                    LexicalIndex = null,
                    TerminalStatus = "plain_utf8_non_lua",
                };
            }

            var decoded = EndfieldXxtea.DecryptStrict(cipher, EndfieldVfsKeys.XxteaKey);
            var decodedText = DecodeUtf8(decoded, "lua.decoded.invalid_utf8");
            var lexicalIndex = EndfieldLuaLexicalScanner.Scan(decodedText);
            if (!lexicalIndex.IsValid)
            {
                throw new EndfieldLuaDecodeException(
                    lexicalIndex.DiagnosticCode,
                    lexicalIndex.DiagnosticOffset,
                    "decoded payload failed the bounded Lua lexical gate");
            }

            return new EndfieldLuaDecodeResult
            {
                WrapperVariant = EndfieldLuaWrapperVariant.Base64Xxtea,
                SourceBytes = (byte[])source.Clone(),
                CipherBytes = cipher,
                DecodedBytes = decoded,
                SourceSha256 = sourceSha256,
                CipherSha256 = Convert.ToHexString(SHA256.HashData(cipher)),
                DecodedSha256 = Convert.ToHexString(SHA256.HashData(decoded)),
                LexicalIndex = lexicalIndex,
                TerminalStatus = "decoded_lua_lexical_index",
            };
        }

        private static string DecodeUtf8(byte[] bytes, string code)
        {
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException e)
            {
                throw new EndfieldLuaDecodeException(code, e.Index, e.Message);
            }
        }

        private static bool IsPlainText(byte[] bytes)
        {
            foreach (var value in bytes)
            {
                if ((value < 0x20 && value is not (0x09 or 0x0A or 0x0D)) || value == 0x7F)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryDecodeStrictBase64(
            string text,
            out byte[] bytes,
            out string code,
            out int offset,
            out string detail)
        {
            bytes = Array.Empty<byte>();
            code = "lua.wrapper.base64.invalid";
            offset = 0;
            detail = "payload is not canonical standard Base64";

            if (text.Length == 0)
            {
                code = "lua.wrapper.base64.empty";
                detail = "empty wrapper";
                return false;
            }
            if ((text.Length & 3) != 0)
            {
                code = "lua.wrapper.base64.length";
                detail = $"length {text.Length} is not a multiple of four";
                offset = text.Length;
                return false;
            }

            var padding = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '=')
                {
                    padding++;
                    if (i < text.Length - 2 || padding > 2)
                    {
                        code = "lua.wrapper.base64.padding";
                        detail = "padding is not confined to the final Base64 quantum";
                        offset = i;
                        return false;
                    }
                    continue;
                }
                if (padding != 0 || !IsBase64Character(ch))
                {
                    code = "lua.wrapper.base64.character";
                    detail = $"invalid character U+{(int)ch:X4}";
                    offset = i;
                    return false;
                }
            }

            if (padding == 1 && text[^1] != '=')
            {
                code = "lua.wrapper.base64.padding";
                detail = "one padding byte was not final";
                offset = text.Length - 1;
                return false;
            }
            if (padding == 2 && (text[^1] != '=' || text[^2] != '='))
            {
                code = "lua.wrapper.base64.padding";
                detail = "two padding bytes were not final";
                offset = text.Length - 2;
                return false;
            }

            var lastQuantum = text.Length - 4;
            if (padding == 1 && Base64Value(text[lastQuantum + 2]) is var third && (third & 3) != 0)
            {
                code = "lua.wrapper.base64.nonzero_tail";
                detail = "unused trailing bits are non-zero";
                offset = lastQuantum + 2;
                return false;
            }
            if (padding == 2 && Base64Value(text[lastQuantum + 1]) is var second && (second & 15) != 0)
            {
                code = "lua.wrapper.base64.nonzero_tail";
                detail = "unused trailing bits are non-zero";
                offset = lastQuantum + 1;
                return false;
            }

            try
            {
                bytes = Convert.FromBase64String(text);
                if (bytes.Length < 8 || (bytes.Length & 3) != 0)
                {
                    code = "lua.wrapper.xxtea.frame";
                    detail = $"ciphertext length {bytes.Length} is not a complete XXTEA frame";
                    offset = bytes.Length;
                    bytes = Array.Empty<byte>();
                    return false;
                }
                return true;
            }
            catch (FormatException e)
            {
                code = "lua.wrapper.base64.invalid";
                detail = e.Message;
                return false;
            }
        }

        private static bool IsBase64Character(char value) =>
            value is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '+' or '/';

        private static int Base64Value(char value) => value switch
        {
            >= 'A' and <= 'Z' => value - 'A',
            >= 'a' and <= 'z' => value - 'a' + 26,
            >= '0' and <= '9' => value - '0' + 52,
            '+' => 62,
            '/' => 63,
            _ => -1,
        };
    }

    internal static class EndfieldLuaLexicalScanner
    {
        public static EndfieldLuaLexicalIndex Scan(string text)
        {
            var strings = new List<string>();
            var calls = new List<string>();
            var delimiters = new Stack<(char Open, int Offset)>();
            var tokenCount = 0;
            var identifierCount = 0;
            var callCount = 0;
            string previousIdentifier = null;
            var i = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;

            while (i < text.Length)
            {
                var ch = text[i];
                if (char.IsWhiteSpace(ch))
                {
                    i++;
                    continue;
                }
                if (ch == '\0' || (ch < 0x20 && ch is not ('\t' or '\n' or '\r')) || ch == '\u007F')
                {
                    return Invalid("lua.lexical.control_byte", i);
                }
                if (ch == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    i += 2;
                    if (i < text.Length && text[i] == '[' && TryLongBracketStart(text, i, out var equals, out _))
                    {
                        var contentStart = i + equals + 2;
                        var end = FindLongBracketEnd(text, contentStart, equals);
                        if (end < 0)
                        {
                            return Invalid("lua.lexical.unterminated_long_comment", i);
                        }
                        i = end + equals + 2;
                    }
                    else
                    {
                        while (i < text.Length && text[i] is not ('\r' or '\n'))
                        {
                            i++;
                        }
                    }
                    previousIdentifier = null;
                    continue;
                }
                if (ch is '\'' or '"')
                {
                    var quote = ch;
                    var start = ++i;
                    var value = new StringBuilder();
                    var closed = false;
                    while (i < text.Length)
                    {
                        ch = text[i];
                        if (ch == quote)
                        {
                            closed = true;
                            i++;
                            break;
                        }
                        if (ch is '\r' or '\n')
                        {
                            return Invalid("lua.lexical.unterminated_string", start - 1);
                        }
                        if (ch == '\\')
                        {
                            if (++i >= text.Length)
                            {
                                return Invalid("lua.lexical.unterminated_escape", i - 1);
                            }
                            value.Append('\\').Append(text[i++]);
                            continue;
                        }
                        value.Append(ch);
                        i++;
                    }
                    if (!closed)
                    {
                        return Invalid("lua.lexical.unterminated_string", start - 1);
                    }
                    strings.Add(value.ToString());
                    tokenCount++;
                    previousIdentifier = null;
                    continue;
                }
                if (ch == '[' && TryLongBracketStart(text, i, out var longEquals, out var longContentStart))
                {
                    var end = FindLongBracketEnd(text, longContentStart, longEquals);
                    if (end < 0)
                    {
                        return Invalid("lua.lexical.unterminated_long_string", i);
                    }
                    strings.Add(text[longContentStart..end]);
                    tokenCount++;
                    i = end + longEquals + 2;
                    previousIdentifier = null;
                    continue;
                }
                if (IsIdentifierStart(ch))
                {
                    var start = i++;
                    while (i < text.Length && IsIdentifierPart(text[i]))
                    {
                        i++;
                    }
                    var identifier = text[start..i];
                    identifierCount++;
                    tokenCount++;
                    previousIdentifier = identifier;
                    continue;
                }
                if (char.IsDigit(ch) || (ch == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    i++;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '_' or '+' or '-'))
                    {
                        i++;
                    }
                    tokenCount++;
                    previousIdentifier = null;
                    continue;
                }

                if (ch is '(' or '[' or '{')
                {
                    delimiters.Push((ch, i));
                    if (ch == '(' && previousIdentifier is not null)
                    {
                        calls.Add(previousIdentifier);
                        callCount++;
                    }
                }
                else if (ch is ')' or ']' or '}')
                {
                    if (delimiters.Count == 0 || !Matches(delimiters.Peek().Open, ch))
                    {
                        return Invalid("lua.lexical.mismatched_delimiter", i);
                    }
                    delimiters.Pop();
                }
                tokenCount++;
                previousIdentifier = null;
                i++;
            }

            if (delimiters.Count != 0)
            {
                return Invalid("lua.lexical.unclosed_delimiter", delimiters.Peek().Offset);
            }
            return new EndfieldLuaLexicalIndex
            {
                IsValid = true,
                DiagnosticCode = "ok",
                TokenCount = tokenCount,
                IdentifierCount = identifierCount,
                CallCount = callCount,
                StringCount = strings.Count,
                Strings = strings,
                Calls = calls,
            };

            EndfieldLuaLexicalIndex Invalid(string code, int offset) => new()
            {
                IsValid = false,
                DiagnosticCode = code,
                DiagnosticOffset = offset,
                TokenCount = tokenCount,
                IdentifierCount = identifierCount,
                CallCount = callCount,
                StringCount = strings.Count,
                Strings = strings,
                Calls = calls,
            };
        }

        private static bool TryLongBracketStart(string text, int offset, out int equals, out int contentStart)
        {
            equals = 0;
            contentStart = offset + 1;
            if (offset >= text.Length || text[offset] != '[')
            {
                return false;
            }
            var cursor = offset + 1;
            while (cursor < text.Length && text[cursor] == '=')
            {
                equals++;
                cursor++;
            }
            if (cursor >= text.Length || text[cursor] != '[')
            {
                return false;
            }
            contentStart = cursor + 1;
            return true;
        }

        private static int FindLongBracketEnd(string text, int start, int equals)
        {
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] != ']')
                {
                    continue;
                }
                var cursor = i + 1;
                var count = 0;
                while (count < equals && cursor < text.Length && text[cursor] == '=')
                {
                    count++;
                    cursor++;
                }
                if (count == equals && cursor < text.Length && text[cursor] == ']')
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool IsIdentifierStart(char value) =>
            value == '_' || char.IsLetter(value);

        private static bool IsIdentifierPart(char value) =>
            value == '_' || char.IsLetterOrDigit(value);

        private static bool Matches(char open, char close) =>
            open == '(' && close == ')'
                || open == '[' && close == ']'
                || open == '{' && close == '}';
    }
}
