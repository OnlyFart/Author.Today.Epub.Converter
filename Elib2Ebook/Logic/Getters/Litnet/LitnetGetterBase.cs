using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using Elib2Ebook.Configs;
using Elib2Ebook.Extensions;
using Elib2Ebook.Types.Book;
using Elib2Ebook.Types.Litnet;
using HtmlAgilityPack;
using HtmlAgilityPack.CssSelectors.NetCore;
namespace Elib2Ebook.Logic.Getters.Litnet;

public abstract class LitnetGetterBase : GetterBase {
    public LitnetGetterBase(BookGetterConfig config) : base(config) { }

    private Uri _apiUrl => new($"https://api.{SystemUrl.Host}/");
    
    private static readonly string DeviceId = Guid.NewGuid().ToString().ToUpper();
    private const string SECRET = "14a6579a984b3c6abecda6c2dfa83a64";

    private string _token { get; set; }

    protected override string GetId(Uri url) => base.GetId(url).Split('-').Last().Replace("b", string.Empty);
    private static byte[] DecryptBin(string text)
    {
        using var aes = Aes.Create();
        const int IV_SHIFT = 16;

        aes.Key = Encoding.UTF8.GetBytes(SECRET);
        aes.IV = Encoding.UTF8.GetBytes(text)[..IV_SHIFT];

        var decryptor = aes.CreateDecryptor();
        using var ms = new MemoryStream(Convert.FromBase64String(text));
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);

