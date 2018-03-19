﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Xml.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mustache.Extension;

namespace Mustache
{
    public class MustacheRenderer
    {
        public Dictionary<string, Func<MustacheContext, string>> Cache { get; private set; }
        public Dictionary<string, Func<MustacheContext, string>> PartialCache { get; private set; }
        public Dictionary<string, string> Partials { get; private set; }

        public Delimiter CurrentDelimiter { get; private set; }

        public MustacheRenderer()
        {
            Cache = new Dictionary<string, Func<MustacheContext, string>>();
            PartialCache = new Dictionary<string, Func<MustacheContext, string>>();
            CurrentDelimiter = Delimiter.Default();
        }

        public string Render(string template, object view, Dictionary<string, string> partials)
        {
            if (partials != null)
            {
                Partials = partials;
            }

            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            if (!Cache.ContainsKey(template))
            {
                Cache[template] = Compile(template);
            }

            return Cache[template](new MustacheContext(view, null));
        }

        Func<MustacheContext, MustacheRenderer, string> CompileTokens(List<Token> tokens)
        {
            var subs = new Dictionary<int, Func<MustacheContext, MustacheRenderer, string>>();
            Func<int, List<Token>, Func<MustacheContext, MustacheRenderer, string>> subRender = (subIndex, subTokens) =>
            {
                if (!subs.ContainsKey(subIndex))
                {
                    subs[subIndex] = CompileTokens(subTokens);
                }
                return subs[subIndex];
            };

            return (ctx, rnd) =>
            {
                var builder = new StringBuilder();

                for (int i = 0; i < tokens.Count; ++i)
                {
                    var token = tokens[i];
                    var next = string.Empty;

                    switch (token.Type)
                    {
                        case TokenType.SectionOpen:
                            next = rnd.RenderSection(token, ctx, subRender(i, token.Children));
                            builder.Append(next);
                            break;
                        case TokenType.InvertedSectionOpen:
                            next = rnd.RenderInverted(token.Name, ctx, subRender(i, token.Children));
                            builder.Append(next);
                            break;
                        case TokenType.Partial:
                            next = rnd.RenderPartial(token.Name, ctx, token.PartialIndent);
                            builder.Append(next);
                            break;
                        case TokenType.Variable:
                            next = rnd.RenderName(token.Name, ctx, true);
                            builder.Append(next);
                            break;
                        case TokenType.UnescapedVariable:
                            next = rnd.RenderName(token.Name, ctx, false);
                            builder.Append(next);
                            break;
                        case TokenType.Text:
                            builder.Append(token.Template, token.TextStartIndex, token.TextLength);
                            break;
                    }
                }

                return builder.ToString();
            };
        }

        // When Lambdas are used as the data value for Section tag, 
        // the returned value MUST be rendered against the current delimiters.
        Func<MustacheContext, string> CompileAgainstCurrentDelimiter(string template)
        {
            var parsed = new MustacheParser().Parse(template, CurrentDelimiter);
            return Compile(parsed.Item1);
        }

        Func<MustacheContext, string> Compile(string template)
        {
            var parsed = new MustacheParser().Parse(template, Delimiter.Default());
            CurrentDelimiter = parsed.Item2;
            return Compile(parsed.Item1);
        }

        Func<MustacheContext, string> Compile(List<Token> tokens)
        {
            var fn = CompileTokens(tokens);

            return (c) =>
            {
                return fn(c, this);
            };
        }

        string RenderSection(Token token, MustacheContext ctx, Func<MustacheContext, MustacheRenderer, string> callback)
        {
            var value = ctx.Lookup(token.Name);

            if (value.IsFalsey())
            {
                return string.Empty;
            }

            if (value.IsLambda())
            {
                var template = value.InvokeSectionLambda(token.SectionTemplate) as string;
                if (!string.IsNullOrEmpty(template))
                {
                    var fn = CompileAgainstCurrentDelimiter(template);
                    return fn(ctx);
                }
            }

            if (value is ICollection)
            {
                var sb = new StringBuilder();
                foreach (object item in value as ICollection)
                {
                    sb.Append(callback(new MustacheContext(item, ctx), this));
                }
                return sb.ToString();
            }

            return callback(new MustacheContext(value, ctx), this);
        }


        string RenderInverted(string name, MustacheContext ctx, Func<MustacheContext, MustacheRenderer, string> callback)
        {
            var value = ctx.Lookup(name);

            if (value.IsFalsey())
            {
                return callback(ctx, this);
            }

            return string.Empty;
        }

        string RenderPartial(string name, MustacheContext ctx, string indent)
        {
            var key = name + "#" + indent;
            var fn = PartialCache.GetValueOrDefault(key);

            if (fn == null && Partials != null)
            {
                var partial = Partials.GetValueOrDefault(name);
                if (partial == null)
                {
                    return string.Empty;
                }

                if (string.IsNullOrEmpty(indent))
                {
                    fn = Compile(partial);
                }
                else
                {
                    // FIXME: dirty
                    var replaced = string.Empty;
                    if (partial[partial.Length - 1] != '\n')
                    {
                        replaced = Regex.Replace(partial, @"^", indent, RegexOptions.Multiline);
                    }
                    else
                    {
                        var s = partial.Substring(0, partial.Length - 1);
                        replaced = Regex.Replace(s, @"^", indent, RegexOptions.Multiline) + "\n";
                    }
                    fn = Compile(replaced);
                }

                PartialCache[key] = fn;
            }

            if (fn == null)
            {
                return string.Empty;
            }

            return fn(ctx);
        }

        string RenderName(string name, MustacheContext ctx, bool escape)
        {
            var value = ctx.Lookup(name);

            if (value == null)
            {
                return string.Empty;
            }

            if (value.IsLambda())
            {
                var tpl = value.InvokeNameLambda() as string;
                value = Compile(tpl)(ctx);
            }

            if (value.IsFalsey())
            {
                return string.Empty;
            }

            if (escape)
            {
                return System.Web.HttpUtility.HtmlEncode(value.ToString());
            }

            return value.ToString();
        }
    }
}