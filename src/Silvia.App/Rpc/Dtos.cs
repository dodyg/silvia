using System.Text.Json.Serialization;

namespace Silvia.App.Rpc;

// Wire shapes for the RPC surface. Every root type used by a method, event or channel must be listed in
// AppJsonContext, or the NeoAstra generator fails the build (NEORPC009).

public sealed record EmptyRequest;

public sealed record ProjectDto(
    string Id,
    string Name,
    string? Description,
    string CreatedUtc,
    string? LastOpenedUtc,
    string Status);

public sealed record CreateProjectRequest(string Name, string? Description, string? EmbedModel);

public sealed record RenameProjectRequest(string Id, string Name);

public sealed record ProjectIdRequest(string Id);

public sealed record ImportProjectRequest(string FilePath, string Name);

public sealed record ProjectFileDto(string Path);

public sealed record SourceDto(string Id, string Path, string IncludeGlobs, string ExcludeGlobs, bool Enabled);

public sealed record ProjectIdScoped(string ProjectId);

public sealed record AddSourceRequest(string ProjectId, string Path, string? IncludeGlobs, string? ExcludeGlobs);

/// <summary>The result of the folder dialog: <c>picked</c> with a path, <c>canceled</c>, or <c>unavailable</c>.</summary>
public sealed record FolderPickDto(string Status, string? Path);

/// <summary>Whether a project's embedder and reranker are installed. Without the embedder, search is by keyword.</summary>
public sealed record ModelStatusDto(bool CanEmbed, bool CanRerank);

public sealed record SourceIdRequest(string ProjectId, string SourceId);

public sealed record RescanRequest(string ProjectId, string? SourceId);

public sealed record DocumentDto(string DocumentId, string FileName, string Folder, int Chunks);

public sealed record JobSummaryDto(int Pending, int Running, int Done, int Failed, IReadOnlyList<FailureDto> RecentFailures);

public sealed record FailureDto(string Path, string? Error, string? FinishedUtc);

public sealed record ConversationDto(string Id, string Title, string CreatedUtc);

public sealed record ConversationIdRequest(string ProjectId, string ConversationId);

public sealed record MessageDto(string Role, string Content, string CreatedUtc, IReadOnlyList<CitationDto> Sources);

public sealed record CitationDto(string DocumentId, int ChunkIndex, string FileName, string Text);

public sealed record AskRequest(string ProjectId, string? ConversationId, string Question);

public sealed record AskUpdateDto(string Kind, string? ConversationId, string? Text, IReadOnlyList<CitationDto>? Sources);

public sealed record SearchRequest(string ProjectId, string Query, int? TopK);

public sealed record HitDto(string DocumentId, int ChunkIndex, string FileName, string Text, int Relevance);

public sealed record ProjectSettingsDto(
    string EmbedModel,
    int EmbedDims,
    string RerankModel,
    string ChatModel,
    string CloudPolicy,
    int ChunkTokens,
    int ChunkOverlap,
    int TopK,
    int MemoryTopK,
    int MemoryMinPercent,
    string SystemPrompt,
    bool DocumentOverviews,
    string? JudgeModel);

public sealed record FactDto(string Id, string Text, string CreatedUtc);

public sealed record AddFactRequest(string ProjectId, string Text);

public sealed record UpdateFactRequest(string ProjectId, string Id, string Text);

public sealed record FactIdRequest(string ProjectId, string Id);

public sealed record UpdateProjectSettingsRequest(string ProjectId, ProjectSettingsDto Settings);

public sealed record ModelDto(string Id, string Kind, int Dimensions, bool Installed, bool Default);

public sealed record GlobalSettingsDto(string DataRoot, string ModelsRoot, bool DeepSeekKeyConfigured, int MaxOpenProjects);

public sealed record AppearanceDto(
    string Theme,
    int InterfaceSize,
    int ReadingSize,
    string LineHeight,
    string ReadingWidth,
    string InterfaceFont,
    string ReadingFont,
    string MonoFont);

public sealed record FontListDto(IReadOnlyList<string> Bundled, IReadOnlyList<string> System);

public sealed record OpenDocumentRequest(string ProjectId, string DocumentId, int? Page);

public sealed record ShellResult(bool Opened, string Message);

public sealed record SetDeepSeekKeyRequest(string Key);

/// <summary>
/// What DeepSeek said about the key: <c>ok</c>, <c>no_key</c>, <c>rejected</c>, <c>unreachable</c> or
/// <c>failed</c>, a sentence for the screen, and the chat models it offers when the key works.
/// </summary>
public sealed record ChatProviderDto(string State, string Message, IReadOnlyList<string> Models);

public sealed record ModelDownloadStatusDto(string State, string? File, int Percent, string? Message);

public sealed record DocumentRequest(string ProjectId, string DocumentId);

public sealed record MetadataEntryDto(string Key, string Value);

public sealed record PassageDto(int ChunkIndex, string Text, IReadOnlyList<MetadataEntryDto> Metadata);

