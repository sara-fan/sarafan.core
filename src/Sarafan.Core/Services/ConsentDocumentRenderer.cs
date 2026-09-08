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

    public static (string Html, string SourceHash, string ContentHash) Render(byte[]? source, string? fileName)
    {
        if (source is null || source.Length == 0) throw Invalid("legal_document_file_required");
        if (source.Length > MaxBytes) throw Invalid("legal_document_file_too_large");
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw Invalid("legal_document_file_type");
        string text;
        try { text = Utf8.GetString(source); }
        catch (DecoderFallbackException) { throw Invalid("legal_document_encoding"); }
        if (string.IsNullOrWhiteSpace(text)) throw Invalid("legal_document_text_required");
        if (text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw Invalid("legal_document_control_character");
        var document = Markdown.Parse(text.TrimStart('\uFEFF'), Pipeline);
        foreach (var node in document.Descendants())
        {
            if (node is HtmlBlock or HtmlInline) throw Invalid("legal_document_html_not_allowed");
            if (node is CodeBlock or CodeInline) throw Invalid("legal_document_code_not_allowed");
            if (node is QuoteBlock) throw Invalid("legal_document_quote_not_allowed");
            if (node is ThematicBreakBlock) throw Invalid("legal_document_separator_not_allowed");
            if (node is LinkInline { IsImage: true }) throw Invalid("legal_document_image_not_allowed");
            if (node is LinkInline link && !SafeUrl(link.Url)) throw Invalid("legal_document_link_not_allowed");
            if (node is AutolinkInline auto && !SafeUrl(auto.IsEmail ? "mailto:" + auto.Url : auto.Url))
                throw Invalid("legal_document_link_not_allowed");
        }
        var html = document.ToHtml(Pipeline);
        return (html, Hash(source), Hash(Utf8.GetBytes(html)));
    }

    private static bool SafeUrl(string? value) => value is not null
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" or "mailto"
        && !value.Any(char.IsControl);

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static ServiceException Invalid(string code) => new(400, code);
}
