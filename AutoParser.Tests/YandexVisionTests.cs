using AutoParser.Core.Text;

namespace AutoParser.Tests;

public class YandexVisionTests
{
    [Fact]
    public void ParsesRecognizeTextResponse()
    {
        const string json = """
        {"result":{"textAnnotation":{"width":"1000","height":"800","blocks":[{"lines":[
          {"text":"Собственник МИХАЙЛОВА","words":[
            {"boundingBox":{"vertices":[{"x":"10","y":"20"},{"x":"110","y":"20"},{"x":"110","y":"40"},{"x":"10","y":"40"}]},"text":"Собственник"},
            {"boundingBox":{"vertices":[{"x":"200","y":"20"},{"x":"320","y":"20"},{"x":"320","y":"40"},{"y":"40"}]},"text":"МИХАЙЛОВА"}]}]}]},"page":"0"}}
        """;
        var page = YandexVisionOcrEngine.Parse(json, 3, scale: 2, width: 500, height: 400, engine: "YandexVision-page");
        Assert.Equal(3, page.PageNumber);
        Assert.Single(page.Lines);
        Assert.Equal("Собственник МИХАЙЛОВА", page.Lines[0].Text);
        Assert.Equal(new BBox(5, 10, 50, 10), page.Lines[0].Words[0].Box);   // координаты делятся на масштаб
        Assert.Equal(TextOrigin.Ocr, page.Origin);
    }

    [Fact]
    public void NotAvailableWithoutKeys() =>
        Assert.False(new YandexVisionOcrEngine(apiKey: null, folderId: null, iamToken: null).IsAvailable
                     && Environment.GetEnvironmentVariable("YANDEX_FOLDER_ID") is null);
}