/// <summary>One document as the index holds it: its overview, when it was last indexed, and every passage.</summary>
public sealed record DocumentDetailDto(
    string DocumentId,
    string FileName,
    string Folder,
    string? Overview,
    string? IndexedUtc,
    IReadOnlyList<PassageDto> Passages);

/// <summary>
/// A retrieved passage and why it ranked there. Score and similarity are thousandths. Position is its place
/// in the final list, zero for a close call; similarity and vector rank are null in keyword mode.
/// </summary>
public sealed record TracedHitDto(
    string DocumentId,
    int ChunkIndex,
    string FileName,
    string Text,
    int Score,
    int Position,
    int? Similarity,
    int? VectorRank);

/// <summary>How a search was answered: <c>vector</c> or <c>keyword</c> mode, whether it was reranked, the hits, and close calls.</summary>
public sealed record TraceDto(string Mode, bool Reranked, IReadOnlyList<TracedHitDto> Hits, IReadOnlyList<TracedHitDto> CloseCalls);

/// <summary>One passage on the embedding map. X and Y run from 0 to 10000.</summary>
public sealed record MapPointDto(string DocumentId, string FileName, int ChunkIndex, int X, int Y, string Preview);

/// <summary>
/// The embedding map, or <c>Available</c> false when the project has no embedder. Total counts every passage;
/// the shares are thousandths of the spread each axis shows.
/// </summary>
public sealed record MapDto(bool Available, int Total, int FirstShare, int SecondShare, IReadOnlyList<MapPointDto> Points);

public sealed record DocumentCoverageDto(string DocumentId, string FileName, int Passages, int Characters, bool HasOverview, string? IndexedUtc);

public sealed record CoverageDto(string Mode, string? EmbedModel, bool OverviewsOn, IReadOnlyList<DocumentCoverageDto> Documents);

/// <summary>A test question. FileName names the expected document, when the question has one.</summary>
public sealed record EvalQuestionDto(
    string Id,
    string Question,
    IReadOnlyList<string> Facts,
    string? DocumentId,
    string? FileName,
    bool ExpectNoAnswer,
    string Origin,
    bool Draft,
    string CreatedUtc);

public sealed record AddEvalQuestionRequest(string ProjectId, string Question, IReadOnlyList<string>? Facts, string? DocumentId, bool ExpectNoAnswer, string? Origin);

public sealed record UpdateEvalQuestionRequest(string ProjectId, string Id, string Question, IReadOnlyList<string>? Facts, string? DocumentId, bool ExpectNoAnswer);

public sealed record EvalQuestionIdRequest(string ProjectId, string Id);

public sealed record DraftQuestionsRequest(string ProjectId, int Count);

/// <summary>A chat answer to check: the question, the answer as shown, and the passages it cited, in order.</summary>
public sealed record CheckAnswerRequest(string ProjectId, string Question, string Answer, IReadOnlyList<CitationDto> Sources);

/// <summary>One statement of an answer. Sources are the 1-based numbers of the passages that back it.</summary>
public sealed record ClaimDto(string Text, bool Supported, IReadOnlyList<int> Sources, string Reason);

public sealed record FactVerdictDto(string Fact, bool Stated, string Reason);

public sealed record VerdictDto(IReadOnlyList<ClaimDto> Claims, IReadOnlyList<FactVerdictDto> Facts, bool AnswersQuestion, bool Declines, string Summary);

/// <summary>What a run would send: questions, model calls, the models, and why it cannot start, when it cannot.</summary>
public sealed record RunEstimateDto(int Questions, int Drafts, int Calls, string ChatModel, string JudgeModel, string Mode, string? Blocked);

public sealed record RunSettingDto(string Name, string Value);

/// <summary>A run of the test set: <c>running</c>, <c>done</c>, <c>canceled</c> or <c>failed</c>, with its totals so far.</summary>
public sealed record EvalRunDto(
    string Id,
    string Status,
    string StartedUtc,
    string? FinishedUtc,
    int Total,
    int Completed,
    int Passed,
    int FactsStated,
    int FactsTotal,
    int ClaimsSupported,
    int ClaimsTotal,
    int DocumentsFound,
    int DocumentsExpected,
    IReadOnlyList<RunSettingDto> Settings,
    string? Error);

public sealed record EvalRunIdRequest(string ProjectId, string RunId);

/// <summary>One question's outcome. FoundDocument is null when the question names no document.</summary>
public sealed record EvalResultDto(
    string QuestionId,
    string Question,
    IReadOnlyList<string> Facts,
    bool ExpectNoAnswer,
    string Answer,
    IReadOnlyList<CitationDto> Sources,
    VerdictDto? Verdict,
    bool? FoundDocument,
    bool Passed,
    string? Error);

public sealed record EvalRunDetailDto(EvalRunDto Run, IReadOnlyList<EvalResultDto> Results);

public sealed record CompareRunsRequest(string ProjectId, string BeforeId, string AfterId);

public sealed record SettingChangeDto(string Name, string? Before, string? After);

