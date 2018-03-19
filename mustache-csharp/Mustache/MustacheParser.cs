using System;
using System.Text;
using System.Collections.Generic;

namespace Mustache
{
    public struct Delimiter
    {
        public string Left;
        public string Right;

        public static Delimiter Default()
        {
            return new Delimiter { Left = "{{", Right = "}}" };
        }
    }

    public enum TokenType
    {
        Text,
        Variable,
        SectionOpen,
        SectionClose,
        InvertedSectionOpen,
        UnescapedVariable,
        Comment,
        Partial,
        DelimiterChange
    }

    public class Token
    {
        public string Template;
        public int StartIndex;
        public TokenType Type;
        public string Value;
        public string PartialIndent;
        public List<Token> Children;
        public bool IsBol;

        public int SectionStartIndex;
        public int SectionEndIndex;

        public string SectionTemplate
        {
            get
            {
                return Template.Substring(SectionStartIndex, SectionEndIndex - SectionStartIndex + 1);
            }
        }
    }

    public class MustacheParser
    {
        public TokenType TokenInfo(char c)
        {
            switch (c)
            {
                case '>': return TokenType.Partial;
                case '^': return TokenType.InvertedSectionOpen;
                case '/': return TokenType.SectionClose;
                case '&':
                case '{': return TokenType.UnescapedVariable;
                case '#': return TokenType.SectionOpen;
                case '!': return TokenType.Comment;
                case '=': return TokenType.DelimiterChange;
                default: return TokenType.Variable;
            }
        }
        void SquashTokens(ref List<Token> tokens)
        {
            Func<string, bool> isWhiteText = (s) =>
            {
                foreach (var c in s)
                {
                    if (!(Char.IsWhiteSpace(c) || c == '\n' || c == '\r'))
                    {
                        return false;
                    }
                }
                return true;
            };

            uint tagMask = 0;
            var startIndex = 0;
            var removes = new List<int>();
            var tmp = new List<int>();
            var partialTokenMask = 1U << (int)TokenType.Partial;
            var squashTokenMask = 1U << (int)TokenType.Comment |
                                  1U << (int)TokenType.SectionOpen |
                                  1U << (int)TokenType.InvertedSectionOpen |
                                  1U << (int)TokenType.SectionClose |
                                  1U << (int)TokenType.Partial |
                                  1U << (int)TokenType.DelimiterChange;

            for (int i = 0; i < tokens.Count; ++i)
            {
                tagMask |= 1U << (int)tokens[i].Type;

                if (i + 1 == tokens.Count || tokens[i + 1].IsBol)
                {
                    bool hasSquashToken = (tagMask & squashTokenMask) != 0;
                    bool hasPartialToken = (tagMask & partialTokenMask) != 0;
                    bool textOnly = (tagMask & ~(squashTokenMask | (1U << (int)TokenType.Text))) == 0;

                    if (hasSquashToken && textOnly)
                    {
                        bool onlyWhiteText = true;
                        tmp.Clear();
                        for (int j = startIndex; j <= i; ++j)
                        {
                            if (tokens[j].Type == TokenType.Text)
                            {
                                if (!isWhiteText(tokens[j].Value))
                                {
                                    onlyWhiteText = false;
                                    break;
                                }
                                tmp.Add(j);
                            }
                            else if (tokens[j].Type == TokenType.Partial)
                            {
                                // NOTE: https://github.com/mustache/spec/blob/master/specs/partials.yml#L13-L15
                                var sb = new StringBuilder(tokens[j].PartialIndent);
                                foreach (var x in tmp)
                                {
                                    sb.Append(tokens[x].Value);
                                }
                                tokens[j].PartialIndent = sb.ToString();
                            }
                        }

                        if (onlyWhiteText)
                        {
                            removes.AddRange(tmp);
                        }
                    }

                    tagMask = 0;
                    startIndex = i + 1;
                }
            }

            for (int i = removes.Count - 1; 0 <= i; --i)
            {
                tokens.RemoveAt(removes[i]);
            }
        }
        List<Token> NestTokens(List<Token> tokens)
        {
            var root = new List<Token>();
            var collector = root;
            var sections = new List<Token>();

            foreach (var token in tokens)
            {
                if (token.Type == TokenType.SectionOpen || token.Type == TokenType.InvertedSectionOpen)
                {
                    token.Children = new List<Token>();
                    sections.Add(token);
                    collector.Add(token);
                    collector = token.Children;
                }
                else if (token.Type == TokenType.SectionClose)
                {
                    if (sections.Count == 0)
                    {
                        // TODO: be better Exception
                        throw new Exception("Unopened section: " + token.Value);
                    }

                    var section = sections[sections.Count - 1];
                    sections.RemoveAt(sections.Count - 1);

                    if (section.Value != token.Value)
                    {
                        // TODO: be better Exception
                        throw new Exception("Unclosed section: " + section.Value);
                    }

                    section.SectionEndIndex = token.StartIndex - 1;

                    if (0 < sections.Count)
                    {
                        collector = sections[sections.Count - 1].Children;
                    }
                    else
                    {
                        collector = root;
                    }
                }
                else
                {
                    collector.Add(token);
                }
            }

            if (0 < sections.Count)
            {
                // TODO: be better Exception
                var section = sections[sections.Count - 1];
                throw new Exception("Unclosed section: " + section.Value);
            }

            return root;
        }

