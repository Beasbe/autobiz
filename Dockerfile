# AutoParser CLI на Linux (замена Windows.Media.Ocr — Tesseract).
#
#   docker build -t autoparser .
#   docker run --rm -v "/путь/к/заявкам:/data" autoparser process "/data/Заявка№10878 26-130531 НТЭ"
#
# Контекст сборки — папка AutoParser/ (там лежит этот файл).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish AutoParser.Cli/AutoParser.Cli.csproj -c Release -o /app --nologo -v q -m:1

FROM mcr.microsoft.com/dotnet/runtime:8.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-rus libfontconfig1 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./
# Шаблоны заключений уже лежат в публикации (Core копирует templates/*.docx).
ENTRYPOINT ["dotnet", "autoparser.dll"]