        var output = new MemoryStream();
        cs.CopyTo(output);
        return output.ToArray()[IV_SHIFT..];
    }
    private static string Decrypt(string text) {
        return Encoding.UTF8.GetString(DecryptBin(text));
    }

    private static string GetSign(string token) {
        using var md5 = MD5.Create();
        var inputBytes = Encoding.ASCII.GetBytes(DeviceId + SECRET + (token ?? string.Empty));
        var hashBytes = md5.ComputeHash(inputBytes);

        return Convert.ToHexString(hashBytes).ToLower();
    }
    
    /// <summary>
    /// Авторизация в системе
    /// </summary>
    /// <exception cref="Exception"></exception>
    public override async Task Authorize() {
        var path = Config.HasCredentials ? "user/find-by-login" : "registration/registration-by-device";

        var url = _apiUrl.MakeRelativeUri($"v1/{path}?login={HttpUtility.UrlEncode(Config.Options.Login?.TrimStart('+') ?? string.Empty)}&password={HttpUtility.UrlEncode(Config.Options.Password)}&app=android&device_id={DeviceId}&sign={GetSign(string.Empty)}");
        var response = await Config.Client.GetAsync(url);
        var data = await response.Content.ReadFromJsonAsync<LitnetAuthResponse>();

        if (!string.IsNullOrWhiteSpace(data?.Token)) {
            Console.WriteLine("Успешно авторизовались");
            _token = data.Token;
        } else {
            throw new Exception($"Не удалось авторизоваться. {data?.Error}");
        }
    }

    private async Task<LitnetBookResponse> GetBook(string token, string bookId) {
        var url = _apiUrl.MakeRelativeUri($"/v1/book/get/{bookId}?app=android&device_id={DeviceId}&user_token={token}&sign={GetSign(token)}");
        var response = await Config.Client.GetFromJsonAsync<LitnetBookResponse>(url);
        if (!Config.HasCredentials && response!.AdultOnly) {
            throw new Exception("Произведение 18+. Необходимо добавить логин и пароль.");
        }
        
        return response;
    }

    private async Task<LitnetContentsResponse[]> GetBookContents(string token, string bookId) {
        var url = _apiUrl.MakeRelativeUri($"/v1/book/contents?bookId={bookId}&app=android&device_id={DeviceId}&user_token={token}&sign={GetSign(token)}");
        var response = await Config.Client.GetAsync(url);
        return response.StatusCode == HttpStatusCode.NotFound ? 
            Array.Empty<LitnetContentsResponse>() : 
            await response.Content.ReadFromJsonAsync<LitnetContentsResponse[]>();
    }

    private async Task<IEnumerable<LitnetChapterResponse>> GetToc(string token, IEnumerable<LitnetContentsResponse> contents) {
        var chapters = string.Join("&", contents.Select(t => $"chapter_ids[]={t.Id}"));
        var url = _apiUrl.MakeRelativeUri($"/v1/book/get-chapters-texts/?{chapters}&app=android&device_id={DeviceId}&sign={GetSign(token)}&user_token={token}");
        var response = await Config.Client.GetFromJsonAsync<LitnetChapterResponse[]>(url);
        return SliceToc(response);
    }
    
    public override async Task<Book> Get(Uri url) {
        var bookId = GetId(url);

        var litnetBook = await GetBook(_token, bookId);

        var uri = SystemUrl.MakeRelativeUri(litnetBook.Url.AsUri().AbsolutePath);
        var book = new Book(uri) {
            Cover = await GetCover(litnetBook),
            Chapters = await FillChapters(_token, litnetBook, bookId),
            Title = litnetBook.Title.Trim(),
            Author = GetAuthor(litnetBook),
            Annotation = GetAnnotation(litnetBook),
            Seria = await GetSeria(uri, litnetBook),
            Lang = litnetBook.Lang
        };
            
        return book;
    }

    private async Task<Seria> GetSeria(Uri url, LitnetBookResponse book) {
        try {
            var doc = await Config.Client.GetHtmlDocWithTriesAsync(url);
            var a = doc.QuerySelector("div.book-view-info-coll a[href*='sort=cycles']");
            if (a != default) {
                return new Seria {
                    Name = a.GetText(),
                    Url = url.MakeRelativeUri(a.Attributes["href"].Value),
                    Number = book.CyclePriority is > 0 ? book.CyclePriority.Value.ToString() : string.Empty
                };
            }
        } catch (Exception ex) {
            Console.WriteLine(ex);
        }

        return default;
    }

    private Author GetAuthor(LitnetBookResponse book) {
        return new Author((book.AuthorName ?? SystemUrl.Host).Trim(), SystemUrl.MakeRelativeUri($"/ru/{book.AuthorId}"));
    }

    private static string GetAnnotation(LitnetBookResponse book) {
        return string.IsNullOrWhiteSpace(book.Annotation) ? 
            string.Empty : 
            string.Join("", book.Annotation.Split("\n", StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().CoverTag("p")));
    }
    
    private Task<Image> GetCover(LitnetBookResponse book) {
        return !string.IsNullOrWhiteSpace(book.Cover) ? SaveImage(book.Cover.AsUri()) : Task.FromResult(default(Image));
    }

    private async Task<List<Chapter>> FillChapters(string token, LitnetBookResponse book, string bookId) {
        var result = new List<Chapter>();
            
        var contents = await GetBookContents(token, bookId);
        if (contents.Length == 0) {
            return result;
        }
        
        var chapters = await GetToc(token, contents);
        var map = chapters.ToDictionary(t => t.Id);
        
        foreach (var content in contents) {
            var litnetChapter = map[content.Id];

            Console.WriteLine($"Загружаю главу {content.Title.Trim().CoverQuotes()}");
            var chapter = new Chapter {
                Title = (content.Title ?? book.Title).Trim()
            };
            if (string.IsNullOrWhiteSpace(litnetChapter.Text))
            {
                var values = new Dictionary<string, string>() {
                    { "app", "android" },
                    { "device_id", DeviceId },
                    { "user_token", _token },
                    { "sign", GetSign(_token) },
                    { "version", "1.0" }
                };
                var data = new FormUrlEncodedContent(values);
                var url = $"https://sapi.litnet.com/v1/text/get-chapter?chapter_id={litnetChapter.Id}";
                var response = await Config.Client.PostAsync(url, data);
                var buff = await response.Content.ReadAsByteArrayAsync();
                var gz = DecryptBin(Convert.ToBase64String(buff));
                var txt = Gunzip(gz);
                var chapterDoc = txt.Deserialize<string[]>().Aggregate(new StringBuilder(), (sb, row) => sb.Append(row)).AsHtmlDoc();
                chapter.Images = await GetImages(chapterDoc, SystemUrl);
                chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            } else 
            {
                var chapterDoc = GetChapter(litnetChapter);
                chapter.Images = await GetImages(chapterDoc, SystemUrl);
                chapter.Content = chapterDoc.DocumentNode.InnerHtml;
            }

            result.Add(chapter);
        }

        return result;
    }

    private string Gunzip(byte[] gz)
    {
        using (var compressedStream = new MemoryStream(gz))
        using (var zipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
        using (var resultStream = new MemoryStream())
        {
            zipStream.CopyTo(resultStream);
            var result = resultStream.ToArray();
            return Encoding.UTF8.GetString(result);
        }
    }

    private static HtmlDocument GetChapter(LitnetChapterResponse chapter) {
        return Decrypt(chapter.Text).Deserialize<string[]>().Aggregate(new StringBuilder(), (sb, row) => sb.Append(row)).AsHtmlDoc();
    }
}