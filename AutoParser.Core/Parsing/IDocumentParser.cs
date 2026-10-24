using AutoParser.Core.Model;

namespace AutoParser.Core.Parsing;

/// <summary>Извлекает кандидатов значений полей из одного документа.</summary>
public interface IDocumentParser
{
    string Name { get; }
    bool CanParse(CaseDocument doc);
    IEnumerable<FieldCandidate> Parse(CaseDocument doc, ParseContext ctx);
}

public sealed class ParseContext
{
    public required string CaseFolder { get; init; }
    public List<string> Log { get; } = [];
}

public static class DocumentTypes
{
    public static bool IsFieldSource(DocumentType t) => Classify.DocumentClassifier.IsFieldSource(t);
}
