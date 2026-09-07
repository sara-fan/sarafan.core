// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Sarafan.Core.Services;

public static class ConsentDocumentRenderer
{
    public const int MaxBytes = 256 * 1024;
    public const string Version = "sarafan-safe-markdown-1/markdig-1.3.2";
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static (string Html, string SourceHash, string ContentHash) Render(byte[] source, string fileName)
    {
        if (source is null || source.Length is 0 or > MaxBytes || string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        string text;
        try { text = Utf8.GetString(source); }
        catch (DecoderFallbackException) { throw Invalid(); }
        if (string.IsNullOrWhiteSpace(text) || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw Invalid();
        var document = Markdown.Parse(text.TrimStart('\uFEFF'), Pipeline);
        foreach (var node in document.Descendants())
        {
            if (node is HtmlBlock or HtmlInline or CodeBlock or CodeInline or QuoteBlock or ThematicBreakBlock)
                throw Invalid();
            if (node is LinkInline link && (link.IsImage || !SafeUrl(link.Url)))
                throw Invalid();
            if (node is AutolinkInline auto && !SafeUrl(auto.IsEmail ? "mailto:" + auto.Url : auto.Url))
                throw Invalid();
        }
        var html = document.ToHtml(Pipeline);
        return (html, Hash(source), Hash(Utf8.GetBytes(html)));
    }

    private static bool SafeUrl(string? value) => value is not null
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" or "mailto"
        && !value.Any(char.IsControl);

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static ServiceException Invalid() => new(400, "invalid_legal_document");
}
