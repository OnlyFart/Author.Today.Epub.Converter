using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Elib2Ebook.Extensions;
using Elib2Ebook.Types.Book;
using EpubSharp.Format;

namespace Elib2Ebook.Logic.Builders; 

public class JsonBuilder : BuilderBase {
    private readonly Book _book = new(null);
    
    public static BuilderBase Create() {
        return new JsonBuilder();
    }
    
    public override BuilderBase AddAuthor(Author author) {
        _book.Author = author;
        return this;
    }

    public override BuilderBase WithTitle(string title) {
        _book.Title = title;
        return this;
    }

    public override BuilderBase WithCover(Image cover) {
        _book.Cover = cover;
        return this;
    }

    public override BuilderBase WithBookUrl(Uri url) {
        _book.Url = url;
        return this;
    }

    public override BuilderBase WithAnnotation(string annotation) {
        _book.Annotation = annotation;
        return this;
    }

    public override BuilderBase WithFiles(string directory, string searchPattern, EpubContentType type) {
        return this;
    }

    public override BuilderBase WithChapters(IEnumerable<Chapter> chapters) {
        _book.Chapters = chapters.Where(c => c.IsValid);
        return this;
    }

    public override BuilderBase WithSeria(Seria seria) {
        _book.Seria = seria;
        return this;
    }

    protected override void BuildInternal(string name) {
        using var file = File.Create(name);
        JsonSerializer.Serialize(file, _book);
    }

    protected override string GetFileName(string name) {
        return $"{name}.json".RemoveInvalidChars();
    }
}