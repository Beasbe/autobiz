using AutoParser.Core.Cases;
using AutoParser.Core.Parsing;
using AutoParser.Core.Validate;

namespace AutoParser.Tests;

public class ValidatorTests
{
    [Theory]
    [InlineData("H222OY152", "Н222ОУ152")]      // латиница → кириллица
    [InlineData("Е41ОТУЗ6", "Е410ТУ36")]        // OCR: О→0, З→3 в цифровых позициях
    [InlineData("е 410 ту 36", "Е410ТУ36")]
    [InlineData("A001MP77", "А001МР77")]
    [InlineData("Х954ОВ152", "Х954ОВ152")]
    public void Plate_Normalizes(string raw, string expected) => Assert.Equal(expected, PlateNormalizer.Normalize(raw));

    [Theory]
    [InlineData("Б225ББ111")]   // Б не используется на номерах
    [InlineData("12345678")]
    [InlineData("")]
    public void Plate_Rejects(string raw) => Assert.Null(PlateNormalizer.Normalize(raw));

    [Fact]
    public void Plate_FoundInSentence() =>
        Assert.Contains("Е410ТУ36", PlateNormalizer.FindAll("автомобиля Дэу Нексиа, г/н E41 ОТУ/З6, страховая"));

    [Theory]
    [InlineData("WBAHF31020WX16362", "WBAHF31020WX16362")]
    [InlineData("W B A H F 3 1 0 2 0 W X 1 6 3 6 2", "WBAHF31020WX16362")]
    [InlineData("ХWВ3L32ЕDВА265960", "XWB3L32EDBA265960")]   // кириллические двойники
    [InlineData("WBAHF3IO2OWX16362", "WBAHF31020WX16362")]   // I→1, O→0
    public void Vin_Normalizes(string raw, string expected) => Assert.Equal(expected, VinValidator.Normalize(raw));

    [Fact]
    public void Vin_FindsSpaced() =>
        Assert.Contains("WBAHF31020WX16362", VinValidator.FindAll("BMW Z4 W B A H F 3 1 0 2 0 W X 1 6 3 6 2"));

    [Fact]
    public void Vin_CheckDigit() => Assert.True(VinValidator.CheckDigitValid("1M8GDM9AXKP042788"));

    [Theory]
    [InlineData("99 65 680398", "99 65 № 680398")]
    [InlineData("9965 № 680398", "99 65 № 680398")]
    [InlineData("Копия СТС 99 62 435851", "99 62 № 435851")]
    public void Sts_Normalizes(string raw, string expected) => Assert.Equal(expected, StsNormalizer.Normalize(raw));

    [Fact]
    public void Sts_FromSeriesNumber() => Assert.Equal("99 65 № 680398", StsNormalizer.FromSeriesNumber("9965", "680398"));

    [Theory]
    [InlineData("190 КВТ/258,3 Л.С.", "190/258,3")]
    [InlineData("Nexia 1.5i МТ 80 л.с.", "58,8/80")]
    [InlineData("78,7 кВт / 107 л.с.", "78,7/107")]
    public void Power_Parses(string raw, string expected) => Assert.Equal(expected, PowerParser.Parse(raw)!.ToString());

    [Theory]
    [InlineData("190", "мощность квт л с", "190/258,3")]
    [InlineData("80", "мощность л с", "58,8/80")]
    public void Power_BareNumberUsesLabel(string raw, string label, string expected) =>
        Assert.Equal(expected, PowerParser.ParseBare(raw, label)!.ToString());

    [Theory]
    [InlineData("ОСАГО ХХХ № 0622257614", "ХХХ №0622257614")]
    [InlineData("дог. NQXXX0622257614 от 04.03.26", "ХХХ №0622257614")]
    [InlineData("№ ХХХ0390237711", "ХХХ №0390237711")]
    [InlineData("ТТТ № 7099145248", "ТТТ №7099145248")]
    public void Policy_Finds(string raw, string expected) => Assert.Equal(expected, PolicyNormalizer.FindAll(raw).First().ToString());