        public Tuple<List<Token>, Delimiter> Parse(string template, Delimiter delimiter)
        {
            var scanner = new MustacheScanner(template);
            var tokens = new List<Token>();
            var start = scanner.Pos;
            var lastBol = 0;

            Action<int> pushText = (int end) =>
            {
                if (start < end && end <= template.Length)
                {
                    tokens.Add(new Token
                    {
                        Template = template,
                        Type = TokenType.Text,
                        Value = template.Substring(start, end - start),
                        StartIndex = start,
                        IsBol = start == lastBol,
                    });
                }
            };

            for (var ok = true; ok;)
            {
                if (scanner.Pos == 0 || scanner.Peek(-1) == '\n')
                {
                    lastBol = scanner.Pos;
                }

                if (scanner.Peek() == '\n')
                {
                    pushText(scanner.Pos + 1);
                    start = scanner.Pos + 1;
                    ok = scanner.Seek(1);
                }
                else if (scanner.StartsWith(delimiter.Left))
                {
                    if (start < scanner.Pos)
                    {
                        pushText(scanner.Pos);
                        start = scanner.Pos;
                    }

                    ok = scanner.Seek(delimiter.Left.Length); // discard begin tag

                    var tokenChar = scanner.Peek();
                    var tokenType = TokenInfo(tokenChar);
                    var value = string.Empty;

                    if (tokenType != TokenType.Variable)
                    {
                        scanner.Seek(1); // discard token type character
                    }

                    if (tokenType == TokenType.DelimiterChange)
                    {
                        value = scanner.ReadUntilJustBefore("=");
                        scanner.Seek(1); // discard '='
                        scanner.SeekUntilJustBefore(delimiter.Right);
                    }
                    else if (tokenType == TokenType.UnescapedVariable && tokenChar == '{')
                    {
                        value = scanner.ReadUntilJustBefore("}" + delimiter.Right);
                        scanner.Seek(1); // discard '}'
                    }
                    else
                    {
                        value = scanner.ReadUntilJustBefore(delimiter.Right);
                    }

                    if (!scanner.StartsWith(delimiter.Right))
                    {
                        // TODO: be better exception
                        throw new Exception("Unclosed tag at " + scanner.Pos);
                    }

                    var token = new Token
                    {
                        Template = template,
                        Type = tokenType,
                        Value = value.Trim(), // trim to use it as key string
                        StartIndex = start,
                        IsBol = start == lastBol,
                    };

                    start = scanner.Pos + delimiter.Right.Length;
                    ok = scanner.Seek(delimiter.Right.Length); // discard end tag

                    token.SectionStartIndex = start;
                    tokens.Add(token);

                    if (tokenType == TokenType.DelimiterChange)
                    {
                        var sp = value.Split(null as string[], StringSplitOptions.RemoveEmptyEntries);
                        delimiter.Left = sp[0];
                        delimiter.Right = sp[1];
                    }
                }
                else
                {
                    ok = scanner.Seek(1);
                }
            }

            pushText(scanner.Pos + 1);

            SquashTokens(ref tokens);
            tokens = NestTokens(tokens);
            return Tuple.Create(tokens, delimiter);
        }

    }
}