/// <summary>How one question fared: <c>improved</c>, <c>regressed</c>, <c>same</c>, <c>added</c> or <c>removed</c>.</summary>
public sealed record QuestionChangeDto(string QuestionId, string Question, bool? Before, bool? After, string Change);

public sealed record RunComparisonDto(EvalRunDto Before, EvalRunDto After, IReadOnlyList<SettingChangeDto> Settings, IReadOnlyList<QuestionChangeDto> Questions);

[JsonSerializable(typeof(EmptyRequest))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(ProjectDto[]))]
[JsonSerializable(typeof(CreateProjectRequest))]
[JsonSerializable(typeof(RenameProjectRequest))]
[JsonSerializable(typeof(ProjectIdRequest))]
[JsonSerializable(typeof(ImportProjectRequest))]
[JsonSerializable(typeof(ProjectFileDto))]
[JsonSerializable(typeof(SourceDto))]
[JsonSerializable(typeof(SourceDto[]))]
[JsonSerializable(typeof(ProjectIdScoped))]
[JsonSerializable(typeof(AddSourceRequest))]
[JsonSerializable(typeof(FolderPickDto))]
[JsonSerializable(typeof(ModelStatusDto))]
[JsonSerializable(typeof(SourceIdRequest))]
[JsonSerializable(typeof(RescanRequest))]
[JsonSerializable(typeof(DocumentDto))]
[JsonSerializable(typeof(DocumentDto[]))]
[JsonSerializable(typeof(JobSummaryDto))]
[JsonSerializable(typeof(FailureDto))]
[JsonSerializable(typeof(ConversationDto))]
[JsonSerializable(typeof(ConversationDto[]))]
[JsonSerializable(typeof(ConversationIdRequest))]
[JsonSerializable(typeof(MessageDto))]
[JsonSerializable(typeof(MessageDto[]))]
[JsonSerializable(typeof(CitationDto))]
[JsonSerializable(typeof(AskRequest))]
[JsonSerializable(typeof(AskUpdateDto))]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(HitDto))]
[JsonSerializable(typeof(HitDto[]))]
[JsonSerializable(typeof(ProjectSettingsDto))]
[JsonSerializable(typeof(FactDto))]
[JsonSerializable(typeof(FactDto[]))]
[JsonSerializable(typeof(AddFactRequest))]
[JsonSerializable(typeof(UpdateFactRequest))]
[JsonSerializable(typeof(FactIdRequest))]
[JsonSerializable(typeof(UpdateProjectSettingsRequest))]
[JsonSerializable(typeof(ModelDto))]
[JsonSerializable(typeof(ModelDto[]))]
[JsonSerializable(typeof(GlobalSettingsDto))]
[JsonSerializable(typeof(AppearanceDto))]
[JsonSerializable(typeof(FontListDto))]
[JsonSerializable(typeof(OpenDocumentRequest))]
[JsonSerializable(typeof(ShellResult))]
[JsonSerializable(typeof(SetDeepSeekKeyRequest))]
[JsonSerializable(typeof(ChatProviderDto))]
[JsonSerializable(typeof(ModelDownloadStatusDto))]
[JsonSerializable(typeof(DocumentRequest))]
[JsonSerializable(typeof(MetadataEntryDto))]
[JsonSerializable(typeof(PassageDto))]
[JsonSerializable(typeof(DocumentDetailDto))]
[JsonSerializable(typeof(TracedHitDto))]
[JsonSerializable(typeof(TraceDto))]
[JsonSerializable(typeof(MapPointDto))]
[JsonSerializable(typeof(MapDto))]
[JsonSerializable(typeof(DocumentCoverageDto))]
[JsonSerializable(typeof(CoverageDto))]
[JsonSerializable(typeof(EvalQuestionDto))]
[JsonSerializable(typeof(EvalQuestionDto[]))]
[JsonSerializable(typeof(AddEvalQuestionRequest))]
[JsonSerializable(typeof(UpdateEvalQuestionRequest))]
[JsonSerializable(typeof(EvalQuestionIdRequest))]
[JsonSerializable(typeof(DraftQuestionsRequest))]
[JsonSerializable(typeof(CheckAnswerRequest))]
[JsonSerializable(typeof(ClaimDto))]
[JsonSerializable(typeof(FactVerdictDto))]
[JsonSerializable(typeof(VerdictDto))]
[JsonSerializable(typeof(RunEstimateDto))]
[JsonSerializable(typeof(RunSettingDto))]
[JsonSerializable(typeof(EvalRunDto))]
[JsonSerializable(typeof(EvalRunDto[]))]
[JsonSerializable(typeof(EvalRunIdRequest))]
[JsonSerializable(typeof(EvalResultDto))]
[JsonSerializable(typeof(EvalRunDetailDto))]
[JsonSerializable(typeof(CompareRunsRequest))]
[JsonSerializable(typeof(SettingChangeDto))]
[JsonSerializable(typeof(QuestionChangeDto))]
[JsonSerializable(typeof(RunComparisonDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AppJsonContext : JsonSerializerContext;