    [Theory]
    [InlineData("28.03.2024", "28.03.2024")]
    [InlineData("«15» Июля 2026г., произошло ДТП", "15.07.2026")]
    [InlineData("16 часов 30 мин 15.07.2026г.", "15.07.2026")]
    public void Date_Finds(string raw, string expected) => Assert.Equal(expected, DateNormalizer.Format(DateNormalizer.First(raw)!.Value));

    [Theory]
    [InlineData("г. Нижний Новгород", "Нижегородская область")]
    [InlineData("г.Н.Новгород, ул. Ильинская", "Нижегородская область")]
    [InlineData("Воронежская обл., Богучарский р-н, с. Залиман", "Воронежская область")]
    [InlineData("г. Хабаровск", "Хабаровский край")]
    [InlineData("в г. Казани", "Республика Татарстан")]
    public void Region_ByPlace(string place, string expected) => Assert.Equal(expected, RegionDirectory.Default.ByPlace(place)!.Name);

    [Theory]
    [InlineData("Н222ОУ152", "Нижегородская область")]
    [InlineData("Е410ТУ36", "Воронежская область")]
    [InlineData("А001МР77", "г. Москва")]
    public void Region_ByPlate(string plate, string expected) => Assert.Equal(expected, RegionDirectory.Default.ByPlate(plate)!.Name);

    [Theory]
    [InlineData("Б/М1", "B/М1")]
    [InlineData("B/M1", "B/М1")]
    [InlineData("B", "B")]
    public void Category_Normalizes(string raw, string expected) => Assert.Equal(expected, ValueTypes.NormalizeCategory(raw));

    [Theory]
    [InlineData("5-ступ кпп", "МКПП")]
    [InlineData("Nexia 1.5i МТ 80 л.с.", "МКПП")]
    [InlineData("АКПП 8-ступ", "АКПП")]
    [InlineData("Вариатор CVT", "Вариатор")]
    public void Gearbox_Detects(string raw, string expected) => Assert.Equal(expected, GearboxDetector.Detect(raw));

    [Theory]
    [InlineData("- 2-СЛ АКРИЛОВ ОКРАСКА", "Акриловая двухслойная")]
    [InlineData("Цвет, тип краски: бледно-черный. Двухслойный металлик", "Металлик двухслойный")]
    [InlineData("2-СЛОЙН МЕТАЛЛИК", "Металлик двухслойный")]
    public void PaintType_Detects(string raw, string expected) => Assert.Equal(expected, PaintTypeDetector.Detect(raw));

    [Theory]
    [InlineData(@"C:\data\Заявка№27742 26-154015 НТЭ\У-26-154015-3020-004", "У-26-154015/3020-004")]
    [InlineData(@"C:\data\Заявка№10878 26-130531 НТЭ", "У-26-130531")]
    public void ClaimNumber_FromFolder(string path, string expected) => Assert.Equal(expected, CaseFolderScanner.ClaimNumberFromPath(path));
}

public class AudatexNamesTests
{
    [Theory]
    [InlineData("НАКЛ Н ЗЕРКАЛО НАР Л", "Накладка нижняя зеркала наружного левого")]
    [InlineData("ДВЕРЬ П Л", "Дверь передняя левая")]
    [InlineData("КРЫЛО П Л", "Крыло переднее левое")]
    [InlineData("БОКОВАЯ СТЕНКА П Л", "Крыло переднее левое")]
    [InlineData("НАКЛАДКА ПОРОГА Л", "Накладка порога левого")]
    [InlineData("БАМПЕР П", "Бампер передний")]
    [InlineData("МОЛД БАМПЕР Л", "Молдинг бампера левый")]
    [InlineData("ФАРА ПР", "Фара правая")]
    [InlineData("ЩИТОК ЛОНЖЕРОНА Л", "Щиток лонжерона левого")]
    public void Expands(string audatex, string expected) => Assert.Equal(expected, AudatexNames.Default.Expand(audatex));

    [Theory]
    [InlineData("Л", "левый", 'f', false, "левая")]
    [InlineData("П", "передний", 'n', false, "переднее")]
    [InlineData("П", "передний", 'm', true, "переднего")]
    [InlineData("Н", "нижний", 'f', true, "нижней")]
    public void Declines(string _, string adj, char gender, bool genitive, string expected) =>
        Assert.Equal(expected, AudatexNames.Decline(adj, gender, genitive));
}
